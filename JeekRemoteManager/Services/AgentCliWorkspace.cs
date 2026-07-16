using System;
using System.IO;
using System.Linq;
using System.Text;
using JeekRemoteManager.Models;

namespace JeekRemoteManager.Services;

/// <summary>
/// Resolves a durable per-connection working directory for agent CLIs, keyed by the
/// connection's path under the Connections tree (e.g. <c>vps/bwg</c>). Hosts
/// <c>AGENTS.md</c> (full context), a thin <c>CLAUDE.md</c> that includes it, and
/// project MCP configs so Claude, Codex, and Grok (CLI or desktop) pick up the same
/// remote-server context without command-line prompts or flags.
/// </summary>
public static class AgentCliWorkspace
{
    public const string RootFolderName = "AgentWorkspaces";
    public const string McpServerName = "jrm-remote";

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
    /// Creates the directory, refreshes <c>AGENTS.md</c> (and a <c>CLAUDE.md</c> include),
    /// and when <paramref name="mcpEndpointUrl"/> is set, writes project MCP configs that
    /// desktop and CLI agents load from the working directory (no command-line MCP/system flags).
    /// </summary>
    public static string Ensure(
        string connectionsRoot,
        string? sourcePath,
        Connection? connection,
        string? mcpEndpointUrl = null)
    {
        var relative = ResolveRelativePath(connectionsRoot, sourcePath, connection);
        var absolute = Path.GetFullPath(Path.Combine(
            RootPath,
            relative.Replace('/', Path.DirectorySeparatorChar)));

        Directory.CreateDirectory(absolute);
        WriteAgentDocs(absolute, relative, connection, sourcePath, mcpEndpointUrl);
        if (!string.IsNullOrWhiteSpace(mcpEndpointUrl))
            WriteProjectMcpConfigs(absolute, mcpEndpointUrl.Trim());
        return absolute;
    }

    private static void WriteAgentDocs(
        string workspaceDir,
        string relativePath,
        Connection? connection,
        string? sourcePath,
        string? mcpEndpointUrl)
    {
        var body = BuildAgentDocBody(relativePath, connection, sourcePath, mcpEndpointUrl);
        // Full context lives in AGENTS.md (Codex/Grok/shared). Claude reads CLAUDE.md which
        // only includes AGENTS.md so we do not maintain two copies.
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        File.WriteAllText(Path.Combine(workspaceDir, "AGENTS.md"), body, utf8);
        File.WriteAllText(Path.Combine(workspaceDir, "CLAUDE.md"), "@AGENTS.md\n", utf8);
    }

    /// <summary>
    /// Writes project-level MCP configs for Claude (`.mcp.json`), Codex (`.codex/config.toml`),
    /// and Grok (`.grok/config.toml`) so any agent that opens this directory can reach the
    /// live JeekRemoteManager remote tools endpoint.
    /// </summary>
    public static void WriteProjectMcpConfigs(string workspaceDir, string mcpEndpointUrl)
    {
        Directory.CreateDirectory(workspaceDir);
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        // Legacy Claude sidecar used with --mcp-config; project agents load .mcp.json instead.
        TryDelete(Path.Combine(workspaceDir, "jrm-mcp.json"));

        // Claude Code / Claude Desktop project MCP (auto-loaded from workspace root).
        File.WriteAllText(
            Path.Combine(workspaceDir, ".mcp.json"),
            "{\n" +
            "  \"mcpServers\": {\n" +
            $"    \"{McpServerName}\": {{\n" +
            "      \"type\": \"http\",\n" +
            $"      \"url\": \"{EscapeJson(mcpEndpointUrl)}\"\n" +
            "    }\n" +
            "  }\n" +
            "}\n",
            utf8);

        // Codex project config (workspace/.codex/config.toml).
        var codexDir = Path.Combine(workspaceDir, ".codex");
        Directory.CreateDirectory(codexDir);
        File.WriteAllText(
            Path.Combine(codexDir, "config.toml"),
            "# Generated by JeekRemoteManager — per-connection remote tools\n" +
            $"# Open this workspace folder in Codex desktop/CLI; AGENTS.md has full context.\n" +
            $"[mcp_servers.{McpServerName}]\n" +
            $"url = \"{EscapeToml(mcpEndpointUrl)}\"\n",
            utf8);

        // Grok project MCP (workspace/.grok/config.toml).
        var grokDir = Path.Combine(workspaceDir, ".grok");
        Directory.CreateDirectory(grokDir);
        File.WriteAllText(
            Path.Combine(grokDir, "config.toml"),
            "# Generated by JeekRemoteManager — per-connection remote tools\n" +
            $"# Open this workspace folder in Grok; AGENTS.md has full context.\n" +
            $"[mcp_servers.{McpServerName}]\n" +
            "transport = \"http\"\n" +
            $"url = \"{EscapeToml(mcpEndpointUrl)}\"\n",
            utf8);
    }

