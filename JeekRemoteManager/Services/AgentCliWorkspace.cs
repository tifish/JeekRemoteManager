using System;
using System.IO;
using System.Linq;
using System.Text;
using JeekRemoteManager.Models;

namespace JeekRemoteManager.Services;

/// <summary>
/// Resolves a durable per-connection working directory for agent CLIs, keyed by the
/// connection's path under the Connections tree (e.g. <c>vps/bwg</c>). Hosts
/// <c>CLAUDE.md</c> / <c>AGENTS.md</c> so Claude, Codex, and Grok pick up the same
/// "you are operating this remote server" context.
/// </summary>
public static class AgentCliWorkspace
{
    public const string RootFolderName = "AgentWorkspaces";

    /// <summary>
    /// Always under <c>%LOCALAPPDATA%\JeekRemoteManager\AgentWorkspaces</c>
    /// (machine-local; not tied to portable/roaming config roots).
    /// </summary>
    public static string RootPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JeekRemoteManager",
        RootFolderName);

    /// <summary>
    /// Relative path matching the connection tree, without the <c>.json</c> extension
    /// (e.g. <c>vps/bwg</c>). Falls back to the connection name when no file path is known.
    /// </summary>
    public static string ResolveRelativePath(
        string connectionsRoot,
        string? sourcePath,
        Connection? connection)
    {
        if (!string.IsNullOrWhiteSpace(sourcePath) && !string.IsNullOrWhiteSpace(connectionsRoot))
        {
            try
            {
                var full = Path.GetFullPath(sourcePath);
                var root = Path.GetFullPath(connectionsRoot)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var prefix = root + Path.DirectorySeparatorChar;
                if (full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
                {
                    var rel = Path.GetRelativePath(root, full);
                    if (rel.EndsWith(ConnectionStore.FileExtension, StringComparison.OrdinalIgnoreCase))
                        rel = rel[..^ConnectionStore.FileExtension.Length];
                    var cleaned = SanitizeRelativePath(rel);
                    if (!string.IsNullOrEmpty(cleaned))
                        return cleaned;
                }
            }
            catch
            {
                // Fall through to name-based identity.
            }
        }

        if (!string.IsNullOrWhiteSpace(connection?.Name))
            return SanitizeRelativePath(connection.Name);

        if (!string.IsNullOrWhiteSpace(connection?.ConnectionId)
            && Guid.TryParse(connection.ConnectionId, out _))
        {
            return Path.Combine("connection", connection.ConnectionId.Trim());
        }

        return "unknown";
    }

    /// <summary>
    /// Absolute workspace under <see cref="RootPath"/>/&lt;tree-relative-path&gt;.
    /// Creates the directory and refreshes <c>CLAUDE.md</c> / <c>AGENTS.md</c>.
    /// </summary>
    public static string Ensure(
        string connectionsRoot,
        string? sourcePath,
        Connection? connection)
    {
        var relative = ResolveRelativePath(connectionsRoot, sourcePath, connection);
        var absolute = Path.GetFullPath(Path.Combine(
            RootPath,
            relative.Replace('/', Path.DirectorySeparatorChar)));

        Directory.CreateDirectory(absolute);
        WriteAgentDocs(absolute, relative, connection, sourcePath);
        return absolute;
    }

    private static void WriteAgentDocs(
        string workspaceDir,
        string relativePath,
        Connection? connection,
        string? sourcePath)
    {
        var body = BuildAgentDocBody(relativePath, connection, sourcePath);
        // Both files carry the same guidance: Claude reads CLAUDE.md; Codex/Grok read AGENTS.md.
        File.WriteAllText(Path.Combine(workspaceDir, "CLAUDE.md"), body, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.WriteAllText(Path.Combine(workspaceDir, "AGENTS.md"), body, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static string BuildAgentDocBody(
        string relativePath,
        Connection? connection,
        string? sourcePath)
    {
        var name = connection?.Name?.Trim();
        if (string.IsNullOrEmpty(name))
            name = Path.GetFileName(relativePath.Replace('\\', '/'));

        var kind = connection?.IsWsl == true
            ? "WSL"
            : connection?.IsRdp == true
                ? "RDP"
                : "SSH";

        var target = connection?.IsWsl == true
            ? (string.IsNullOrWhiteSpace(connection.WslDistro) ? "default WSL distribution" : connection.WslDistro.Trim())
            : string.IsNullOrWhiteSpace(connection?.Host)
                ? "(unknown host)"
                : $"{connection!.Username}@{connection.Host}:{connection.Port}";

        var notes = connection?.Notes?.Trim();
        var sb = new StringBuilder();
        sb.AppendLine("# JeekRemoteManager agent workspace");
        sb.AppendLine();
        sb.AppendLine("This directory is the **local** working directory for the agent CLI attached to one");
        sb.AppendLine("JeekRemoteManager connection tab. It is **not** the remote server filesystem.");
        sb.AppendLine();
        sb.AppendLine("## Primary goal");
        sb.AppendLine();
        sb.AppendLine("You are assisting the user **inside JeekRemoteManager** to operate a remote server");
        sb.AppendLine($"(connection path: `{relativePath.Replace('\\', '/')}`, display name: **{name}**).");
        sb.AppendLine("Almost all useful work happens on that remote session, not on this Windows machine.");
        sb.AppendLine();
        sb.AppendLine("## Connection");
        sb.AppendLine();
        sb.AppendLine($"- **Type:** {kind}");
        sb.AppendLine($"- **Target:** {target}");
        sb.AppendLine($"- **Tree path:** `{relativePath.Replace('\\', '/')}`");
        if (!string.IsNullOrWhiteSpace(sourcePath))
            sb.AppendLine($"- **Connection file:** `{sourcePath}`");
        if (!string.IsNullOrEmpty(notes))
        {
            sb.AppendLine();
            sb.AppendLine("### Notes from the connection");
            sb.AppendLine();
            sb.AppendLine(notes);
        }

        sb.AppendLine();
        sb.AppendLine("## Tools: local vs remote");
        sb.AppendLine();
        sb.AppendLine("### Remote server (use these first)");
        sb.AppendLine();
        sb.AppendLine("JeekRemoteManager exposes **jrm-remote** MCP tools that drive the **already-open**");
        sb.AppendLine("interactive terminal for this tab (same shell, same cwd, same environment):");
        sb.AppendLine();
        sb.AppendLine("| Tool | Purpose |");
        sb.AppendLine("|------|---------|");
        sb.AppendLine("| `terminal_run` | Run a non-interactive remote command / short script; returns output |");
        sb.AppendLine("| `terminal_run_danger` | Same, but asks the user to confirm destructive work |");
        sb.AppendLine("| `terminal_interrupt` | Ctrl-C / restore shell input |");
        sb.AppendLine("| `terminal_reconnect` | Rebuild SSH/WSL when the channel is unhealthy |");
        sb.AppendLine("| `file_upload` / `file_download` | Transfer files (SSH ZMODEM; WSL prefers `/mnt/c/...`) |");
        sb.AppendLine();
        sb.AppendLine("Prefer **one tool call per step**, wait for the result, then decide the next step.");
        sb.AppendLine("When finished, reply with a short summary and no further tool calls.");
        sb.AppendLine();
        sb.AppendLine("### Local Windows machine");
        sb.AppendLine();
        sb.AppendLine("Your built-in shell / file tools (if any) run on the **local Windows host** where");
        sb.AppendLine("JeekRemoteManager runs — including this workspace directory. Use them only for");
        sb.AppendLine("local-side work (notes, reading files under this workspace, preparing uploads).");
        sb.AppendLine("**Never** assume a local bash command runs on the remote server.");
        sb.AppendLine();
        sb.AppendLine("## Safety");
        sb.AppendLine();
        sb.AppendLine("- Use `terminal_run_danger` for deletes, drops, force-push, disk wipe, prune with data, etc.");
        sb.AppendLine("- Prefer non-interactive flags (`-y`, `--yes`, `--no-pager`, `-o cat`) when available.");
        sb.AppendLine("- Do **not** pipe to `less`/`more` or rely on interactive pagers; the host sets `PAGER=cat`.");
        sb.AppendLine("- If the shell seems stuck after a command (e.g. a pager), call `terminal_interrupt`.");
        sb.AppendLine("- Assume Linux on the remote unless the connection type or output says otherwise.");
        sb.AppendLine("- Large remote outputs may arrive as a short preview plus a local file path — that is");
        sb.AppendLine("  complete delivery; read the path with a local tool if you need every line.");
        sb.AppendLine();
        sb.AppendLine("## This workspace");
        sb.AppendLine();
        sb.AppendLine("Files here (including this document) are machine-local and may be refreshed by");
        sb.AppendLine("JeekRemoteManager when the AI panel opens. You may create extra notes or artifacts");
        sb.AppendLine("in this folder for the user's local reference.");
        sb.AppendLine();
        return sb.ToString();
    }

    private static string SanitizeRelativePath(string relative)
    {
        var parts = relative
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => p is not "." and not "..")
            .Select(SanitizeSegment)
            .Where(p => p.Length > 0)
            .ToArray();
        return parts.Length == 0 ? "unknown" : string.Join(Path.DirectorySeparatorChar, parts);
    }

    private static string SanitizeSegment(string segment)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = segment.Select(c => invalid.Contains(c) || c is '<' or '>' or ':' or '"' or '|' or '?' or '*'
            ? '_'
            : c).ToArray();
        var cleaned = new string(chars).Trim().TrimEnd('.');
        return string.IsNullOrEmpty(cleaned) ? "_" : cleaned;
    }
}
