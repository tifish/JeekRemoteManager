using System;
using JeekRemoteManager.Models;
using Renci.SshNet;

namespace JeekRemoteManager.Services;

/// <summary>
/// Everything a dial needs from its caller, passed explicitly rather than read from
/// process-wide settable state: what to do with host keys, which known-hosts store to
/// check them against, and who answers keyboard-interactive prompts (OTP, extra PAM
/// fields) that the stored password cannot.
/// </summary>
/// <param name="OnMismatch">(host, port, keyType, saved, presented) =&gt; replace? — null rejects a changed key.</param>
/// <param name="PromptUser">Answers a keyboard-interactive challenge; null fails such prompts.</param>
/// <param name="KnownHosts">Store to verify against; null = <see cref="KnownHostsStore.Default"/>.</param>
public sealed record SshDialOptions(
    Func<string, int, string, string, string, bool>? OnMismatch = null,
    Action<string>? OnRejected = null,
    Action<string>? OnTrusted = null,
    Func<SshConnectionFactory.KeyboardInteractiveChallenge, string?>? PromptUser = null,
    KnownHostsStore? KnownHosts = null)
{
    public KnownHostsStore Store => KnownHosts ?? KnownHostsStore.Default;
}

/// <summary>
/// A connection to a jump host with a local port forwarded to the real target — the
/// ProxyJump equivalent. SSH.NET cannot run a session over another session's channel, so
/// the target is dialed at 127.0.0.1:<see cref="LocalPort"/>, which the jump host relays.
/// Must outlive the client dialed through it.
/// </summary>
public sealed class SshJumpTunnel : IDisposable
{
    private readonly SshClient _jumpClient;
    private readonly ForwardedPortLocal _forward;
    private readonly SshJumpTunnel? _upstream;

    private SshJumpTunnel(SshClient jumpClient, ForwardedPortLocal forward, SshJumpTunnel? upstream)
    {
        _jumpClient = jumpClient;
        _forward = forward;
        _upstream = upstream;
    }

    public string JumpHost => _jumpClient.ConnectionInfo.Host;

    public int LocalPort => (int)_forward.BoundPort;

    /// <summary>Loopback listeners owned by this route, exposed for Debug MCP cleanup checks.</summary>
    internal System.Collections.Generic.IReadOnlyList<int> LocalPortsForDebug =>
        _upstream is null ? [LocalPort] : [LocalPort, .. _upstream.LocalPortsForDebug];

    public static SshJumpTunnel Open(
        Connection jump,
        string targetHost,
        int targetPort,
        SshDialOptions options) => Open([jump], targetHost, targetPort, options);

    /// <summary>Opens a validated route in network order, from the outermost jump inward.</summary>
    public static SshJumpTunnel Open(
        System.Collections.Generic.IReadOnlyList<Connection> jumps,
        string targetHost,
        int targetPort,
        SshDialOptions options)
    {
        if (jumps.Count == 0)
            throw new ArgumentException("A jump route must contain at least one SSH connection.", nameof(jumps));
        SshJumpTunnel? tunnel = null;
        try
        {
            for (var i = 0; i < jumps.Count; i++)
            {
                var nextHost = i + 1 < jumps.Count ? jumps[i + 1].Host.Trim() : targetHost;
                var nextPort = i + 1 < jumps.Count ? (jumps[i + 1].Port > 0 ? jumps[i + 1].Port : 22) : targetPort;
                tunnel = OpenHop(jumps[i], nextHost, nextPort, options, tunnel);
            }
            return tunnel!;
        }
        catch
        {
            tunnel?.Dispose();
            throw;
        }
    }

    private static SshJumpTunnel OpenHop(
        Connection jump, string targetHost, int targetPort, SshDialOptions options, SshJumpTunnel? upstream)
    {
        var jumpHost = jump.Host.Trim();
        var jumpPort = jump.Port > 0 ? jump.Port : 22;
        var client = new SshClient(upstream is null
            ? SshConnectionFactory.Build(jump, options.PromptUser)
            : SshConnectionFactory.Build(jump, options.PromptUser, "127.0.0.1", upstream.LocalPort));
        try
        {
            SshHostKey.Attach(client, jumpHost, jumpPort, options);
            client.KeepAliveInterval = TimeSpan.FromSeconds(30);
            client.Connect();

            // Port 0: the OS picks a free loopback port, read back after Start.
            var forward = new ForwardedPortLocal("127.0.0.1", 0, targetHost, (uint)targetPort);
            client.AddForwardedPort(forward);
            forward.Start();
            return new SshJumpTunnel(client, forward, upstream);
        }
        catch
        {
            try { client.Dispose(); } catch { /* ignore */ }
            throw;
        }
    }

