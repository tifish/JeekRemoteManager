using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using JeekRemoteManager.Models;
using Microsoft.Win32;

namespace JeekRemoteManager.Services;

/// <summary>
/// VNC connections open in the TigerVNC viewer, an external program like mstsc is for RDP.
/// Windows ships no VNC client and the app never downloads one itself: a missing viewer is
/// installed through winget, after the user agrees in the GUI.
/// </summary>
public static class VncViewer
{
    public const string WingetPackageId = "TigerVNC.TigerVNC";

    /// <summary>Shown to the user before it runs. <c>--source winget</c> keeps the msstore
    /// source (and its separate agreement prompt) out of it.</summary>
    public const string InstallCommand = $"winget install -e --id {WingetPackageId} --source winget";

    private const string ViewerFileName = "vncviewer.exe";

    /// <summary>Finds an installed TigerVNC viewer, or null.</summary>
    public static string? Locate()
    {
        foreach (var candidate in EnumerateCandidates())
        {
            if (File.Exists(candidate))
                return candidate;
        }

        // Other VNC products also name their viewer vncviewer.exe but take different
        // arguments, so a PATH hit counts only when it is TigerVNC's.
        var onPath = AgentCliLocator.FindOnPath(ViewerFileName);
        return onPath is not null && IsTigerVnc(onPath) ? onPath : null;
    }

    private static IEnumerable<string> EnumerateCandidates()
    {
        foreach (var location in EnumerateUninstallLocations())
            yield return Path.Combine(location, ViewerFileName);

        foreach (var folder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            var root = Environment.GetFolderPath(folder);
            if (root.Length > 0)
                yield return Path.Combine(root, "TigerVNC", ViewerFileName);
        }
    }

    /// <summary>InstallLocation of every TigerVNC entry under Add/Remove Programs, machine
    /// (both registry views) and per-user.</summary>
    private static IEnumerable<string> EnumerateUninstallLocations()
    {
        var roots = new (RegistryHive Hive, RegistryView View)[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64),
            (RegistryHive.LocalMachine, RegistryView.Registry32),
            (RegistryHive.CurrentUser, RegistryView.Default),
        };

