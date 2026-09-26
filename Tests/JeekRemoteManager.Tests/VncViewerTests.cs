using JeekRemoteManager.Models;
using JeekRemoteManager.Services;

namespace JeekRemoteManager.Tests;

public class VncViewerTests
{
    [Theory]
    [InlineData("vnc.example", 5901, "vnc.example::5901")]
    [InlineData(" 10.0.0.2 ", 5900, "10.0.0.2::5900")]
    [InlineData("::1", 5900, "[::1]::5900")]
    [InlineData("[fe80::1]", 5902, "[fe80::1]::5902")]
    public void Server_argument_always_names_the_port(string host, int port, string expected) =>
        Assert.Equal(expected, VncViewer.FormatServer(host, port));

    [Fact]
    public void Password_travels_in_the_environment_not_the_command_line()
    {
        var connection = new Connection
        {
            Type = ConnectionType.Vnc,
            Username = "alice",
            VncFullScreen = true,
            VncViewOnly = false,
            VncShared = true,
        };

        var startInfo = VncViewer.CreateViewerStartInfo(@"C:\TigerVNC\vncviewer.exe", connection, "h", 5900, "pw");

        Assert.Equal(["-FullScreen=1", "-ViewOnly=0", "-Shared=1", "h::5900"], startInfo.ArgumentList);
        Assert.Equal("pw", startInfo.Environment["VNC_PASSWORD"]);
        Assert.Equal("alice", startInfo.Environment["VNC_USERNAME"]);
    }

    [Fact]
    public void No_password_means_no_credentials_in_the_environment()
    {
        var startInfo = VncViewer.CreateViewerStartInfo(
            @"C:\TigerVNC\vncviewer.exe",
            new Connection { Type = ConnectionType.Vnc, Username = "alice" },
            "h",
            5900,
            "");

        Assert.False(startInfo.Environment.ContainsKey("VNC_PASSWORD"));
        Assert.False(startInfo.Environment.ContainsKey("VNC_USERNAME"));
    }

    [Fact]
    public void Vnc_defaults_to_port_5900() =>
        Assert.Equal(5900, Connection.DefaultPort(ConnectionType.Vnc));
}