    public void Dispose()
    {
        try { _forward.Stop(); } catch { /* ignore */ }
        try { _forward.Dispose(); } catch { /* ignore */ }
        try { _jumpClient.Disconnect(); } catch { /* ignore */ }
        try { _jumpClient.Dispose(); } catch { /* ignore */ }
        _upstream?.Dispose();
    }
}

/// <summary>
/// The one way to dial a connection: builds its credentials, goes through its jump host
/// when it names one, verifies the host key against the real target (never the loopback
/// end of a tunnel), and connects. Terminal, file browser and key installer all come
/// through here, so none of them can skip host-key verification or ignore the jump host.
/// </summary>
public static class SshDialer
{
    /// <param name="resolveConnection">Looks a saved connection up by its stable ConnectionId;
    /// needed only when the connection names a jump host.</param>
    /// <param name="configure">Last-chance hook on the connection info before connecting.</param>
    /// <returns>The connected client and the jump tunnel it depends on (null when direct);
    /// the caller disposes the tunnel after the client.</returns>
    public static (TClient Client, SshJumpTunnel? Tunnel) Connect<TClient>(
        Connection connection,
        Func<ConnectionInfo, TClient> createClient,
        SshDialOptions options,
        Func<string, Connection?>? resolveConnection = null,
        Action<ConnectionInfo>? configure = null)
        where TClient : BaseClient
    {
        var host = connection.Host.Trim();
        var port = connection.Port > 0 ? connection.Port : 22;
        SshJumpTunnel? tunnel = null;
        TClient? client = null;
        try
        {
            ConnectionInfo info;
            var jumps = ResolveJumpRoute(connection, resolveConnection);
            if (jumps.Count > 0)
            {
                tunnel = SshJumpTunnel.Open(jumps, host, port, options);
                info = SshConnectionFactory.Build(connection, options.PromptUser, "127.0.0.1", tunnel.LocalPort);
            }
            else
            {
                info = SshConnectionFactory.Build(connection, options.PromptUser);
            }

            configure?.Invoke(info);
            client = createClient(info);
            SshHostKey.Attach(client, host, port, options);
            client.Connect();
            return (client, tunnel);
        }
        catch
        {
            try { client?.Dispose(); } catch { /* ignore */ }
            tunnel?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The saved connection named as this one's immediate jump host, or null for a direct dial.
    /// </summary>
    /// <exception cref="InvalidOperationException">The jump host is named but cannot be used.</exception>
    public static Connection? ResolveJump(Connection connection, Func<string, Connection?>? resolveConnection)
    {
        var id = connection.JumpHostConnectionId.Trim();
        if (id.Length == 0)
            return null;
        if (resolveConnection is null)
            throw new InvalidOperationException($"Jump host '{id}' cannot be resolved here.");
        var jump = resolveConnection(id)
            ?? throw new InvalidOperationException($"Jump host '{id}' is not a saved connection.");
        if (!jump.IsSsh)
            throw new InvalidOperationException($"Jump host '{id}' is not an SSH connection.");
        if (!string.Equals(id, jump.ConnectionId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Jump host '{id}' no longer matches its saved identity.");
        if (string.Equals(jump.ConnectionId, connection.ConnectionId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A connection cannot be its own jump host.");
        return jump;
    }

    /// <summary>Resolves every jump by stable id before opening sockets, rejecting loops
    /// and missing hops. Returned in network order (outermost jump first).</summary>
    public static System.Collections.Generic.IReadOnlyList<Connection> ResolveJumpRoute(
        Connection connection, Func<string, Connection?>? resolveConnection)
    {
        var route = new System.Collections.Generic.List<Connection>();
        var ids = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (connection.ConnectionId.Length > 0)
            ids.Add(connection.ConnectionId);
        var current = connection;
        while (current.JumpHostConnectionId.Trim().Length > 0)
        {
            var id = current.JumpHostConnectionId.Trim();
            if (!ids.Add(id))
                throw new InvalidOperationException($"Jump host cycle detected at '{id}'.");
            if (route.Count >= 32)
                throw new InvalidOperationException("Jump host route exceeds the maximum of 32 hops.");
            var jump = ResolveJump(current, resolveConnection)!;
            route.Add(jump);
            current = jump;
        }
        route.Reverse();
        return route;
    }
}
