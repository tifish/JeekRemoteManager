using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using JeekRemoteManager.Models;
using Microsoft.Win32;

namespace JeekRemoteManager.Services;

public sealed record WslDistroInfo(string Name, bool IsDefault);

/// <summary>
/// Enumerates installed WSL distributions. Reads the Lxss registry key directly
/// (fast, no process spawn, and it knows the default distribution); falls back to
/// `wsl.exe --list --quiet` when the key is missing. Note wsl.exe writes UTF-16LE
/// when its output is redirected.
/// </summary>
public static class WslDistroService
{
    private const string LxssKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Lxss";

    public static string WslExePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe");

    public static bool IsWslInstalled => File.Exists(WslExePath);

    /// <summary>Installed distributions, the default one first. Empty when WSL is
    /// not installed or has no distributions.</summary>
    public static List<WslDistroInfo> ListDistros()
    {
        var distros = ListFromRegistry();
        if (distros.Count == 0)
            distros = ListFromWslExe();
        return distros
            .OrderByDescending(d => d.IsDefault)
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>wsl.exe arguments for opening the connection's interactive shell.</summary>
    public static List<string> BuildLaunchArguments(Connection connection)
    {
        var args = new List<string>();

        var distro = connection.WslDistro.Trim();
        if (distro.Length > 0)
        {
            args.Add("--distribution");
            args.Add(distro);
        }

        var user = connection.Username.Trim();
        if (user.Length > 0)
        {
            args.Add("--user");
            args.Add(user);
        }

        // Without --cd the shell starts in the app's own Windows directory
        // (/mnt/...); "~" is wsl.exe's spelling of the Linux user's home.
        var startDir = connection.WslStartDirectory.Trim();
        args.Add("--cd");
        args.Add(startDir.Length > 0 ? startDir : "~");

        return args;
    }

    /// <summary>UNC root of a distribution's filesystem.</summary>
    public static string UncRoot(string distro) => @"\\wsl.localhost\" + distro;

    /// <summary>
    /// Translates a Windows path to its in-distro equivalent: drive letters map to
    /// /mnt/&lt;drive&gt;, \\wsl.localhost\&lt;distro&gt; UNC paths map back to native paths.
    /// Used when files are dropped onto a WSL terminal.
    /// </summary>
    public static string ToWslPath(string windowsPath)
    {
        var path = windowsPath.Trim();
        if (path.Length >= 2 && path[1] == ':' && char.IsAsciiLetter(path[0]))
            return "/mnt/" + char.ToLowerInvariant(path[0]) + path[2..].Replace('\\', '/');

        foreach (var prefix in new[] { @"\\wsl.localhost\", @"\\wsl$\" })
        {
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            var rest = path[prefix.Length..];
            var slash = rest.IndexOf('\\');
            return slash < 0 ? "/" : rest[slash..].Replace('\\', '/');
        }

        return path.Replace('\\', '/');
    }

    private static List<WslDistroInfo> ListFromRegistry()
    {
        var result = new List<WslDistroInfo>();
        try
        {
            using var lxss = Registry.CurrentUser.OpenSubKey(LxssKeyPath);
            if (lxss is null)
                return result;

            var defaultGuid = lxss.GetValue("DefaultDistribution") as string;
            foreach (var subKeyName in lxss.GetSubKeyNames())
            {
                using var distroKey = lxss.OpenSubKey(subKeyName);
                if (distroKey?.GetValue("DistributionName") is not string name
                    || string.IsNullOrWhiteSpace(name))
                    continue;
                // A distribution mid-install/uninstall has a non-1 State.
                if (distroKey.GetValue("State") is int state && state != 1)
                    continue;
                result.Add(new WslDistroInfo(
                    name,
                    string.Equals(subKeyName, defaultGuid, StringComparison.OrdinalIgnoreCase)));
            }
        }
        catch
        {
            // Registry layout changed or access denied — the wsl.exe fallback covers it.
        }
        return result;
    }

    private static List<WslDistroInfo> ListFromWslExe()
    {
        var result = new List<WslDistroInfo>();
        if (!IsWslInstalled)
            return result;

        try
        {
            var psi = new ProcessStartInfo(WslExePath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                // wsl.exe emits UTF-16LE on a redirected pipe.
                StandardOutputEncoding = System.Text.Encoding.Unicode,
            };
            psi.ArgumentList.Add("--list");
            psi.ArgumentList.Add("--quiet");

            using var process = Process.Start(psi);
            if (process is null)
                return result;

            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(10000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return result;
            }

            foreach (var line in output.Split('\n'))
            {
                var name = line.Trim().Trim('\0');
                if (name.Length > 0)
                    result.Add(new WslDistroInfo(name, IsDefault: result.Count == 0));
            }
        }
        catch
        {
            // No WSL, or wsl.exe errored — treated as "no distributions".
        }
        return result;
    }
}