        var locations = new List<string>();
        foreach (var (hive, view) in roots)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = baseKey.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null)
                    continue;

                foreach (var name in uninstall.GetSubKeyNames())
                {
                    using var entry = uninstall.OpenSubKey(name);
                    if (entry?.GetValue("DisplayName") is not string displayName
                        || !displayName.StartsWith("TigerVNC", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (entry.GetValue("InstallLocation") is string location && location.Trim().Length > 0)
                        locations.Add(location.Trim().Trim('"'));
                }
            }
            catch
            {
                // Unreadable hive: fall through to the fixed install folders.
            }
        }

        return locations;
    }

    private static bool IsTigerVnc(string path)
    {
        if (path.Contains("TigerVNC", StringComparison.OrdinalIgnoreCase))
            return true;

        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return $"{info.ProductName} {info.FileDescription} {info.CompanyName}"
                .Contains("TigerVNC", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>winget.exe (an App Installer execution alias), or null when App Installer is missing.</summary>
    public static string? LocateWinget()
    {
        var onPath = AgentCliLocator.FindOnPath("winget.exe");
        if (onPath is not null)
            return onPath;

        var alias = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft",
            "WindowsApps",
            "winget.exe");
        return File.Exists(alias) ? alias : null;
    }

    /// <summary>
    /// The visible console that runs <see cref="InstallCommand"/>. winget's progress, its UAC
    /// prompt and any error stay in front of the user; the window only waits for Enter when
    /// winget failed, so its message can be read.
    /// </summary>
    public static ProcessStartInfo CreateInstallProcessStartInfo(string winget)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = false,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(
            $"& '{winget.Replace("'", "''")}'{InstallCommand["winget".Length..]}; "
            + "if ($LASTEXITCODE -ne 0) { Read-Host 'Press Enter to close' }; exit $LASTEXITCODE");
        return startInfo;
    }

    /// <summary>Runs winget in its own console, waits for it, and returns the viewer it installed.</summary>
    /// <exception cref="InvalidOperationException">winget is missing or finished without a viewer.</exception>
    public static async Task<string> InstallAsync(CancellationToken cancellationToken = default)
    {
        var winget = LocateWinget()
                     ?? throw new InvalidOperationException(
                         "winget is not available. Install App Installer from the Microsoft Store, or install TigerVNC yourself.");

        using (var process = Process.Start(CreateInstallProcessStartInfo(winget))
                             ?? throw new InvalidOperationException("Could not start winget."))
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"winget exited with code {process.ExitCode}.");
        }

        return Locate()
               ?? throw new InvalidOperationException("winget finished, but the TigerVNC viewer was not found.");
    }

    /// <summary>
    /// Formats the viewer's server argument. TigerVNC reads "host:N" as display N (port
    /// 5900+N), so an explicit port always uses "host::port"; IPv6 literals are bracketed.
    /// </summary>
    public static string FormatServer(string host, int port)
    {
        host = host.Trim();
        if (IPAddress.TryParse(host.Trim('[', ']'), out var address)
            && address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            host = $"[{host.Trim('[', ']')}]";
        }

        return $"{host}::{port}";
    }

    /// <summary>
    /// The viewer's command line. The password goes through TigerVNC's VNC_PASSWORD
    /// (and VNC_USERNAME) environment variables of the child only — never an argument,
    /// which any process could read, and never a file. Every option is passed explicitly
    /// because the viewer otherwise falls back to whatever it saved in the registry last time.
    /// </summary>
    public static ProcessStartInfo CreateViewerStartInfo(
        string viewerPath,
        Connection connection,
        string host,
        int port,
        string password)
    {
        var startInfo = new ProcessStartInfo(viewerPath)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(viewerPath) ?? "",
        };
        startInfo.ArgumentList.Add($"-FullScreen={Flag(connection.VncFullScreen)}");
        startInfo.ArgumentList.Add($"-ViewOnly={Flag(connection.VncViewOnly)}");
        startInfo.ArgumentList.Add($"-Shared={Flag(connection.VncShared)}");
        startInfo.ArgumentList.Add(FormatServer(host, port));

        // Never let values inherited from this process's environment answer for this connection.
        startInfo.Environment.Remove("VNC_PASSWORD");
        startInfo.Environment.Remove("VNC_USERNAME");
        if (password.Length > 0)
        {
            startInfo.Environment["VNC_PASSWORD"] = password;
            // TigerVNC uses the pair only for username+password schemes (VeNCrypt Plain, RA2);
            // classic VNC authentication reads the password alone.
            var username = connection.Username.Trim();
            if (username.Length > 0)
                startInfo.Environment["VNC_USERNAME"] = username;
        }

        return startInfo;

        static string Flag(bool value) => value ? "1" : "0";
    }

    /// <summary>
    /// Starts the viewer for <paramref name="connection"/>. With a jump host, the viewer
    /// connects to a local port the SSH connection forwards to the VNC server (resolved on
    /// the jump host's side, so "localhost" there means the jump host), and that tunnel is
    /// closed when the viewer exits.
    /// </summary>
    /// <param name="startProcess">Replaces <see cref="Process.Start(ProcessStartInfo)"/>; Debug MCP only.</param>
    public static async Task<Process> LaunchAsync(
        Connection connection,
        string viewerPath,
        SshDialOptions options,
        Func<string, Connection?>? resolveConnection,
        Func<ProcessStartInfo, Process?>? startProcess = null)
    {
        var host = connection.Host.Trim();
        if (host.Length == 0)
            throw new InvalidOperationException("The VNC connection has no host.");
        var port = connection.Port > 0 ? connection.Port : Connection.DefaultPort(ConnectionType.Vnc);

        // An unreadable password (another master password) falls back to the viewer's own prompt.
        var password = PasswordProtector.TryDecrypt(connection.EncryptedPassword, out var clear) ? clear : "";

        SshJumpTunnel? tunnel = null;
        try
        {
            if (SshDialer.ResolveJump(connection, resolveConnection) is { } jump)
            {
                // The dial blocks (ssh-agent IPC, network) and must stay off the UI thread.
                tunnel = await Task.Run(() => SshJumpTunnel.Open(jump, host, port, options)).ConfigureAwait(false);
            }

            var startInfo = tunnel is null
                ? CreateViewerStartInfo(viewerPath, connection, host, port, password)
                : CreateViewerStartInfo(viewerPath, connection, "127.0.0.1", tunnel.LocalPort, password);
            var process = (startProcess is null ? Process.Start(startInfo) : startProcess(startInfo))
                          ?? throw new InvalidOperationException("Could not start the VNC viewer.");

            if (tunnel is not null)
                _ = CloseTunnelOnExitAsync(process, tunnel);
            return process;
        }
        catch
        {
            tunnel?.Dispose();
            throw;
        }
    }

    private static async Task CloseTunnelOnExitAsync(Process process, SshJumpTunnel tunnel)
    {
        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch
        {
            // The handle is gone either way; the tunnel has nothing left to serve.
        }
        finally
        {
            tunnel.Dispose();
        }
    }
}
