using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace JeekRemoteManager.Services;

/// <summary>Loopback listeners shared by the connection panel and owner-scoped MCP calls.</summary>
internal sealed class BackgroundPortForwards : IDisposable
{
    internal static BackgroundPortForwards Shared { get; } = new();

    internal interface ITransport : IDisposable
    {
        bool IsReady { get; }
        int LocalPort { get; }
    }

    internal sealed class UserRequiredException(string reason) : Exception(reason);

    internal sealed record Target(string ConnectionId, string Host, int RemotePort, string RouteIdentity,
        string TreePath = "");
    internal sealed record Overview(string Id, Target Target, bool IsManual, string State, string Error, int LocalPort)
    {
        public bool CanStart => State is "closed" or "failed" || Error == "host_key_requires_user";
        public bool CanStop => State != "closed";
    }

    private sealed class Entry(string? owner, Target target)
    {
        public readonly string Id = Guid.NewGuid().ToString("N");
        public readonly string? Owner = owner;
        public Target Target = target;
        public DateTimeOffset? Expires;
        public bool StoppedByUser;
        public string State = "awaiting_user";
        public string Error = "ssh_connecting";
        public CancellationTokenSource? Cancellation;
        public Task? Dial;
        public TaskCompletionSource? Prompted;
        public ITransport? Transport;
        public int LocalPort;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new();
    private readonly Func<DateTimeOffset> _now;
    private readonly Timer? _timer;
    private bool _disposed;
    internal static readonly TimeSpan Lease = TimeSpan.FromSeconds(120);

