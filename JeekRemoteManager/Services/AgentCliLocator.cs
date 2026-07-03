using System;
using System.IO;

namespace JeekRemoteManager.Services;

/// <summary>Finds installed agent CLI executables (<c>claude</c>, <c>codex</c>) on Windows.</summary>
public static class AgentCliLocator
{
    /// <summary>
    /// Returns the full path to <c>claude.exe</c> (or the native launcher), or <c>null</c> if
    /// it is not installed. Probes PATH plus the native installer's default location
    /// (<c>%USERPROFILE%\.local\bin</c>) and the npm global prefix.
    /// </summary>
    public static string? FindClaude()
    {
        foreach (var candidate in EnumerateClaudeCandidates())
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return FindOnPath("claude.exe") ?? FindOnPath("claude.cmd") ?? FindOnPath("claude");
    }

    /// <summary>
    /// Returns the full path to <c>codex.exe</c>, or <c>null</c> if it is not installed.
    /// Probes PATH plus the native installer's default location
    /// (<c>%LOCALAPPDATA%\Programs\OpenAI\Codex\bin</c>) and the npm global prefix.
    /// </summary>
    public static string? FindCodex()
    {
        foreach (var candidate in EnumerateCodexCandidates())
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return FindOnPath("codex.exe") ?? FindOnPath("codex.cmd") ?? FindOnPath("codex");
    }

    private static System.Collections.Generic.IEnumerable<string> EnumerateClaudeCandidates()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Path.Combine(home, ".local", "bin", "claude.exe");
        yield return Path.Combine(home, ".local", "bin", "claude");

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        yield return Path.Combine(appData, "npm", "claude.cmd");
        yield return Path.Combine(appData, "npm", "claude.exe");
    }

    private static System.Collections.Generic.IEnumerable<string> EnumerateCodexCandidates()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(localAppData, "Programs", "OpenAI", "Codex", "bin", "codex.exe");

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        yield return Path.Combine(appData, "npm", "codex.cmd");
        yield return Path.Combine(appData, "npm", "codex.exe");
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
