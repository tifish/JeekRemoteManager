using System.Text.Json.Nodes;
using JeekRemoteManager.Services;

namespace JeekRemoteManager.Tests;

public sealed class BackgroundPortForwardTests
{
    private sealed class Transport(int port) : BackgroundPortForwards.ITransport
    {
        public bool IsReady { get; set; } = true;
        public int LocalPort => port;
        public void Dispose() => IsReady = false;
    }

    private static readonly BackgroundPortForwards.Target Target = new("pp-id", "110.43.37.79", 28190, "pp-route");
    private static string Id(JsonObject r) => r["forward_id"]!.GetValue<string>();
    private static string State(JsonObject r) => r["state"]!.GetValue<string>();

    [Fact]
    public async Task Concurrent_ensure_shares_one_dial_and_owner_cannot_close_another_forward()
    {
        using var manager = new BackgroundPortForwards(startTimer: false);
        var owner = Guid.NewGuid().ToString();
        var other = Guid.NewGuid().ToString();
        var opens = 0;
        var tasks = Enumerable.Range(0, 8).Select(_ => manager.EnsureAsync(owner, Target, (_, _) =>
        {
            Interlocked.Increment(ref opens);
            return new Transport(41001);
        })).ToArray();
        var all = await Task.WhenAll(tasks);
        Assert.Equal(1, opens);
        Assert.Single(all.Select(Id).Distinct());
        var second = await manager.EnsureAsync(other, Target, (_, _) => new Transport(41002));
        var prod = await manager.EnsureAsync(owner, Target with { ConnectionId = "prod-id", Host = "110.43.37.203" },
            (_, _) => new Transport(41003));
        Assert.NotEqual(Id(all[0]), Id(second));
        Assert.Throws<InvalidOperationException>(() => manager.Read(Id(all[0]), other, close: true));
        Assert.Equal("closed", State(manager.Read(Id(all[0]), owner, close: true)));
        Assert.Equal("closed", State(manager.Read(Id(all[0]), owner, close: true)));
        Assert.Equal("ready", State(manager.Read(Id(second), other)));
        Assert.Equal("ready", State(manager.Read(Id(prod), owner)));
    }

    [Fact]
    public async Task Status_renews_lease_expiry_releases_transport_and_does_not_resurrect()
    {
        var now = DateTimeOffset.UtcNow;
        using var manager = new BackgroundPortForwards(() => now, startTimer: false);
        var transport = new Transport(41001);
        var owner = Guid.NewGuid().ToString();
        var result = await manager.EnsureAsync(owner, Target, (_, _) => transport);
        now += TimeSpan.FromSeconds(100);
        Assert.Equal("ready", State(manager.Read(Id(result), owner)));
        now += TimeSpan.FromSeconds(100);
        manager.Sweep();
        Assert.True(transport.IsReady);
        now += TimeSpan.FromSeconds(21);
        Assert.Equal("closed", State(manager.Read(Id(result), owner)));
        Assert.False(transport.IsReady);
        Assert.Equal("lease_expired", manager.Read(Id(result), owner)["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task Disconnect_is_truthful_and_ensure_recovers_with_new_port_and_same_id()
    {
        using var manager = new BackgroundPortForwards(startTimer: false);
        var owner = Guid.NewGuid().ToString();
        var transport = new Transport(41001);
        var first = await manager.EnsureAsync(owner, Target, (_, _) => transport);
        transport.IsReady = false;
        var failed = manager.Read(Id(first), owner);
        Assert.Equal("failed", State(failed));
        Assert.False(failed["host_key_verified"]!.GetValue<bool>());
        var recovered = await manager.EnsureAsync(owner, Target, (_, _) => new Transport(41002));
        Assert.Equal(Id(first), Id(recovered));
        Assert.Equal(41002, recovered["local_port"]!.GetValue<int>());
        Assert.True(recovered["host_key_verified"]!.GetValue<bool>());
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.EnsureAsync(owner,
            Target with { RouteIdentity = "changed-route" }, (_, _) => new Transport(41003)));
    }

    [Fact]
    public async Task Close_during_pending_authentication_disposes_late_connection()
    {
        using var manager = new BackgroundPortForwards(startTimer: false);
        var owner = Guid.NewGuid().ToString();
        using var finish = new ManualResetEventSlim();
        var transport = new Transport(41001);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = await manager.EnsureAsync(owner, Target, (_, prompted) =>
        {
            prompted();
            finish.Wait(TimeSpan.FromSeconds(5));
            return new NotifyingTransport(transport, released);
        });
        Assert.Equal("awaiting_user", State(result));
        manager.Read(Id(result), owner, close: true);
        finish.Set();
        await released.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(transport.IsReady);
        Assert.Equal("closed", State(manager.Read(Id(result), owner)));
    }

    private sealed class NotifyingTransport(Transport inner, TaskCompletionSource disposed) : BackgroundPortForwards.ITransport
    {
        public bool IsReady => inner.IsReady;
        public int LocalPort => inner.LocalPort;
        public void Dispose() { inner.Dispose(); disposed.TrySetResult(); }
    }

    [Fact]
    public async Task Errors_are_sanitized_and_invalid_parameters_do_not_dial()
    {
        using var manager = new BackgroundPortForwards(startTimer: false);
        var owner = Guid.NewGuid().ToString();
        await Assert.ThrowsAsync<ArgumentException>(() => manager.EnsureAsync("wrong", Target,
            (_, _) => throw new Exception("should not dial")));
        await Assert.ThrowsAsync<ArgumentException>(() => manager.EnsureAsync(owner, Target with { RemotePort = 0 },
            (_, _) => throw new Exception("should not dial")));
        var failed = await manager.EnsureAsync(owner, Target, (_, _) => throw new Exception("secret-server-text"));
        Assert.Equal("failed", State(failed));
        Assert.DoesNotContain("secret-server-text", failed.ToJsonString());
        var waiting = await manager.EnsureAsync(owner, Target,
            (_, _) => throw new BackgroundPortForwards.UserRequiredException("host_key_requires_user"));
        Assert.Equal("awaiting_user", State(waiting));
        Assert.Equal(0, waiting["local_port"]!.GetValue<int>());
    }

    [Fact]
    public void Contracts_advertise_exact_fields_and_loopback_is_not_caller_configurable()
    {
        var tools = ProductMcpContract.BuildToolList();
        var ensure = tools.Single(t => t!["name"]!.GetValue<string>() == "port_forward_ensure")!;
        var properties = ensure["inputSchema"]!["properties"]!.AsObject();
        Assert.Equal(new[] { "connection", "expected_host", "owner", "remote_loopback_port" }, properties.Select(p => p.Key).Order().ToArray());
        Assert.Equal(4, ensure["inputSchema"]!["required"]!.AsArray().Count);
        Assert.Contains(tools, t => t!["name"]!.GetValue<string>() == "port_forward_status");
        Assert.Contains(tools, t => t!["name"]!.GetValue<string>() == "port_forward_close");
        Assert.Contains(DebugMcpContract.BuildToolList(), t => t!["name"]!.GetValue<string>() == "background_port_forward_check");
    }
}
