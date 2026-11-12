using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace JeekRemoteManager.Services;

/// <summary>Owner-scoped loopback listeners. No terminal, HTTP interpretation or request replay.</summary>
internal sealed class BackgroundPortForwards : IDisposable
{
    internal interface ITransport : IDisposable
    {
        bool IsReady { get; }
        int LocalPort { get; }
    }

    internal sealed class UserRequiredException(string reason) : Exception(reason);

    internal sealed record Target(string ConnectionId, string Host, int RemotePort, string RouteIdentity);

    private sealed class Entry(string owner, Target target, DateTimeOffset expires)
    {
        public readonly string Id = Guid.NewGuid().ToString("N");
        public readonly string Owner = owner;
        public readonly Target Target = target;
        public DateTimeOffset Expires = expires;
        public string State = "awaiting_user";
        public string Error = "ssh_connecting";
        public CancellationTokenSource? Cancellation;
        public Task? Dial;
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

    internal async Task<JsonObject> EnsureAsync(string owner, Target target,
        Func<CancellationToken, Action, ITransport> dial)
    {
        owner = NormalizeOwner(owner);
        if (target.RemotePort is < 1 or > 65535)
            throw new ArgumentException("remote_loopback_port must be between 1 and 65535.");
        Sweep();
        Entry entry;
        Task pending;
        var prompted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            entry = _entries.Values.FirstOrDefault(e => e.Owner == owner
                && e.Target.ConnectionId == target.ConnectionId
                && string.Equals(e.Target.Host, target.Host, StringComparison.OrdinalIgnoreCase)
                && e.Target.RemotePort == target.RemotePort)
                ?? new Entry(owner, target, _now() + Lease);
            if (entry.Target.RouteIdentity != target.RouteIdentity)
                throw new InvalidOperationException("Saved SSH route changed; close this owner's old forward first.");
            _entries[entry.Id] = entry;
            entry.Expires = _now() + Lease;
            if (entry.Transport?.IsReady == true)
                return Snapshot(entry);
            if (entry.Dial is null || entry.Dial.IsCompleted)
            {
                Retire(entry, "awaiting_user", "ssh_connecting");
                entry.Cancellation = new CancellationTokenSource();
                var token = entry.Cancellation.Token;
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
                                prompted.TrySetResult();
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
        }
        // Like session_open: a GUI challenge/slow dial must not hold an MCP call indefinitely.
        await Task.WhenAny(pending, prompted.Task, Task.Delay(TimeSpan.FromSeconds(10))).ConfigureAwait(false);
        lock (_gate)
        {
            Refresh(entry);
            return Snapshot(entry);
        }
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
            if (close)
                Retire(entry, "closed", "");
            else if (entry.State != "closed")
                entry.Expires = _now() + Lease;
            return Snapshot(entry);
        }
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
        ["lease_expires_at"] = e.Expires.ToString("O"),
    };

    internal void Sweep()
    {
        lock (_gate)
        {
            if (_disposed) return;
            foreach (var entry in _entries.Values.ToArray())
            {
                if (entry.State != "closed" && entry.Expires <= _now())
                    Retire(entry, "closed", "lease_expired");
                else
                    Refresh(entry);
                // Keep tombstones briefly for idempotent close/status without unbounded growth.
                if (entry.State == "closed" && entry.Expires + TimeSpan.FromMinutes(10) < _now())
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
