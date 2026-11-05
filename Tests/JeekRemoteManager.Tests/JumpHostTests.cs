using JeekRemoteManager.Models;
using JeekRemoteManager.Services;
using JeekRemoteManager.ViewModels;

namespace JeekRemoteManager.Tests;

public class JumpHostTests
{
    [Fact]
    public void Resolves_nested_jumps_in_network_order_without_accessing_credentials()
    {
        var outer = new Connection { ConnectionId = "outer", EncryptedPassword = "unreadable" };
        var inner = new Connection { ConnectionId = "inner", JumpHost = "outer" };
        var target = new Connection { ConnectionId = "target", JumpHost = "inner" };
        var saved = new Dictionary<string, Connection> { ["outer"] = outer, ["inner"] = inner };

        Assert.Equal(new[] { outer, inner }, SshDialer.ResolveJumpRoute(target, path => saved[path]));
        Assert.Empty(SshDialer.ResolveJumpRoute(new Connection(), null));
    }

    [Fact]
    public void Rejects_indirect_cycles_even_when_the_resolver_loads_new_objects()
    {
        var target = new Connection { ConnectionId = "target", JumpHost = "inner" };
        var error = Assert.Throws<InvalidOperationException>(() => SshDialer.ResolveJumpRoute(target,
            path => path == "inner"
                ? new Connection { ConnectionId = "inner", JumpHost = "target-alias" }
                : new Connection { ConnectionId = "target" }));

        Assert.Contains("cycle", error.Message);
        Assert.Contains("target-alias", error.Message);
    }

    [Fact]
    public void Rejects_cycles_without_ids_and_unbounded_resolvers()
    {
        var target = new Connection { JumpHost = "a" };
        Assert.Contains("cycle", Assert.Throws<InvalidOperationException>(() => SshDialer.ResolveJumpRoute(target,
            path => new Connection { JumpHost = path == "a" ? "b" : "A" })).Message);
        var number = 0;
        Assert.Contains("32 hops", Assert.Throws<InvalidOperationException>(() => SshDialer.ResolveJumpRoute(target,
            _ => new Connection { JumpHost = (++number).ToString() })).Message);
    }

    [Theory]
    [InlineData(false, "not a saved connection")]
    [InlineData(true, "not an SSH connection")]
    public void Validates_the_entire_route_before_constructing_a_client(bool nonSsh, string expected)
    {
        var created = false;
        var target = new Connection { Host = "unused", JumpHost = "inner" };
        var error = Assert.Throws<InvalidOperationException>(() => SshDialer.Connect(target, info =>
        {
            created = true;
            return new Renci.SshNet.SshClient(info);
        }, new SshDialOptions(), path => path == "inner"
            ? new Connection { JumpHost = "broken" }
            : nonSsh ? new Connection { Type = ConnectionType.Vnc } : null));

        Assert.False(created);
        Assert.Contains(expected, error.Message);
        Assert.Contains("broken", error.Message);
    }

    [Fact]
    public void Tree_drop_validates_the_route_and_preserves_the_old_value_on_rejection()
    {
        var editor = ConnectionEditorViewModel.FromConnection(new Connection { ConnectionId = "self", JumpHost = "missing" });
        var saved = new Dictionary<string, Connection>
        {
            ["self"] = new() { ConnectionId = "self" },
            ["vps/outer"] = new() { ConnectionId = "outer" },
            ["vps/inner"] = new() { ConnectionId = "inner", JumpHost = "vps/outer" },
            ["desktop"] = new() { Type = ConnectionType.Rdp },
        };
        editor.SetJumpHostConnections(saved);
        editor.SetJumpHostConnections(saved);
        Assert.Equal("missing", editor.JumpHost);
        Assert.True(editor.HasJumpHostValidationMessage);
        Assert.True(editor.TrySetJumpHostFromTree("VPS\\inner.json"));
        Assert.False(editor.HasJumpHostValidationMessage);
        Assert.Equal("VPS/inner", editor.JumpHost);
        Assert.False(editor.TrySetJumpHostFromTree("desktop"));
        Assert.False(editor.TrySetJumpHostFromTree("self"));
        Assert.Equal("VPS/inner", editor.JumpHost);
        saved["vps/outer"].JumpHost = "self";
        editor.SetJumpHostConnections(saved);
        Assert.False(editor.TrySetJumpHostFromTree("vps/outer"));
        Assert.Equal("VPS/inner", editor.JumpHost);
        Assert.Contains("cycle", editor.JumpHostValidationMessage);
        editor.JumpHost = "";
        Assert.False(editor.HasJumpHostValidationMessage);
        Assert.False(editor.HasJumpHost);
    }
}