    internal BackgroundPortForwards(Func<DateTimeOffset>? now = null, bool startTimer = true)
    {
        _now = now ?? (() => DateTimeOffset.UtcNow);
        if (startTimer)
            _timer = new Timer(_ => Sweep(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    internal static string NormalizeOwner(string owner) =>
        Guid.TryParse(owner, out var id) && id != Guid.Empty
            ? id.ToString("D") : throw new ArgumentException("owner must be a non-empty GUID.");

    internal async Task<JsonObject> EnsureAsync(string owner, Target target, Func<CancellationToken, Action, ITransport> dial) =>
        await StartAsync(NormalizeOwner(owner), target, dial).ConfigureAwait(false);

    internal Task<JsonObject> StartFromManagerAsync(Target target, Func<CancellationToken, Action, ITransport> dial,
        string? resumeId = null) => StartAsync(null, target, dial, resumeId);

    private async Task<JsonObject> StartAsync(string? owner, Target target,
        Func<CancellationToken, Action, ITransport> dial, string? resumeId = null)
    {
        if (target.RemotePort is < 1 or > 65535)
            throw new ArgumentException("remote_loopback_port must be between 1 and 65535.");
        Sweep();
        Entry entry;
        Task pending;
        Task prompted;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (resumeId is not null)
            {
                if (!_entries.TryGetValue(resumeId, out var existing))
                    throw new InvalidOperationException("The background forward no longer exists.");
                entry = existing;
                if (!SameTarget(entry.Target, target) || entry.Target.RouteIdentity != target.RouteIdentity)
                    throw new InvalidOperationException("The saved SSH route has changed.");
                entry.StoppedByUser = false;
            }
            else
            {
                entry = _entries.Values.FirstOrDefault(e => e.Owner == owner && SameTarget(e.Target, target))
                    ?? new Entry(owner, target);
                if (entry.StoppedByUser)
                {
                    Renew(entry);
                    return Snapshot(entry);
                }
                if (entry.State != "closed" && entry.Target.RouteIdentity != target.RouteIdentity)
                    throw new InvalidOperationException("Saved SSH route changed; close this owner's old forward first.");
                _entries[entry.Id] = entry;
            }
            entry.Target = target;
            Renew(entry);
            if (entry.Transport?.IsReady == true)
                return Snapshot(entry);
            if (entry.Dial is null || entry.Dial.IsCompleted)
            {
                Retire(entry, "awaiting_user", "ssh_connecting");
                entry.Cancellation = new CancellationTokenSource();
                var token = entry.Cancellation.Token;
                var promptSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                entry.Prompted = promptSignal;
                entry.Dial = Task.Run(() =>
                {
                    ITransport? transport = null;
                    string state = "failed", error = "ssh_connect_failed";
                    try
                    {
                        transport = dial(token, () =>
                        {
                            lock (_gate)
                            {
                                if (entry.Cancellation?.Token != token || token.IsCancellationRequested)
                                    return;
                                entry.Error = "complete_authentication_in_jrm";
                                promptSignal.TrySetResult();
                            }
                        });
                        if (!transport.IsReady)
                            throw new InvalidOperationException("Listener or SSH is not active.");
                        state = "ready";
                        error = "";
                    }
                    catch (UserRequiredException ex)
                    {
                        transport?.Dispose();
                        transport = null;
                        state = "awaiting_user";
                        error = ex.Message; // Only fixed application reason codes reach this exception.
                    }
                    catch
                    {
                        transport?.Dispose();
                        transport = null;
                        // Do not return server-controlled exception text or credentials.
                    }
                    lock (_gate)
                    {
                        if (entry.Cancellation?.Token == token && !token.IsCancellationRequested && !_disposed)
                        {
                            entry.State = state;
                            entry.Error = error;
                            entry.Transport = transport;
                            entry.LocalPort = transport?.LocalPort ?? 0;
                            transport = null;
                        }
                    }
                    transport?.Dispose(); // A late connect cannot resurrect a closed/expired lease.
                });
            }
            pending = entry.Dial;
            prompted = entry.Prompted!.Task;
        }
        // Like session_open: a GUI challenge/slow dial must not hold an MCP call indefinitely.
        await Task.WhenAny(pending, prompted, Task.Delay(TimeSpan.FromSeconds(10))).ConfigureAwait(false);
        lock (_gate)
        {
            Refresh(entry);
            return Snapshot(entry);
        }
    }

    private static bool SameTarget(Target a, Target b) =>
        a.ConnectionId == b.ConnectionId && a.RemotePort == b.RemotePort
        && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase);

    private void Renew(Entry entry)
    {
        if (entry.Owner is not null)
            entry.Expires = _now() + Lease;
    }

    internal JsonObject Read(string id, string owner, bool close = false)
    {
        owner = NormalizeOwner(owner);
        Sweep();
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out var entry) || entry.Owner != owner)
                throw new InvalidOperationException("No forward belongs to this forward_id and owner.");
            Refresh(entry);
            if (close && !entry.StoppedByUser)
                Retire(entry, "closed", "");
            else if (!close && (entry.State != "closed" || entry.StoppedByUser))
                Renew(entry);
            return Snapshot(entry);
        }
    }

    internal void StopFromManager(string id)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out var entry) || entry.State == "closed")
                return;
            entry.StoppedByUser = entry.Owner is not null;
            Retire(entry, "closed", "stopped_by_user");
        }
    }

    internal Overview[] List()
    {
        Sweep();
        lock (_gate)
            return _entries.Values.Select(e => new Overview(e.Id, e.Target, e.Owner is null,
                e.State, e.Error, e.State == "ready" ? e.LocalPort : 0)).ToArray();
    }

    private void Refresh(Entry entry)
    {
        if (entry.State == "ready" && entry.Transport?.IsReady != true)
            Retire(entry, "failed", "ssh_or_listener_disconnected");
    }

    // Resource cleanup is bounded by SSH.NET's listener shutdown timeout; entries are
    // invalidated before disposal, and their dedicated clients never belong to a tab.
    private static void Retire(Entry entry, string state, string error)
    {
        entry.State = state;
        entry.Error = error;
        entry.Cancellation?.Cancel();
        entry.Cancellation?.Dispose();
        entry.Cancellation = null;
        entry.Dial = null;
        entry.Prompted = null;
        entry.Transport?.Dispose();
        entry.Transport = null;
    }

    private static JsonObject Snapshot(Entry e) => new()
    {
        ["forward_id"] = e.Id,
        ["state"] = e.State,
        ["local_host"] = "127.0.0.1",
        ["local_port"] = e.LocalPort,
        ["remote_host"] = "127.0.0.1",
        ["remote_port"] = e.Target.RemotePort,
        ["host"] = e.Target.Host,
        ["connection_id"] = e.Target.ConnectionId,
        ["host_key_verified"] = e.State == "ready",
        ["error"] = e.Error,
        ["lease_expires_at"] = e.Expires?.ToString("O"),
    };

    internal void Sweep()
    {
        lock (_gate)
        {
            if (_disposed) return;
            foreach (var entry in _entries.Values.ToArray())
            {
                var now = _now();
                if (entry.State != "closed" && entry.Expires <= now)
                    Retire(entry, "closed", "lease_expired");
                else
                    Refresh(entry);
                // An explicit user stop survives the caller's lease. Keep ordinary tombstones briefly.
                if (entry.State == "closed" && !entry.StoppedByUser && entry.Expires + TimeSpan.FromMinutes(10) < now)
                    _entries.Remove(entry.Id);
            }
        }
    }

    internal JsonArray SnapshotForDebug()
    {
        Sweep();
        lock (_gate)
            return new JsonArray(_entries.Values.Select(e => (JsonNode)Snapshot(e)).ToArray());
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _timer?.Dispose();
            foreach (var entry in _entries.Values)
                Retire(entry, "closed", "jrm_stopping");
        }
    }
}
