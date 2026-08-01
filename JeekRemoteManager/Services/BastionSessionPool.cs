using System.Security.Cryptography;
using System.Text;
using JeekRemoteManager.Models;

namespace JeekRemoteManager.Services;

/// <summary>A saved logical route reached through one authenticated bastion transport.</summary>
public sealed record BastionRoute(string RouteId, string Name, string LoginCommands)
{
    public static BastionRoute FromConnection(Connection connection) =>
        new(
            string.IsNullOrWhiteSpace(connection.ConnectionId)
                ? Fingerprint($"{connection.Host}\n{connection.Port}\n{connection.Username}\n{connection.LoginCommands}")
                : connection.ConnectionId,
            connection.Name,
            connection.LoginCommands);

    private static string Fingerprint(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

/// <summary>
/// Process-local pool of authenticated SSH transports. Entries are grouped automatically
/// by endpoint, user, and credential identity; no bastion-group setting is persisted.
/// Each entry also tracks the logical target that a newly-opened channel currently inherits.
/// </summary>
public sealed class BastionSessionPool : IDisposable
{
    private static readonly TimeSpan IdleLifetime = TimeSpan.FromHours(8);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(5);

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Timer _sweepTimer;
    private bool _disposed;

    public BastionSessionPool() =>
        _sweepTimer = new Timer(_ => SweepIdle(), null, SweepInterval, SweepInterval);

    /// <summary>Number of live authenticated transports retained by the pool.</summary>
    public int SessionCount
    {
        get
        {
            lock (_gate)
                return _entries.Values.Count(entry => entry.Client.IsConnected);
        }
    }

    /// <summary>Safe diagnostics: route names and endpoint grouping, never credentials.</summary>
    public string Snapshot
    {
        get
        {
            lock (_gate)
            {
                var live = _entries.Values
                    .Where(entry => entry.Client.IsConnected)
                    .Select(entry =>
                        $"{entry.EndpointLabel} => {entry.Route.Name}; "
                        + $"active={entry.ActiveLeases}; idle={DateTime.UtcNow - entry.LastUsedUtc:g}")
                    .ToArray();
                return live.Length == 0 ? "(empty)" : string.Join(Environment.NewLine, live);
            }
        }
    }

    /// <summary>
    /// Tries to borrow an authenticated transport for a structured bastion workflow.
    /// The returned lease owns one client reference and serializes route transitions.
    /// </summary>
    public async Task<BastionSessionLease?> TryAcquireAsync(
        Connection target,
        CancellationToken cancellationToken = default)
    {
        if (!IsEligible(target))
            return null;

        Entry? entry;
        lock (_gate)
        {
            if (_disposed
                || !_entries.TryGetValue(BuildKey(target), out entry)
                || !entry.Client.IsConnected
                || !entry.Client.TryAddRef())
            {
                return null;
            }

            entry.ActiveLeases++;
            entry.LastUsedUtc = DateTime.UtcNow;
        }

        try
        {
            await entry.RouteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            ReleaseBorrow(entry, releaseClient: true, releaseRouteGate: false);
            throw;
        }

        if (!entry.Client.IsConnected)
        {
            ReleaseBorrow(entry, releaseClient: true, releaseRouteGate: true);
            RemoveEntry(entry);
            return null;
        }

        return new BastionSessionLease(this, entry, BastionRoute.FromConnection(target));
    }

    /// <summary>
    /// Retains a successfully logged-in fresh transport. The caller keeps its own reference;
    /// the pool takes one additional reference until expiry or application shutdown.
    /// </summary>
    public bool Register(SharedSshClient client, Connection routeConnection)
    {
        if (_disposed
            || !client.IsConnected
            || !IsEligible(routeConnection)
            || !client.TryAddRef())
        {
            return false;
        }

        var key = BuildKey(routeConnection);
        var entry = new Entry(
            key,
            BuildEndpointLabel(routeConnection),
            client,
            BastionRoute.FromConnection(routeConnection));
        Entry? replaced = null;

        lock (_gate)
        {
            if (_disposed)
            {
                client.Release();
                return false;
            }

            if (_entries.TryGetValue(key, out var existing))
            {
                if (ReferenceEquals(existing.Client, client))
                {
                    existing.Route = entry.Route;
                    existing.LastUsedUtc = DateTime.UtcNow;
                    client.Release(); // the pool already owns a reference
                    return true;
                }

                if (existing.Client.IsConnected)
                {
                    // A concurrent fresh login won the race. Keep one deterministic pool
                    // entry; this terminal still owns and can use its independent transport.
                    client.Release();
                    return false;
                }

                replaced = existing;
            }

            _entries[key] = entry;
        }

        replaced?.Client.Release();
        return true;
    }

    public void Dispose()
    {
        Entry[] entries;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            entries = _entries.Values.ToArray();
            _entries.Clear();
        }

        _sweepTimer.Dispose();
        foreach (var entry in entries)
            entry.Client.Release();
    }

    private void Complete(BastionSessionLease lease)
    {
        lock (_gate)
        {
            if (!_disposed && _entries.TryGetValue(lease.Entry.Key, out var current)
                           && ReferenceEquals(current, lease.Entry))
            {
                current.Route = lease.TargetRoute;
                current.LastUsedUtc = DateTime.UtcNow;
            }
        }
    }

    private void Release(BastionSessionLease lease, bool clientTaken)
    {
        if (lease.Abandoned)
            RemoveEntry(lease.Entry);
        else if (lease.Completed)
            Complete(lease);
        ReleaseBorrow(lease.Entry, releaseClient: !clientTaken, releaseRouteGate: true);
    }

    private void ReleaseBorrow(Entry entry, bool releaseClient, bool releaseRouteGate)
    {
        if (releaseRouteGate)
            entry.RouteGate.Release();
        if (releaseClient)
            entry.Client.Release();

        lock (_gate)
        {
            entry.ActiveLeases = Math.Max(0, entry.ActiveLeases - 1);
            entry.LastUsedUtc = DateTime.UtcNow;
        }
    }

    private void SweepIdle()
    {
        List<Entry>? expired = null;
        lock (_gate)
        {
            if (_disposed)
                return;

            var cutoff = DateTime.UtcNow - IdleLifetime;
            foreach (var pair in _entries.ToArray())
            {
                var entry = pair.Value;
                if (entry.ActiveLeases != 0
                    || entry.Client.IsConnected && entry.LastUsedUtc >= cutoff)
                {
                    continue;
                }

                _entries.Remove(pair.Key);
                (expired ??= []).Add(entry);
            }
        }

        if (expired is null)
            return;
        foreach (var entry in expired)
            entry.Client.Release();
    }

    private void RemoveEntry(Entry entry)
    {
        var removed = false;
        lock (_gate)
        {
            if (_entries.TryGetValue(entry.Key, out var current) && ReferenceEquals(current, entry))
            {
                _entries.Remove(entry.Key);
                removed = true;
            }
        }

        if (removed)
            entry.Client.Release(); // pool reference
    }

    private static string BuildKey(Connection connection)
    {
        var host = connection.Host.Trim().TrimEnd('.').ToLowerInvariant();
        var port = connection.Port > 0 ? connection.Port : 22;
        var user = connection.Username.Trim();
        var keyPath = NormalizePath(connection.PrivateKeyPath);
        var passwordIdentity = SecretIdentity(connection.EncryptedPassword);
        var passphraseIdentity = SecretIdentity(connection.EncryptedPrivateKeyPassphrase);
        var material = $"{host}\n{port}\n{user}\n{keyPath}\n{passwordIdentity}\n{passphraseIdentity}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    /// <summary>Safe hash-only identity exposed to SmokeTest/Debug MCP verification.</summary>
    public static string PoolKeyForDebug(Connection connection) => BuildKey(connection);

    private static bool IsEligible(Connection connection) =>
        LoginCommandSequence.HasStructuredReuseWorkflow(connection.LoginCommands)
        && LoginCommandSequence.Validate(connection.LoginCommands).Count == 0;

    private static string BuildEndpointLabel(Connection connection)
    {
        var host = connection.Host.Trim();
        var port = connection.Port > 0 ? connection.Port : 22;
        var user = connection.Username.Trim();
        return $"{(user.Length == 0 ? "" : user + "@")}{host}:{port}";
    }

    private static string SecretIdentity(string encrypted)
    {
        if (string.IsNullOrEmpty(encrypted))
            return "(none)";
        var value = PasswordProtector.TryDecrypt(encrypted, out var clear) ? clear : encrypted;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";
        try
        {
            return Path.GetFullPath(path.Trim()).TrimEnd(Path.DirectorySeparatorChar)
                .ToUpperInvariant();
        }
        catch
        {
            return path.Trim().ToUpperInvariant();
        }
    }

    internal sealed class Entry(
        string key,
        string endpointLabel,
        SharedSshClient client,
        BastionRoute route)
    {
        public string Key { get; } = key;
        public string EndpointLabel { get; } = endpointLabel;
        public SharedSshClient Client { get; } = client;
        public SemaphoreSlim RouteGate { get; } = new(1, 1);
        public BastionRoute Route { get; set; } = route;
        public DateTime LastUsedUtc { get; set; } = DateTime.UtcNow;
        public int ActiveLeases { get; set; }
    }

    /// <summary>A serialized borrow of one pooled transport and its current route state.</summary>
    public sealed class BastionSessionLease : IDisposable
    {
        private readonly BastionSessionPool _owner;
        private int _disposed;
        private bool _clientTaken;

        internal BastionSessionLease(BastionSessionPool owner, Entry entry, BastionRoute targetRoute)
        {
            _owner = owner;
            Entry = entry;
            TargetRoute = targetRoute;
        }

        internal Entry Entry { get; }
        public SharedSshClient Client => Entry.Client;
        public BastionRoute SourceRoute => Entry.Route;
        public BastionRoute TargetRoute { get; }
        public bool RequiresSwitch =>
            !string.Equals(SourceRoute.RouteId, TargetRoute.RouteId, StringComparison.Ordinal);
        public bool Completed { get; private set; }
        public bool Abandoned { get; private set; }

        /// <summary>Marks the channel setup successful and transfers the borrowed client
        /// reference to the terminal or monitor that will continue using it.</summary>
        public SharedSshClient CompleteAndTakeClient()
        {
            Completed = true;
            _clientTaken = true;
            return Client;
        }

        /// <summary>
        /// Keeps the borrowed reference for manual terminal takeover, but removes the pool
        /// entry because a failed transition leaves the transport's default route unknown.
        /// </summary>
        public SharedSshClient AbandonAndTakeClient()
        {
            Abandoned = true;
            _clientTaken = true;
            return Client;
        }

        /// <summary>Invalidates an uncertain route and releases the borrowed client reference.</summary>
        public void Abandon() => Abandoned = true;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            _owner.Release(this, _clientTaken);
        }
    }
}
