using System;
using System.Linq;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using JeekRemoteManager.Models;
using JeekRemoteManager.Views;
using JeekTools;
using Renci.SshNet;

namespace JeekRemoteManager.Services;

internal static partial class ProductMcpServer
{
    private static readonly BackgroundPortForwards BackgroundForwards = BackgroundPortForwards.Shared;

    internal static JsonArray PortForwardsForDebug() => BackgroundForwards.SnapshotForDebug();

    private static async Task<JsonObject> PortForwardEnsureAsync(JsonObject args)
    {
        var owner = BackgroundPortForwards.NormalizeOwner(McpHost.RequiredString(args, "owner"));
        var port = args["remote_loopback_port"]?.GetValue<int>()
            ?? throw new ArgumentException("remote_loopback_port is required.");
        if (port is < 1 or > 65535)
            throw new ArgumentException("remote_loopback_port must be between 1 and 65535.");
        var expected = McpHost.RequiredString(args, "expected_host").Trim();
        var path = NormalizeTreePath(McpHost.RequiredString(args, "connection"));
        var plan = await OnUiAsync(() =>
        {
            var (connection, resolved, _) = LoadConnection(path);
            if (!string.Equals(path, resolved, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Use the exact tree path from connection_list.");
            if (!connection.IsSsh || !string.Equals(connection.Host.Trim(), expected, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Saved SSH host does not match expected_host.");
            return SshForwardTransport.Prepare(connection, resolved, port, MainVm.Store.TryLoadById,
                MainWindow.ActivateMainWindow);
        }).ConfigureAwait(false);
        var report = await BackgroundForwards.EnsureAsync(owner, plan.Target, plan.Dial).ConfigureAwait(false);
        return ToolText(report.ToJsonString(PrettyOptions));
    }

    private static Task<JsonObject> PortForwardReadAsync(JsonObject args, bool close)
    {
        var report = BackgroundForwards.Read(McpHost.RequiredString(args, "forward_id"),
            McpHost.RequiredString(args, "owner"), close);
        return Task.FromResult(ToolText(report.ToJsonString(PrettyOptions)));
    }
}

/// <summary>A dedicated transport: no shell channel or saved PortForwards/LoginCommands.</summary>
internal sealed class SshForwardTransport(SshClient client, ForwardedPortLocal forward, SshJumpTunnel? jump)
    : BackgroundPortForwards.ITransport
{
    private int _disposed;
    public bool IsReady => Volatile.Read(ref _disposed) == 0 && client.IsConnected && forward.IsStarted;
    public int LocalPort => (int)forward.BoundPort;

    internal sealed record Plan(BackgroundPortForwards.Target Target,
        Func<CancellationToken, Action, BackgroundPortForwards.ITransport> Dial);

    internal static Plan Prepare(Connection connection, string treePath, int remotePort,
        Func<string, Connection?> resolver, Action activateWindow, int localPort = 0)
    {
        // Capture a route snapshot, never resolve a newly moved/changed target while dialing.
        var route = SshDialer.ResolveJumpRoute(connection, resolver).Append(connection).ToArray();
        var routeIdentity = string.Join("|", route.Select(c =>
            $"{c.ConnectionId}:{c.Host.Trim()}:{c.Port}:{c.Username.Trim()}"));
        var target = new BackgroundPortForwards.Target(connection.ConnectionId, connection.Host.Trim(),
            remotePort, routeIdentity, treePath, localPort);
        return new Plan(target, (token, prompted) =>
        {
            void Activate() => Dispatcher.UIThread.Post(activateWindow);
            // Do this before decrypting credentials. Attach still checks the actual key
            // at every hop, rejecting unknown/changed keys even if the file changes here.
            foreach (var hop in route)
            {
                if (!KnownHostsStore.Default.TryGet(hop.Host, hop.Port, out _))
                {
                    Activate();
                    throw new BackgroundPortForwards.UserRequiredException("host_key_requires_user");
                }
            }
            var keyRejected = false;
            try
            {
                return Open(connection, target,
                    new SshDialOptions(
                        RequireKnownHost: true,
                        OnRejected: _ => { keyRejected = true; Activate(); },
                        PromptUser: challenge =>
                        {
                            prompted();
                            return KeyboardInteractiveDialog.Prompt(challenge, token);
                        }),
                    id => route.FirstOrDefault(c => c.ConnectionId == id), token);
            }
            catch when (keyRejected)
            {
                throw new BackgroundPortForwards.UserRequiredException("host_key_requires_user");
            }
        });
    }

    private static SshForwardTransport Open(Connection connection, BackgroundPortForwards.Target target, SshDialOptions options,
        Func<string, Connection?> resolver, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var (client, jump) = SshDialer.Connect(connection, info => new SshClient(info)
        {
            KeepAliveInterval = TimeSpan.FromSeconds(15),
        }, options, resolver);
        ForwardedPortLocal? forward = null;
        try
        {
            token.ThrowIfCancellationRequested();
            forward = new ForwardedPortLocal("127.0.0.1", (uint)target.RequestedLocalPort, "127.0.0.1", (uint)target.RemotePort);
            client.AddForwardedPort(forward);
            StartListener(forward.Start, target.RequestedLocalPort);
            token.ThrowIfCancellationRequested();
            return new SshForwardTransport(client, forward, jump);
        }
        catch
        {
            try { forward?.Dispose(); } catch { }
            try { client.Dispose(); } catch { }
            try { jump?.Dispose(); } catch { }
            throw;
        }
    }

    internal static void StartListener(Action start, int requestedLocalPort)
    {
        try { start(); }
        catch (SocketException ex) when (requestedLocalPort > 0
            && ex.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
        {
            throw new BackgroundPortForwards.LocalPortUnavailableException(ex);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { forward.Stop(); } catch { }
        try { forward.Dispose(); } catch { }
        try { client.Dispose(); } catch { }
        try { jump?.Dispose(); } catch { }
    }
}
