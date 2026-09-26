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
/// <param name="OnMismatch">(keyType, saved, presented) =&gt; replace? — null rejects a changed key.</param>
/// <param name="PromptUser">Answers a keyboard-interactive challenge; null fails such prompts.</param>
/// <param name="KnownHosts">Store to verify against; null = <see cref="KnownHostsStore.Default"/>.</param>
public sealed record SshDialOptions(
    Func<string, string, string, bool>? OnMismatch = null,
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

    private SshJumpTunnel(SshClient jumpClient, ForwardedPortLocal forward)
    {
        _jumpClient = jumpClient;
        _forward = forward;
    }

    public string JumpHost => _jumpClient.ConnectionInfo.Host;

    public int LocalPort => (int)_forward.BoundPort;

    public static SshJumpTunnel Open(
        Connection jump,
        string targetHost,
        int targetPort,
        SshDialOptions options)
    {
        var jumpHost = jump.Host.Trim();
        var jumpPort = jump.Port > 0 ? jump.Port : 22;
        var client = new SshClient(SshConnectionFactory.Build(jump, options.PromptUser));
        try
        {
            SshHostKey.Attach(client, jumpHost, jumpPort, options);
            client.KeepAliveInterval = TimeSpan.FromSeconds(30);
            client.Connect();

            // Port 0: the OS picks a free loopback port, read back after Start.
            var forward = new ForwardedPortLocal("127.0.0.1", 0, targetHost, (uint)targetPort);
            client.AddForwardedPort(forward);
            forward.Start();
            return new SshJumpTunnel(client, forward);
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
    /// <param name="resolveConnection">Looks a saved connection up by tree path ("vps/bwg");
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
            if (ResolveJump(connection, resolveConnection) is { } jump)
            {
                tunnel = SshJumpTunnel.Open(jump, host, port, options);
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
    /// The saved connection named as this one's jump host, or null for a direct dial. Only
    /// one hop: the jump host's own jump setting is not followed.
    /// </summary>
    /// <exception cref="InvalidOperationException">The jump host is named but cannot be used.</exception>
    public static Connection? ResolveJump(Connection connection, Func<string, Connection?>? resolveConnection)
    {
        var path = connection.JumpHost.Trim();
        if (path.Length == 0)
            return null;
        if (resolveConnection is null)
            throw new InvalidOperationException($"Jump host '{path}' cannot be resolved here.");

        var jump = resolveConnection(path)
                   ?? throw new InvalidOperationException($"Jump host '{path}' is not a saved connection.");
        if (!jump.IsSsh)
            throw new InvalidOperationException($"Jump host '{path}' is not an SSH connection.");
        if (string.Equals(jump.ConnectionId, connection.ConnectionId, StringComparison.Ordinal)
            && connection.ConnectionId.Length > 0)
        {
            throw new InvalidOperationException("A connection cannot be its own jump host.");
        }

        return jump;
    }
}
