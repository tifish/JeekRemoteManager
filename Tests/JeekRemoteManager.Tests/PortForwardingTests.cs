using JeekRemoteManager.Models;
using JeekRemoteManager.Services;

namespace JeekRemoteManager.Tests;

public class PortForwardingTests
{
    [Fact]
    public void Parses_all_three_kinds_and_both_local_forms()
    {
        var specs = SshPortForwarding.Parse(
            "L 8080 db:5432\nL 0.0.0.0:8081:db:5432 # ssh -L form\n\nR 9000 localhost:3000\nD 1080\n");

        Assert.Equal(4, specs.Count);
        Assert.Equal(new PortForwardSpec('L', "127.0.0.1", 8080, "db", 5432), specs[0]);
        Assert.Equal(new PortForwardSpec('L', "0.0.0.0", 8081, "db", 5432), specs[1]);
        Assert.Equal(new PortForwardSpec('R', "127.0.0.1", 9000, "localhost", 3000), specs[2]);
        Assert.Equal(new PortForwardSpec('D', "127.0.0.1", 1080, "", 0), specs[3]);
    }

    [Theory]
    [InlineData("L 8080")]
    [InlineData("X 1 a:2")]
    [InlineData("L 70000 a:1")]
    [InlineData("L 8080 5432")]
    [InlineData("D 1080 extra")]
    [InlineData("-")]
    [InlineData("--")]
    [InlineData("- 8080 db:5432")]
    public void Rejects_malformed_lines_with_the_line_number(string line)
    {
        var message = SshPortForwarding.Validate("D 1080\n" + line);

        Assert.NotNull(message);
        Assert.StartsWith("Port forwarding line 2:", message);
    }

    [Fact]
    public void Listeners_never_default_to_all_interfaces()
    {
        Assert.All(
            SshPortForwarding.Parse("L 1 a:1\nR 2 b:2\nD 3"),
            spec => Assert.Equal("127.0.0.1", spec.BindHost));
    }

    [Fact]
    public void Jump_host_errors_name_the_problem()
    {
        var connection = new Connection { ConnectionId = "a", Host = "h", Username = "u", JumpHost = "vps/jump" };

        Assert.Null(SshDialer.ResolveJump(new Connection { Host = "h" }, null));
        Assert.Contains("not a saved connection",
            Assert.Throws<InvalidOperationException>(() => SshDialer.ResolveJump(connection, _ => null)).Message);
        Assert.Contains("not an SSH connection",
            Assert.Throws<InvalidOperationException>(() => SshDialer.ResolveJump(
                connection, _ => new Connection { Type = ConnectionType.Rdp })).Message);
        Assert.Contains("its own jump host",
            Assert.Throws<InvalidOperationException>(() => SshDialer.ResolveJump(
                connection, _ => new Connection { ConnectionId = "a" })).Message);
    }
}
