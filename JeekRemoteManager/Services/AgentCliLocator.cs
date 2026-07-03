using System;
using System.IO;

namespace JeekRemoteManager.Services;

/// <summary>Finds the installed <c>claude</c> CLI executable on Windows.</summary>
public static class AgentCliLocator
{
    /// <summary>
    /// Returns the full path to <c>claude.exe</c> (or the native launcher), or <c>null</c> if
    /// it is not installed. Probes PATH plus the native installer's default location
    /// (<c>%USERPROFILE%\.local\bin</c>) and the npm global prefix.
    /// </summary>
    public static string? FindClaude()
    {
        foreach (var candidate in EnumerateCandidates())
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return FindOnPath("claude.exe") ?? FindOnPath("claude.cmd") ?? FindOnPath("claude");
    }

    private static System.Collections.Generic.IEnumerable<string> EnumerateCandidates()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Path.Combine(home, ".local", "bin", "claude.exe");
        yield return Path.Combine(home, ".local", "bin", "claude");

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        yield return Path.Combine(appData, "npm", "claude.cmd");
        yield return Path.Combine(appData, "npm", "claude.exe");
    }

    private static string? FindOnPath(string fileName)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVar))
            return null;

        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string full;
            try
            {
                full = Path.Combine(dir.Trim(), fileName);
            }
            catch
            {
                continue;
            }

            if (File.Exists(full))
                return full;
        }

        return null;
    }
}