    private static string BuildAgentDocBody(
        string relativePath,
        Connection? connection,
        string? sourcePath,
        string? mcpEndpointUrl)
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
        sb.AppendLine("This directory is the **local** working directory for the agent CLI (or desktop");
        sb.AppendLine("app) attached to one JeekRemoteManager connection tab. It is **not** the remote");
        sb.AppendLine("server filesystem.");
        sb.AppendLine();
        sb.AppendLine("All operational context for this connection lives in this file (`AGENTS.md`).");
        sb.AppendLine("`CLAUDE.md` only includes it (`@AGENTS.md`). Do **not** expect system prompts or");
        sb.AppendLine("server details on the command line — open this folder as the project root and load");
        sb.AppendLine("project MCP configs.");
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
        sb.AppendLine("## Live MCP endpoint (jrm-remote)");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(mcpEndpointUrl))
        {
            sb.AppendLine("JeekRemoteManager is currently exposing remote tools for **this tab** at:");
            sb.AppendLine();
            sb.AppendLine($"- **MCP server name:** `{McpServerName}`");
            sb.AppendLine($"- **URL:** `{mcpEndpointUrl.Trim()}`");
            sb.AppendLine();
            sb.AppendLine("Project configs (refreshed with the same URL when the AI panel starts):");
            sb.AppendLine();
            sb.AppendLine("| Agent | Config file |");
            sb.AppendLine("|-------|-------------|");
            sb.AppendLine("| Claude Code / Desktop | `.mcp.json` |");
            sb.AppendLine("| Codex | `.codex/config.toml` |");
            sb.AppendLine("| Grok | `.grok/config.toml` |");
            sb.AppendLine();
            sb.AppendLine("Open **this directory** as the project/workspace root in desktop Claude, Codex,");
            sb.AppendLine("or Grok so those files are loaded automatically. Keep JeekRemoteManager running");
            sb.AppendLine("with this connection's terminal open; the endpoint is loopback HTTP and dies");
            sb.AppendLine("when the tab/app stops.");
        }
        else
        {
            sb.AppendLine("No live MCP URL has been written yet. Start the AI panel (or reconnect) inside");
            sb.AppendLine("JeekRemoteManager so this file and the project MCP configs are refreshed with");
            sb.AppendLine("the current loopback endpoint.");
        }

        sb.AppendLine();
        sb.AppendLine("## Tools: local vs remote");
        sb.AppendLine();
        sb.AppendLine("### Remote server (use these first)");
        sb.AppendLine();
        sb.AppendLine("The user's remote server is available **only** through the **jrm-remote** MCP tools");
        sb.AppendLine("that drive the **already-open** interactive terminal for this tab (same shell, same");
        sb.AppendLine("cwd, same environment). They do not open a new SSH session.");
        sb.AppendLine();
        sb.AppendLine("| Tool | Purpose |");
        sb.AppendLine("|------|---------|");
        sb.AppendLine("| `terminal_status` | Read-only: connected? lock free? command/transfer running? |");
        sb.AppendLine("| `connection_info` | Safe metadata (type/target/notes; no secrets) |");
        sb.AppendLine("| `terminal_run` | Run a non-interactive remote command; optional `timeout_seconds` |");
        sb.AppendLine("| `terminal_run_danger` | Same, but asks the user to confirm destructive work |");
        sb.AppendLine("| `terminal_interrupt` | Force-interrupt active command (can run while `terminal_run` is in flight) |");
        sb.AppendLine("| `terminal_reconnect` | Rebuild SSH/WSL when the channel is unhealthy |");
        sb.AppendLine("| `terminal_scrollback` | Read last N lines of the live terminal buffer |");
        sb.AppendLine("| `terminal_send_keys` | Raw keys to the shell (pagers/prompts); does not capture output |");
        sb.AppendLine("| `ask_user` | Ask the user a question (optional multi-choice) in the app UI |");
        sb.AppendLine("| `monitor_snapshot` | CPU/mem/load/disk snapshot when the monitor panel has data |");
        sb.AppendLine("| `file_upload` / `file_download` | Transfer via the interactive shell (ZMODEM on SSH; works through bastion) |");
        sb.AppendLine();
        sb.AppendLine("Prefer **jrm-remote** tools for anything that must run on the connected SSH/WSL");
        sb.AppendLine("session. Prefer **one shell-owning tool call per step** (`terminal_run`, transfers).");
        sb.AppendLine("Status/scrollback/interrupt/send_keys do not take the command lock.");
        sb.AppendLine("There is **no SFTP channel** — transfers and shell share the same session (jump hosts OK).");
        sb.AppendLine("When finished, reply with a short summary and no further tool calls.");
        sb.AppendLine();
        sb.AppendLine("### Local Windows machine");
        sb.AppendLine();
        sb.AppendLine("Your built-in shell / file tools (if any) run on the **local Windows host** where");
        sb.AppendLine("JeekRemoteManager runs — including this workspace directory. Use them only for");
        sb.AppendLine("local-side work (notes, reading files under this workspace, preparing uploads).");
        sb.AppendLine("**Never** assume a local bash command runs on the remote server. Never confuse");
        sb.AppendLine("local tools with the remote session.");
        sb.AppendLine();
        sb.AppendLine("## Safety");
        sb.AppendLine();
        sb.AppendLine("- Use `terminal_run_danger` for deletes, drops, force-push, disk wipe, prune with data, etc.");
        sb.AppendLine("- Prefer non-interactive flags (`-y`, `--yes`, `--no-pager`, `-o cat`) when available.");
        sb.AppendLine("- Do **not** pipe to `less`/`more` or rely on interactive pagers; the host sets `PAGER=cat`.");
        sb.AppendLine("- If the shell seems stuck after a command (e.g. a pager), call `terminal_interrupt` or `terminal_send_keys` with `q`.");
        sb.AppendLine("- Set `timeout_seconds` on long `terminal_run` calls so the host can auto-interrupt.");
        sb.AppendLine("- Call `terminal_status` before claiming the channel is free for re-verification.");
        sb.AppendLine("- Assume Linux on the remote unless the connection type or output says otherwise.");
        sb.AppendLine("- Large remote outputs may arrive as a short preview plus a local file path — that is");
        sb.AppendLine("  complete delivery; read the path with a local tool if you need every line.");
        sb.AppendLine();
        sb.AppendLine("## This workspace");
        sb.AppendLine();
        sb.AppendLine("Files here (including this document and the MCP configs) are machine-local and may");
        sb.AppendLine("be refreshed by JeekRemoteManager when the AI panel opens or restarts. You may");
        sb.AppendLine("create extra notes or artifacts in this folder for the user's local reference.");
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

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup of obsolete sidecar files.
        }
    }

    private static string EscapeJson(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private static string EscapeToml(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
}
