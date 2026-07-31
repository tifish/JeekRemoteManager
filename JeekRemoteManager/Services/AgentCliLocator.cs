using System;
using System.IO;
using Microsoft.Win32;

namespace JeekRemoteManager.Services;

/// <summary>
/// Finds the installed agent executables on Windows — CLIs (<c>claude</c>, <c>codex</c>,
/// <c>grok</c>, <c>agy</c>) and the editors and desktop apps opened on a workspace folder.
/// </summary>
public static class AgentCliLocator
{
    /// <summary>
    /// Returns the registered shell command for a URI scheme, or <c>null</c> when Windows has
    /// no handler. Desktop protocol availability must be checked explicitly: ShellExecute can
    /// be invoked for any URI, but an unregistered scheme only fails after the user clicks it.
    /// </summary>
    public static string? FindProtocolHandler(string scheme)
    {
        if (string.IsNullOrWhiteSpace(scheme)
            || scheme.Any(ch => !char.IsLetterOrDigit(ch) && ch is not '+' and not '-' and not '.'))
        {
            return null;
        }

        var subKey = $@"{scheme}\shell\open\command";
        try
        {
            using var currentUser = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{subKey}");
            if (currentUser?.GetValue(null) is string userCommand
                && !string.IsNullOrWhiteSpace(userCommand))
            {
                return userCommand;
            }

            using var classesRoot = Registry.ClassesRoot.OpenSubKey(subKey);
            return classesRoot?.GetValue(null) is string machineCommand
                   && !string.IsNullOrWhiteSpace(machineCommand)
                ? machineCommand
                : null;
        }
        catch
        {
            return null;
        }
    }

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

    public static string? FindCopilot() =>
        FindNpmOrPathCommand("copilot", includeWinGetLink: true);

    public static string? FindOpenCode()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var native = Path.Combine(home, ".opencode", "bin", "opencode.exe");
        return File.Exists(native)
            ? ResolveRealPath(native)
            : FindNpmOrPathCommand("opencode");
    }

    public static string? FindPi() => FindNpmOrPathCommand("pi");

    public static string? FindOmp()
    {
        // The recommended Windows installer places its standalone binary here when Bun is not
        // already available. Probe it directly because the child installer cannot update this
        // running app's inherited PATH.
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var native = Path.Combine(localAppData, "omp", "omp.exe");
        return File.Exists(native)
            ? ResolveRealPath(native)
            : FindNpmOrPathCommand("omp");
    }

    /// <summary>
    /// Returns the full path to <c>agy.exe</c> (Antigravity CLI), or <c>null</c> if it is not
    /// installed. The installer drops it under <c>%LOCALAPPDATA%\agy\bin</c>.
    /// </summary>
    public static string? FindAntigravityCli()
    {
        foreach (var candidate in EnumerateAntigravityCliCandidates())
        {
            if (File.Exists(candidate))
                return ResolveRealPath(candidate);
        }

        var found = FindOnPath("agy.exe") ?? FindOnPath("agy.cmd") ?? FindOnPath("agy");
        return found is null ? null : ResolveRealPath(found);
    }

    /// <summary>
    /// Returns the full path to <c>Antigravity.exe</c> (the Antigravity 2.0 desktop app), or
    /// <c>null</c> if it is not installed. Distinct from the IDE, which installs beside it under
    /// its own "Antigravity IDE" folder.
    /// </summary>
    public static string? FindAntigravityDesktop()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidate = Path.Combine(localAppData, "Programs", "Antigravity", "Antigravity.exe");
        return File.Exists(candidate) ? ResolveRealPath(candidate) : null;
    }

    /// <summary>
    /// Returns the full path to <c>Antigravity IDE.exe</c>, or <c>null</c> if it is not installed.
    /// </summary>
    public static string? FindAntigravityIde()
    {
        foreach (var candidate in EnumerateAntigravityIdeCandidates())
        {
            if (File.Exists(candidate))
                return ResolveRealPath(candidate);
        }

        return null;
    }

    /// <summary>
    /// Returns the full path to <c>Code.exe</c> (Visual Studio Code), or <c>null</c> if it is not
    /// installed. Prefers the real executable over the <c>code.cmd</c> shim on PATH so the folder
    /// can be passed as a plain argument.
    /// </summary>
    public static string? FindVsCode()
    {
        foreach (var candidate in EnumerateVsCodeCandidates())
        {
            if (File.Exists(candidate))
                return ResolveRealPath(candidate);
        }

        var found = FindOnPath("Code.exe") ?? FindOnPath("code.cmd");
        return found is null ? null : ResolveRealPath(found);
    }

    /// <summary>
    /// Returns the full path to <c>Cursor.exe</c>, or <c>null</c> if it is not installed.
    /// Same layout as VS Code — Cursor ships as a per-user Programs install by default.
    /// </summary>
    public static string? FindCursor()
    {
        foreach (var candidate in EnumerateCursorCandidates())
        {
            if (File.Exists(candidate))
                return ResolveRealPath(candidate);
        }

        var found = FindOnPath("Cursor.exe") ?? FindOnPath("cursor.cmd");
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
    internal static string ResolveRealPath(string path)
    {
        try
        {
            if (new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true) is { } fileTarget)
            {
                // Keep argv[0]-dispatch shims (e.g. mise's codex.exe → mise.exe) unresolved:
                // the target binary picks its behavior from the invoked file name, so
                // launching the resolved path directly loses the tool identity.
                return string.Equals(fileTarget.Name, Path.GetFileName(path), StringComparison.OrdinalIgnoreCase)
                    ? fileTarget.FullName
                    : path;
            }

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

    private static System.Collections.Generic.IEnumerable<string> EnumerateVsCodeCandidates()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(localAppData, "Programs", "Microsoft VS Code", "Code.exe");

        // System-wide setup.
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Microsoft VS Code",
            "Code.exe");
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft VS Code",
            "Code.exe");
    }

    private static System.Collections.Generic.IEnumerable<string> EnumerateCursorCandidates()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(localAppData, "Programs", "Cursor", "Cursor.exe");

        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Cursor",
            "Cursor.exe");
    }

    private static System.Collections.Generic.IEnumerable<string> EnumerateAntigravityCliCandidates()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(localAppData, "agy", "bin", "agy.exe");
        yield return Path.Combine(localAppData, "agy", "bin", "agy");
    }

    private static System.Collections.Generic.IEnumerable<string> EnumerateAntigravityIdeCandidates()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        // Recent builds renamed the per-user folder from "Antigravity" to "Antigravity IDE" when
        // the 2.0 desktop app took the original name; keep probing both.
        yield return Path.Combine(localAppData, "Programs", "Antigravity IDE", "Antigravity IDE.exe");
        yield return Path.Combine(localAppData, "Programs", "Antigravity", "Antigravity IDE.exe");

        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Google",
            "Antigravity",
            "Antigravity IDE.exe");
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

    private static string? FindNpmOrPathCommand(string name, bool includeWinGetLink = false)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var candidates = new System.Collections.Generic.List<string>
        {
            Path.Combine(appData, "npm", name + ".cmd"),
            Path.Combine(appData, "npm", name + ".exe"),
        };
        if (includeWinGetLink)
        {
            candidates.Add(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft",
                "WinGet",
                "Links",
                name + ".exe"));
        }

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return ResolveRealPath(candidate);
        }

        var found = FindOnPath(name + ".exe")
                    ?? FindOnPath(name + ".cmd")
                    ?? FindOnPath(name);
        return found is null ? null : ResolveRealPath(found);
    }
}
