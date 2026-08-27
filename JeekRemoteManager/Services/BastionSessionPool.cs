using System.Security.Cryptography;
using System.Text;
using JeekRemoteManager.Models;

namespace JeekRemoteManager.Services;

/// <summary>Where a pooled transport was last known to be sitting.</summary>
public enum BastionRoutePosition
{
    /// <summary>At the bastion itself (menu or prompt); no target entered yet.</summary>
    Entry,
    /// <summary>Inside the target this route names.</summary>
    Target,
    /// <summary>Driven somewhere this process no longer tracks.</summary>
    Unknown,
}

/// <summary>A saved logical route reached through one authenticated bastion transport.</summary>
public sealed record BastionRoute(
    string RouteId,
    string Name,
    string LoginCommands,
    BastionRoutePosition Position)
{
    /// <summary>The transport was driven somewhere we lost track of. Not the same as
    /// <see cref="AtEntry"/>: a target may well have been entered.</summary>
    public static BastionRoute Unknown(string loginCommands) =>
        new("", "(unknown)", loginCommands, BastionRoutePosition.Unknown);

    /// <summary>Authenticated but still at the bastion itself. Entering a target is all
    /// the next channel needs; a <c>#reuse-leave</c> here would type into the menu.</summary>
    public static BastionRoute AtEntry(string loginCommands) =>
        new("", "(bastion entry)", loginCommands, BastionRoutePosition.Entry);

    public bool IsKnown => RouteId.Length > 0;

    public static BastionRoute FromConnection(Connection connection) =>
        new(
            string.IsNullOrWhiteSpace(connection.ConnectionId)
                ? Fingerprint($"{connection.Host}\n{connection.Port}\n{connection.Username}\n{connection.EffectiveLoginCommands}")
                : connection.ConnectionId,
            connection.Name,
            connection.EffectiveLoginCommands,
            BastionRoutePosition.Target);

    private static string Fingerprint(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

/// <summary>
/// Process-local pool of authenticated SSH transports. Entries are grouped automatically
/// by endpoint, user, and credential identity; no bastion-group setting is persisted.
/// Each entry also tracks the last known logical target. A newly-opened channel
/// typically starts at the bastion menu, not inside that target. The pool keeps its own
/// reference while at least one terminal or monitor still owns the transport; when the
/// last external reference goes away, the entry is removed instead of surviving into a
/// later, unrelated connection attempt.
/// </summary>
public sealed class BastionSessionPool : IDisposable
{
    private static readonly TimeSpan IdleLifetime = TimeSpan.FromHours(8);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(5);

    private readonly object _gate = new();
    private readonly Dictionary<string, List<Entry>> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskCompletionSource> _freshLogins = new(StringComparer.Ordinal);
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
                return _entries.Values
                    .SelectMany(entries => entries)
                    .Count(entry => entry.Client.IsConnected);
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
                    .SelectMany(entries => entries)
                    .Where(entry => entry.Client.IsConnected)
                    .Select(entry =>
                    {
                        var channelLimit = entry.Client.KnownShellChannelLimit?.ToString() ?? "?";
                        return $"{entry.EndpointLabel} [{entry.Client.SessionId}] => {entry.Route.Name}; "
                               + $"channels={entry.Client.ActiveShellChannelCount}/{channelLimit}; "
                               + $"pending={entry.ActiveLeases}; idle={DateTime.UtcNow - entry.LastUsedUtc:g}";
                    })
                    .ToArray();
                return live.Length == 0 ? "(empty)" : string.Join(Environment.NewLine, live);
            }
        }
    }

    /// <summary>True when a connected authenticated transport is currently available
    /// for this endpoint and credential identity.</summary>
    public bool HasReusableSession(Connection target)
    {
        if (!IsEligible(target))
            return false;

        lock (_gate)
        {
            return !_disposed
                   && _entries.TryGetValue(BuildKey(target), out var entries)
                   && entries.Any(entry =>
                       entry.Client.IsConnected && entry.Client.HasShellChannelCapacity);
        }
    }

    /// <summary>True when this bastion identity has at least one live transport,
    /// including transports whose observed shell-channel limit is currently full.</summary>
    public bool HasKnownSession(Connection target)
    {
        if (!IsEligible(target))
            return false;

        lock (_gate)
        {
            return !_disposed
                   && _entries.TryGetValue(BuildKey(target), out var entries)
                   && entries.Any(entry => entry.Client.IsConnected);
        }
    }

    /// <summary>
    /// How many borrows are already outstanding for this bastion identity — that is,
    /// how many route switches a new borrower may have to queue behind. Route switches
    /// are serialized, so a caller's own patience has to scale with this.
    /// </summary>
    public int PendingBorrowCount(Connection target)
    {
        if (!IsEligible(target))
            return 0;

        lock (_gate)
        {
            return _disposed || !_entries.TryGetValue(BuildKey(target), out var entries)
                ? 0
                : entries.Sum(entry => entry.ActiveLeases);
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

        var key = BuildKey(target);
        var targetRoute = BastionRoute.FromConnection(target);
        var skipped = new HashSet<Entry>();

        while (true)
        {
            Entry? entry;
            lock (_gate)
            {
                if (_disposed || !_entries.TryGetValue(key, out var entries))
                    return null;

                entry = SelectEntry(entries, targetRoute, skipped);
                if (entry is null)
                    return null;
                if (!entry.Client.TryAddRef())
                {
                    skipped.Add(entry);
                    continue;
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

            if (entry.Client.IsConnected && entry.Client.HasShellChannelCapacity)
                return new BastionSessionLease(this, entry, targetRoute);

            skipped.Add(entry);
            var disconnected = !entry.Client.IsConnected;
            ReleaseBorrow(entry, releaseClient: true, releaseRouteGate: true);
            if (disconnected)
                RemoveEntry(entry);
        }
    }

    /// <summary>
    /// Claims the right to authenticate a new transport for this bastion identity, so
    /// other connections wait for it instead of starting a second authentication —
    /// which on a two-factor bastion means a second code for the user. Returns null
    /// when someone else already holds the claim; that caller should wait with
    /// <see cref="WaitForFreshLoginAsync"/> and then borrow from the pool.
    /// </summary>
    public FreshLoginReservation? TryReserveFreshLogin(Connection target)
    {
        if (!IsEligible(target))
            return null;

        var key = BuildKey(target);
        lock (_gate)
        {
            if (_disposed || _freshLogins.ContainsKey(key))
                return null;

            _freshLogins.Add(
                key,
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        }

        return new FreshLoginReservation(this, key);
    }

    /// <summary>True while another connection is authenticating a transport for this
    /// bastion identity.</summary>
    public bool HasPendingFreshLogin(Connection target)
    {
        if (!IsEligible(target))
            return false;

        lock (_gate)
            return !_disposed && _freshLogins.ContainsKey(BuildKey(target));
    }

    /// <summary>
    /// Waits until the in-flight authentication for this identity finishes, however it
    /// finishes. False means there was nothing to wait for.
    /// </summary>
    public async Task<bool> WaitForFreshLoginAsync(
        Connection target,
        CancellationToken cancellationToken = default)
    {
        if (!IsEligible(target))
            return false;

        TaskCompletionSource? pending;
        lock (_gate)
        {
            if (_disposed || !_freshLogins.TryGetValue(BuildKey(target), out pending))
                return false;
        }

        await pending.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private void CompleteFreshLogin(string key)
    {
        TaskCompletionSource? pending;
        lock (_gate)
        {
            if (!_freshLogins.Remove(key, out pending))
                return;
        }

        pending.TrySetResult();
    }

    /// <summary>
    /// Retains an authenticated fresh transport. The caller keeps its own reference;
    /// the pool takes one additional reference until expiry or application shutdown.
    /// <paramref name="route"/> says where the transport actually is: pass
    /// <see cref="BastionRoute.AtEntry"/> right after authentication, because the
    /// target has not been entered yet, and let it default to the connection's own
    /// route only once the login sequence has arrived there.
    /// </summary>
    public bool Register(
        SharedSshClient client,
        Connection routeConnection,
        BastionRoute? route = null)
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
            route ?? BastionRoute.FromConnection(routeConnection));
        List<Entry>? disconnected = null;

        lock (_gate)
        {
            if (_disposed)
            {
                client.Release();
                return false;
            }

            if (!_entries.TryGetValue(key, out var entries))
            {
                entries = [];
                _entries.Add(key, entries);
            }
            else
            {
                var existing = entries.FirstOrDefault(candidate =>
                    ReferenceEquals(candidate.Client, client));
                if (existing is not null)
                {
                    existing.Route = entry.Route;
                    existing.LastUsedUtc = DateTime.UtcNow;
                    client.Release(); // the pool already owns a reference
                    return true;
                }

                foreach (var stale in entries.Where(candidate => !candidate.Client.IsConnected).ToArray())
                {
                    entries.Remove(stale);
                    (disconnected ??= []).Add(stale);
                }
            }

            entries.Add(entry);
        }

        if (disconnected is not null)
        {
            foreach (var stale in disconnected)
                stale.Client.Release();
        }
        return true;
    }

    /// <summary>
    /// Removes connected transports that have no active borrow and no owner outside the
    /// pool. This is called after terminal/monitor teardown so a new connection cannot
    /// switch through a route left behind by the last closed session.
    /// </summary>
    public int ReleaseUnusedSessions()
    {
        List<Entry>? unused = null;
        lock (_gate)
        {
            if (_disposed)
                return 0;

            foreach (var pair in _entries.ToArray())
            {
                foreach (var entry in pair.Value.ToArray())
                {
                    if (entry.ActiveLeases != 0 || entry.Client.ReferenceCount != 1)
                        continue;

                    pair.Value.Remove(entry);
                    (unused ??= []).Add(entry);
                }

                if (pair.Value.Count == 0)
                    _entries.Remove(pair.Key);
            }
        }

        if (unused is null)
            return 0;

        foreach (var entry in unused)
            entry.Client.Release();
        return unused.Count;
    }

    public void Dispose()
    {
        Entry[] entries;
        TaskCompletionSource[] pendingLogins;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            entries = _entries.Values.SelectMany(group => group).ToArray();
            _entries.Clear();
            pendingLogins = _freshLogins.Values.ToArray();
            _freshLogins.Clear();
        }

        // Never leave a waiter parked on a login that can no longer finish.
        foreach (var pending in pendingLogins)
            pending.TrySetResult();

        _sweepTimer.Dispose();
        foreach (var entry in entries)
            entry.Client.Release();
    }

    private void Complete(BastionSessionLease lease)
    {
        lock (_gate)
        {
            if (!_disposed
                && _entries.TryGetValue(lease.Entry.Key, out var entries)
                && entries.Contains(lease.Entry))
            {
                lease.Entry.Route = lease.RouteUncertain
                    ? BastionRoute.Unknown(lease.Entry.Route.LoginCommands)
                    : lease.TargetRoute;
                lease.Entry.LastUsedUtc = DateTime.UtcNow;
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
                foreach (var entry in pair.Value.ToArray())
                {
                    if (entry.ActiveLeases != 0
                        || entry.Client.IsConnected
                        && !entry.Client.IsShellChannelExhausted
                        && entry.LastUsedUtc >= cutoff)
                    {
                        continue;
                    }

                    pair.Value.Remove(entry);
                    (expired ??= []).Add(entry);
                }

                if (pair.Value.Count == 0)
                    _entries.Remove(pair.Key);
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
            if (_entries.TryGetValue(entry.Key, out var entries) && entries.Remove(entry))
            {
                if (entries.Count == 0)
                    _entries.Remove(entry.Key);
                removed = true;
            }
        }

        if (removed)
            entry.Client.Release(); // pool reference
    }

    private static Entry? SelectEntry(
        IEnumerable<Entry> entries,
        BastionRoute targetRoute,
        IReadOnlySet<Entry> skipped) =>
        entries
            .Where(entry =>
                !skipped.Contains(entry)
                && entry.Client.IsConnected
                && entry.Client.HasShellChannelCapacity)
            .OrderBy(entry => entry.RouteGate.CurrentCount == 0)
            .ThenByDescending(entry =>
                string.Equals(entry.Route.RouteId, targetRoute.RouteId, StringComparison.Ordinal))
            .ThenBy(entry => entry.Client.ActiveShellChannelCount)
            .ThenBy(entry => entry.LastUsedUtc)
            .FirstOrDefault();

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
        LoginCommandSequence.HasStructuredReuseWorkflow(connection.EffectiveLoginCommands)
        && LoginCommandSequence.Validate(connection.EffectiveLoginCommands).Count == 0;

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

    /// <summary>
    /// One in-flight authentication for a bastion identity. Disposing it releases the
    /// connections waiting behind it — do that as soon as the transport is pooled and
    /// borrowable, and unconditionally when the attempt ends.
    /// </summary>
    public sealed class FreshLoginReservation(BastionSessionPool owner, string key) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            owner.CompleteFreshLogin(key);
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

        /// <summary>
        /// What a new channel on this transport has to run before it is inside the
        /// wanted target. Only a route that is both known and identical may skip
        /// straight to the post-arrival commands.
        /// </summary>
        public BastionReuseStart ReuseStart =>
            SourceRoute.Position switch
            {
                BastionRoutePosition.Entry => BastionReuseStart.Enter,
                BastionRoutePosition.Target when string.Equals(
                    SourceRoute.RouteId,
                    TargetRoute.RouteId,
                    StringComparison.Ordinal) => BastionReuseStart.Duplicate,
                // Another target, or one we lost track of. Assuming "already there"
                // would hand the user a shell on the previous machine under the new
                // tab's name, so leave first and enter again.
                _ => BastionReuseStart.Switch,
            };

        public bool RequiresSwitch => ReuseStart == BastionReuseStart.Switch;
        public bool Completed { get; private set; }
        public bool Abandoned { get; private set; }
        public bool RouteUncertain { get; private set; }

        /// <summary>Marks the channel setup successful and transfers the borrowed client
        /// reference to the terminal or monitor that will continue using it.</summary>
        public SharedSshClient CompleteAndTakeClient()
        {
            Completed = true;
            _clientTaken = true;
            return Client;
        }

        /// <summary>
        /// Keeps the borrowed reference for manual terminal takeover and retains the
        /// authenticated transport. The current route is marked unknown so the next
        /// borrower will not send #reuse-leave at the bastion menu.
        /// </summary>
        public SharedSshClient KeepAndTakeClient()
        {
            Completed = true;
            RouteUncertain = true;
            _clientTaken = true;
            return Client;
        }

        /// <summary>
        /// Keeps the borrowed reference for manual terminal takeover, but removes the pool
        /// entry because the transport itself is no longer usable.
        /// </summary>
        public SharedSshClient AbandonAndTakeClient()
        {
            Abandoned = true;
            _clientTaken = true;
            return Client;
        }

        /// <summary>
        /// Releases the borrowed reference but keeps the authenticated transport pooled,
        /// with its route marked unknown. This is what a borrower that failed or was
        /// cancelled owes the next one: the transport is still authenticated, and
        /// dropping it would cost the next connection another two-factor login.
        /// </summary>
        public void KeepAndRelease()
        {
            Completed = true;
            RouteUncertain = true;
        }

        /// <summary>
        /// Drops the pool entry. Only for a transport that is no longer usable —
        /// a failed borrow is <see cref="KeepAndRelease"/>, not this.
        /// </summary>
        public void Abandon() => Abandoned = true;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            _owner.Release(this, _clientTaken);
        }
    }
}
