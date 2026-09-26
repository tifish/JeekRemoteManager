using System;
using System.Collections.Generic;
using System.Linq;
using Renci.SshNet;

namespace JeekRemoteManager.Services;

/// <summary>One parsed port-forwarding line.</summary>
/// <param name="Kind">'L' local, 'R' remote, or 'D' dynamic (SOCKS).</param>
public sealed record PortForwardSpec(char Kind, string BindHost, uint BindPort, string TargetHost, uint TargetPort)
{
    public override string ToString() => Kind == 'D'
        ? $"D {BindHost}:{BindPort} (SOCKS)"
        : $"{Kind} {BindHost}:{BindPort} -> {TargetHost}:{TargetPort}";

    public ForwardedPort Create() => Kind switch
    {
        'L' => new ForwardedPortLocal(BindHost, BindPort, TargetHost, TargetPort),
        'R' => new ForwardedPortRemote(BindHost, BindPort, TargetHost, TargetPort),
        _ => new ForwardedPortDynamic(BindHost, BindPort),
    };
}

/// <summary>
/// Port forwarding for SSH connections, written one per line in an OpenSSH-like form:
/// <code>
/// L 8080 db.internal:5432        # or L 8080:db.internal:5432 (ssh -L form)
/// L 0.0.0.0:8080 db.internal:5432
/// R 9000 localhost:3000          # remote port 9000 reaches local 3000
/// D 1080                         # SOCKS proxy on local port 1080
/// </code>
/// Listeners — local, SOCKS, and the server-side end of a remote forward — bind to
/// 127.0.0.1 unless a bind address is given, so a forward is never exposed to the network
/// by accident. (Not "localhost": SSH.NET resolves it, may pick ::1, and an IPv4 client on
/// the server then finds nothing listening.) Blank lines and '#' comments are ignored.
/// </summary>
public static class SshPortForwarding
{
    private const string LocalBind = "127.0.0.1";

    /// <exception cref="FormatException">A line is not a valid forward; the message names the line.</exception>
    public static IReadOnlyList<PortForwardSpec> Parse(string? text)
    {
        var result = new List<PortForwardSpec>();
        var lines = (text ?? "").Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var comment = line.IndexOf('#');
            if (comment >= 0)
                line = line[..comment];
            line = line.Trim();
            if (line.Length == 0)
                continue;

            try
            {
                result.Add(ParseLine(line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
            }
            catch (FormatException ex)
            {
                throw new FormatException($"Port forwarding line {i + 1}: {ex.Message}", ex);
            }
        }

        return result;
    }

    /// <summary>Validation message for the editor, or null when every line parses.</summary>
    public static string? Validate(string? text)
    {
        try
        {
            Parse(text);
            return null;
        }
        catch (FormatException ex)
        {
            return ex.Message;
        }
    }

    private static PortForwardSpec ParseLine(string[] parts)
    {
        var kind = char.ToUpperInvariant(parts[0].TrimStart('-')[0]);
        if (parts[0].TrimStart('-').Length != 1 || kind is not ('L' or 'R' or 'D'))
            throw new FormatException("start with L (local), R (remote) or D (SOCKS).");

        if (kind == 'D')
        {
            if (parts.Length != 2)
                throw new FormatException("the SOCKS form is D [bind:]port.");
            var (host, port) = ParseEndpoint(parts[1], LocalBind);
            return new PortForwardSpec('D', host, port, "", 0);
        }

        string bindText;
        string targetText;
        if (parts.Length == 3)
        {
            bindText = parts[1];
            targetText = parts[2];
        }
        else if (parts.Length == 2 && parts[1].Split(':') is { Length: 3 or 4 } fields)
        {
            // ssh -L form: [bind:]port:host:hostport.
            bindText = string.Join(':', fields[..^2]);
            targetText = string.Join(':', fields[^2..]);
        }
        else
        {
            throw new FormatException($"the form is {kind} [bind:]port host:port.");
        }

        var bind = ParseEndpoint(bindText, LocalBind);
        var target = ParseEndpoint(targetText, "", requireHost: true);
        return new PortForwardSpec(kind, bind.Host, bind.Port, target.Host, target.Port);
    }

    private static (string Host, uint Port) ParseEndpoint(string value, string defaultHost, bool requireHost = false)
    {
        var separator = value.LastIndexOf(':');
        var host = separator < 0 ? "" : value[..separator].Trim();
        var portText = separator < 0 ? value : value[(separator + 1)..];
        if (host.Length == 0)
        {
            if (requireHost)
                throw new FormatException($"'{value}' must be host:port.");
            host = defaultHost;
        }

        if (!uint.TryParse(portText, out var port) || port is 0 or > 65535)
            throw new FormatException($"'{portText}' is not a valid TCP port.");
        return (host, port);
    }

    /// <summary>
    /// Starts each forward on a connected client. One forward failing (a local port already
    /// in use, a server that refuses remote forwarding) does not stop the others or the
    /// session; every line gets a result the caller can show.
    /// </summary>
    public static IReadOnlyList<(PortForwardSpec Spec, ForwardedPort? Port, string? Error)> StartAll(
        SshClient client,
        IEnumerable<PortForwardSpec> specs)
    {
        var results = new List<(PortForwardSpec, ForwardedPort?, string?)>();
        foreach (var spec in specs)
        {
            var port = spec.Create();
            try
            {
                client.AddForwardedPort(port);
                port.Start();
                results.Add((spec, port, null));
            }
            catch (Exception ex)
            {
                try { client.RemoveForwardedPort(port); } catch { /* not added */ }
                try { port.Dispose(); } catch { /* best effort */ }
                results.Add((spec, null, ex.Message));
            }
        }

        return results;
    }

    /// <summary>Stops forwards started by <see cref="StartAll"/>.</summary>
    /// <remarks>Wrap them in a <see cref="PortForwardSet"/> to tie them to a transport's lifetime.</remarks>
    public static void StopAll(IEnumerable<ForwardedPort> ports)
    {
        foreach (var port in ports)
        {
            try { port.Stop(); } catch { /* transport already gone */ }
            try { port.Dispose(); } catch { /* best effort */ }
        }
    }
}

/// <summary>Started forwards, stopped together when disposed.</summary>
public sealed class PortForwardSet(IReadOnlyList<ForwardedPort> ports) : IDisposable
{
    public IReadOnlyList<ForwardedPort> Ports => ports;

    public void Dispose() => SshPortForwarding.StopAll(ports);
}
