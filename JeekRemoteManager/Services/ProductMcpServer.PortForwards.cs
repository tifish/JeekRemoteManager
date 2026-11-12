using System;
using System.Linq;
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
    private static readonly BackgroundPortForwards BackgroundForwards = new();

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
        var saved = await OnUiAsync(() =>
        {
            var (connection, resolved, _) = LoadConnection(path);
            if (!string.Equals(path, resolved, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Use the exact tree path from connection_list.");
            if (!connection.IsSsh || !string.Equals(connection.Host.Trim(), expected, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Saved SSH host does not match expected_host.");
            var route = SshDialer.ResolveJumpRoute(connection, MainVm.Store.TryLoadById).Append(connection).ToArray();
            return (Connection: connection, Route: route);
        }).ConfigureAwait(false);
        // Capture a route snapshot, never resolve a newly moved/changed target while dialing.
        var routeIdentity = string.Join("|", saved.Route.Select(c =>
            $"{c.ConnectionId}:{c.Host.Trim()}:{c.Port}:{c.Username.Trim()}"));
        var target = new BackgroundPortForwards.Target(saved.Connection.ConnectionId, expected, port, routeIdentity);
        var report = await BackgroundForwards.EnsureAsync(owner, target, (token, prompted) =>
        {
            void Activate() => Dispatcher.UIThread.Post(() =>
            {
                MainWindow.Show();
                MainWindow.Activate();
            });
            // Do this before decrypting credentials. Attach still checks the actual key
            // at every hop, rejecting unknown/changed keys even if the file changes here.
            foreach (var hop in saved.Route)
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
                return SshForwardTransport.Open(saved.Connection, port,
                    new SshDialOptions(
                        RequireKnownHost: true,
                        OnRejected: _ => { keyRejected = true; Activate(); },
                        PromptUser: challenge =>
                        {
                            prompted();
                            return KeyboardInteractiveDialog.Prompt(challenge, token);
                        }),
                    id => saved.Route.FirstOrDefault(c => c.ConnectionId == id), token);
            }
            catch when (keyRejected)
            {
                throw new BackgroundPortForwards.UserRequiredException("host_key_requires_user");
            }
        }).ConfigureAwait(false);
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

    internal static SshForwardTransport Open(Connection connection, int port, SshDialOptions options,
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
            forward = new ForwardedPortLocal("127.0.0.1", 0, "127.0.0.1", (uint)port);
            client.AddForwardedPort(forward);
            forward.Start();
            token.ThrowIfCancellationRequested();
            return new SshForwardTransport(client, forward, jump);
        }
        catch
        {
            forward?.Dispose();
            client.Dispose();
            jump?.Dispose();
            throw;
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
