using System;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace JeekRemoteManager.Services;

internal static partial class DebugMcpServer
{
    private sealed class ForwardListenerFixture : BackgroundPortForwards.ITransport
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        public bool IsReady { get; private set; } = true;
        public int LocalPort { get; }
        public ForwardListenerFixture()
        {
            _listener.Start();
            LocalPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        }
        public void Dispose() { IsReady = false; _listener.Stop(); }
    }

    private static async Task<JsonObject> BackgroundPortForwardCheckAsync()
    {
        var now = DateTimeOffset.UtcNow;
        using var forwards = new BackgroundPortForwards(() => now, startTimer: false);
        var owner = Guid.NewGuid().ToString();
        var other = Guid.NewGuid().ToString();
        var target = new BackgroundPortForwards.Target("fixture-pp", "fixture.invalid", 28190, "pp");
        var opens = 0;
        ForwardListenerFixture? first = null;
        BackgroundPortForwards.ITransport Dial()
        {
            opens++;
            return new ForwardListenerFixture();
        }
        string Id(JsonObject r) => r["forward_id"]!.GetValue<string>();
        string State(JsonObject r) => r["state"]!.GetValue<string>();
        bool Released(int port)
        {
            try
            {
                using var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                return true;
            }
            catch (SocketException) { return false; }
        }
        var a = await forwards.EnsureAsync(owner, target, (_, _) => first = (ForwardListenerFixture)Dial());
        var repeat = await forwards.EnsureAsync(owner, target, (_, _) => Dial());
        var b = await forwards.EnsureAsync(other, target, (_, _) => Dial());
        var c = await forwards.EnsureAsync(owner, target with { ConnectionId = "fixture-prod", RouteIdentity = "prod" }, (_, _) => Dial());
        var isolated = Id(a) == Id(repeat) && Id(a) != Id(b) && Id(a) != Id(c) && opens == 3;
        var unauthorized = false;
        try { forwards.Read(Id(a), other, close: true); }
        catch (InvalidOperationException) { unauthorized = true; }
        var port = first!.LocalPort;
        var conflict = false;
        try { using var blocked = new TcpListener(IPAddress.Loopback, port); blocked.Start(); }
        catch (SocketException) { conflict = true; }
        first.Dispose();
        var disconnected = State(forwards.Read(Id(a), owner)) == "failed";
        var recovered = await forwards.EnsureAsync(owner, target, (_, _) => Dial());
        var closed = forwards.Read(Id(a), owner, close: true);
        var ownClosed = State(closed) == "closed" && Released(closed["local_port"]!.GetValue<int>())
            && State(forwards.Read(Id(b), other)) == "ready";
        now += TimeSpan.FromSeconds(121);
        forwards.Sweep();
        var expired = State(forwards.Read(Id(b), other)) == "closed"
            && Released(b["local_port"]!.GetValue<int>());
        var passed = isolated && unauthorized && conflict && disconnected && ownClosed && expired
            && State(recovered) == "ready" && a["local_host"]!.GetValue<string>() == "127.0.0.1";
        return ToolText(new JsonObject
        {
            ["passed"] = passed, ["fixture_only"] = true,
            ["idempotent_and_isolated"] = isolated, ["owner_checked"] = unauthorized,
            ["port_conflict_checked"] = conflict, ["disconnect_detected"] = disconnected,
            ["close_released_only_owner"] = ownClosed, ["expired_released"] = expired,
        }.ToJsonString(), isError: !passed);
    }
}
