using System;
using System.IO;

namespace JeekRemoteManager.Services;

/// <summary>Finds installed agent CLI executables (<c>claude</c>, <c>codex</c>, <c>grok</c>) on Windows.</summary>
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
                return ResolveRealPath(candidate);
        }

        var found = FindOnPath("claude.exe") ?? FindOnPath("claude.cmd") ?? FindOnPath("claude");
        return found is null ? null : ResolveRealPath(found);
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
                return ResolveRealPath(candidate);
        }

        var found = FindOnPath("codex.exe") ?? FindOnPath("codex.cmd") ?? FindOnPath("codex");
        return found is null ? null : ResolveRealPath(found);
    }

    /// <summary>
    /// Returns the full path to <c>grok.exe</c> (Grok Build CLI), or <c>null</c> if it is not
    /// installed. Probes PATH plus the native installer's default location
    /// (<c>%USERPROFILE%\.grok\bin</c>).
    /// </summary>
    public static string? FindGrok()
    {
        foreach (var candidate in EnumerateGrokCandidates())
        {
            if (File.Exists(candidate))
                return ResolveRealPath(candidate);
        }

        var found = FindOnPath("grok.exe") ?? FindOnPath("grok.cmd") ?? FindOnPath("grok");
        return found is null ? null : ResolveRealPath(found);
    }

    /// <summary>
    /// Resolves symlinks and directory junctions so the CLI runs from its real install
    /// directory. The Codex standalone installer exposes codex.exe through a junction
    /// (%LOCALAPPDATA%\Programs\OpenAI\Codex\bin → ~\.codex\packages\standalone\current\bin),
    /// and codex.exe locates its Windows sandbox helpers in a codex-resources directory
    /// relative to its own path — launched through the junction that directory does not
    /// exist and sandboxed commands fail with "program not found".
    /// </summary>
    private static string ResolveRealPath(string path)
    {
        try
        {
            if (new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true) is { } fileTarget)
                return fileTarget.FullName;

            // The file itself is not a link; resolve the nearest ancestor directory that is.
            var suffix = Path.GetFileName(path);
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            while (!string.IsNullOrEmpty(dir))
            {
                if (Directory.Exists(dir)
                    && new DirectoryInfo(dir).ResolveLinkTarget(returnFinalTarget: true) is { } dirTarget)
                {
                    return Path.Combine(dirTarget.FullName, suffix);
                }

                suffix = Path.Combine(Path.GetFileName(dir), suffix);
                dir = Path.GetDirectoryName(dir);
            }
        }
        catch
        {
            // Resolution is best-effort; fall back to the discovered path.
        }

        return path;
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

    private static System.Collections.Generic.IEnumerable<string> EnumerateGrokCandidates()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Path.Combine(home, ".grok", "bin", "grok.exe");
        yield return Path.Combine(home, ".grok", "bin", "grok");

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(localAppData, "grok", "bin", "grok.exe");
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
