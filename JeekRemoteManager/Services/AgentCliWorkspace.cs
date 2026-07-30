using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using JeekRemoteManager.Models;

namespace JeekRemoteManager.Services;

/// <summary>
/// Resolves a durable per-tab working directory for agent CLIs, keyed by the
/// connection's path under the Connections tree (e.g. <c>vps/bwg</c>) and, when
/// multiple terminal tabs share that connection, the same suffix as the tab header
/// (<c>vps/bwg (2)</c>). Hosts <c>AGENTS.md</c> (full context), a thin
/// <c>CLAUDE.md</c> that includes it, and every project MCP config in
/// <see cref="AgentMcpConfigCatalog"/> so any supported agent or editor picks up the same
/// remote-server context without command-line prompts or flags — while duplicated tabs stay
/// isolated from each other.
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
    /// (e.g. <c>vps/bwg</c>). Extra terminal tabs on the same connection use the same
    /// leaf suffix as the tab header: <c>vps/bwg (2)</c>, <c>vps/bwg (3)</c>, …
    /// Falls back to the connection name when no file path is known.
    /// </summary>
    public static string ResolveRelativePath(
        string connectionsRoot,
        string? sourcePath,
        Connection? connection,
        int sessionNumber = 1)
    {
        var baseRelative = ResolveConnectionRelativePath(connectionsRoot, sourcePath, connection);
        return AppendSessionSegment(baseRelative, sessionNumber);
    }

    /// <summary>
    /// Absolute workspace under <see cref="RootPath"/>/&lt;tree-relative-path&gt;.
    /// Creates the directory, refreshes <c>AGENTS.md</c> (and a <c>CLAUDE.md</c> include), and
    /// writes project MCP configs that desktop and CLI agents load from the working directory
    /// (no command-line MCP/system flags). Those configs launch <c>JeekRemoteManagerMcp.exe</c> pinned to
    /// this connection, so nothing in them expires between app runs.
    /// <paramref name="mcpToolsAutoApprove"/> controls Codex
    /// <c>default_tools_approval_mode</c> (approve vs prompt); do not pass this via
    /// <c>codex -c mcp_servers...</c> — partial MCP overrides fail with "invalid transport".
    /// <paramref name="sessionNumber"/> ≥ 2 isolates duplicated (or otherwise parallel) tabs.
    /// </summary>
    public static string Ensure(
        string connectionsRoot,
        string? sourcePath,
        Connection? connection,
        int sessionNumber = 1,
        bool mcpToolsAutoApprove = true,
        string? workspaceRoot = null)
    {
        var connectionPath = ResolveConnectionRelativePath(connectionsRoot, sourcePath, connection);
        var relative = AppendSessionSegment(connectionPath, sessionNumber);
        var root = string.IsNullOrWhiteSpace(workspaceRoot)
            ? RootPath
            : Path.GetFullPath(workspaceRoot);
        var absolute = Path.GetFullPath(Path.Combine(
            root,
            relative.Replace('/', Path.DirectorySeparatorChar)));

        Directory.CreateDirectory(absolute);
        WriteAgentDocs(absolute, relative, connection, sourcePath, connectionPath, sessionNumber);
        WriteProjectMcpConfigs(absolute, connectionPath, mcpToolsAutoApprove);
        return absolute;
    }

    /// <summary>
    /// Workspace identity handed to <see cref="AgentProjectLink"/> when the user links this
    /// connection into their own project folder.
    /// </summary>
    public static AgentWorkspaceLink BuildLink(
        string connectionsRoot,
        string? sourcePath,
        Connection? connection,
        int sessionNumber = 1,
        bool mcpToolsAutoApprove = true)
    {
        var connectionPath = ResolveConnectionRelativePath(connectionsRoot, sourcePath, connection);
        var relative = AppendSessionSegment(connectionPath, sessionNumber);
        var absolute = Path.GetFullPath(Path.Combine(
            RootPath,
            relative.Replace('/', Path.DirectorySeparatorChar)));
        return BuildLink(absolute, relative, connectionPath, connection, mcpToolsAutoApprove);
    }

    private static AgentWorkspaceLink BuildLink(
        string workspaceDirectory,
        string relativePath,
        string connectionPath,
        Connection? connection,
        bool mcpToolsAutoApprove) =>
        new(
            workspaceDirectory,
            relativePath,
            connectionPath,
            ResolveDisplayName(relativePath, connection),
            ResolveConnectionKind(connection),
            ResolveConnectionTarget(connection),
            mcpToolsAutoApprove);

    private static string ResolveConnectionRelativePath(
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
    /// Session 1 keeps the connection path alone (stable default). Session 2+ renames the
    /// leaf folder to match the tab header: <c>name (N)</c> as a sibling, not a subdirectory.
    /// </summary>
    public static string AppendSessionSegment(string connectionRelativePath, int sessionNumber)
    {
        var basePath = string.IsNullOrWhiteSpace(connectionRelativePath)
            ? "unknown"
            : connectionRelativePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (sessionNumber <= 1)
            return basePath;

        var parent = Path.GetDirectoryName(basePath);
        var leaf = Path.GetFileName(basePath);
        if (string.IsNullOrEmpty(leaf))
            leaf = "unknown";

        // Same text as the tab chrome: "Name" + " (2)" beside it → folder "Name (2)".
        var leafWithSession = SanitizeSegment($"{leaf} ({sessionNumber})");
        return string.IsNullOrEmpty(parent)
            ? leafWithSession
            : Path.Combine(parent, leafWithSession);
    }

    private static void WriteAgentDocs(
        string workspaceDir,
        string relativePath,
        Connection? connection,
        string? sourcePath,
        string connectionPath,
        int sessionNumber)
    {
        var body = BuildAgentDocBody(relativePath, connection, sourcePath, connectionPath, sessionNumber);
        // Full context lives in AGENTS.md (Codex/Grok/shared). Claude reads CLAUDE.md which
        // only includes AGENTS.md so we do not maintain two copies.
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        File.WriteAllText(Path.Combine(workspaceDir, "AGENTS.md"), body, utf8);
        File.WriteAllText(Path.Combine(workspaceDir, "CLAUDE.md"), "@AGENTS.md\n", utf8);
    }

    /// <summary>
    /// Writes every project-level MCP config in <see cref="AgentMcpConfigCatalog.All"/> so any
    /// agent that opens this directory reaches this connection. Each launches
    /// <c>JeekRemoteManagerMcp.exe --connection &lt;tree path&gt;</c>, the stdio adapter that talks to
    /// the app over a named pipe — there is no port or token here, so these files stay valid
    /// across app restarts and the workspace can be opened cold.
    ///
    /// This directory is generated and owned by JeekRemoteManager, so each config is rewritten
    /// whole. <see cref="AgentProjectLink"/> merges the same entries into folders we do not own.
    /// </summary>
    /// <param name="mcpToolsAutoApprove">
    /// When true, Codex uses <c>default_tools_approval_mode = "approve"</c>; otherwise
    /// <c>"prompt"</c>. Must live in its config file — not in <c>codex -c</c> overrides — because
    /// partial <c>mcp_servers.*</c> CLI patches without <c>url</c>/<c>command</c> fail with
    /// "invalid transport".
    /// </param>
    public static void WriteProjectMcpConfigs(
        string workspaceDir,
        string connectionPath,
        bool mcpToolsAutoApprove = true)
    {
        Directory.CreateDirectory(workspaceDir);
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var adapter = AgentWorkspaceLink.AdapterPath;
        var connection = connectionPath.Replace('\\', '/').Trim('/');

        // Legacy Claude sidecar used with --mcp-config; project agents load .mcp.json instead.
        TryDelete(Path.Combine(workspaceDir, "jrm-mcp.json"));
        // Legacy registry of linked projects, kept when project configs still held an
        // expiring URL. Writing into a project folder is a one-shot action now.
        TryDelete(Path.Combine(workspaceDir, "linked-projects.json"));

        foreach (var target in AgentMcpConfigCatalog.All)
        {
            var path = target.ResolvePath(workspaceDir);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var content = target.Format == AgentMcpConfigCatalog.ConfigFormat.Json
                ? new JsonObject
                    {
                        [target.JsonRootKey!] = new JsonObject
                        {
                            [McpServerName] = AgentMcpConfigCatalog.BuildJsonEntry(adapter, connection),
                        },
                    }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n"
                : "# Generated by JeekRemoteManager — per-connection remote tools\n"
                  + $"# Open this workspace folder in {target.Label}; AGENTS.md has full context.\n"
                  + AgentMcpConfigCatalog.BuildTomlEntry(
                      target, McpServerName, adapter, connection, mcpToolsAutoApprove);

            File.WriteAllText(path, content, utf8);
        }

        // Claude treats project-scoped .mcp.json servers as untrusted until the user
        // approves them. This workspace is generated by JRM itself and its adapter is
        // pinned to the current connection, so approve only our server in local project
        // settings. Without this, a fresh connection silently starts Claude without any
        // jrm-remote tools even though --allowedTools contains their names.
        WriteClaudeMcpApproval(workspaceDir, utf8);
    }

    private static void WriteClaudeMcpApproval(string workspaceDir, Encoding utf8)
    {
        var claudeDir = Path.Combine(workspaceDir, ".claude");
        var settingsPath = Path.Combine(claudeDir, "settings.local.json");
        Directory.CreateDirectory(claudeDir);

        JsonObject root;
        try
        {
            root = File.Exists(settingsPath)
                ? JsonNode.Parse(
                    File.ReadAllText(settingsPath),
                    documentOptions: new JsonDocumentOptions
                    {
                        CommentHandling = JsonCommentHandling.Skip,
                        AllowTrailingCommas = true,
                    }) as JsonObject ?? new JsonObject()
                : new JsonObject();
        }
        catch (JsonException)
        {
            root = new JsonObject();
        }

        var enabled = root["enabledMcpjsonServers"] as JsonArray ?? new JsonArray();
        if (!enabled.Any(node =>
                string.Equals(node?.GetValue<string>(), McpServerName, StringComparison.Ordinal)))
        {
            enabled.Add(McpServerName);
        }
        root["enabledMcpjsonServers"] = enabled;

        if (root["disabledMcpjsonServers"] is JsonArray disabled)
        {
            for (var i = disabled.Count - 1; i >= 0; i--)
            {
                if (string.Equals(
                        disabled[i]?.GetValue<string>(),
                        McpServerName,
                        StringComparison.Ordinal))
                {
                    disabled.RemoveAt(i);
                }
            }
        }

        File.WriteAllText(
            settingsPath,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n",
            utf8);
    }

    private static string BuildAgentDocBody(
        string relativePath,
        Connection? connection,
        string? sourcePath,
        string connectionPath,
        int sessionNumber)
    {
        var name = ResolveDisplayName(relativePath, connection);
        var kind = ResolveConnectionKind(connection);
        var target = ResolveConnectionTarget(connection);
        var notes = connection?.Notes?.Trim();
        var sb = new StringBuilder();
        sb.AppendLine("# JeekRemoteManager agent workspace");
        sb.AppendLine();
        sb.AppendLine("This directory is the **local** working directory for the agent CLI (or desktop");
        sb.AppendLine("app) attached to **one** JeekRemoteManager terminal tab. It is **not** the remote");
        sb.AppendLine("server filesystem. Other tabs on the same connection use separate workspace folders.");
        sb.AppendLine();
        sb.AppendLine("All operational context for this connection lives in this file (`AGENTS.md`).");
        sb.AppendLine("`CLAUDE.md` only includes it (`@AGENTS.md`). Do **not** expect system prompts or");
        sb.AppendLine("server details on the command line — open this folder as the project root and load");
        sb.AppendLine("project MCP configs.");
        sb.AppendLine();
        sb.AppendLine("## Primary goal");
        sb.AppendLine();
        sb.AppendLine("You are assisting the user **inside JeekRemoteManager** to operate a remote server");
        sb.AppendLine($"(workspace path: `{relativePath.Replace('\\', '/')}`, display name: **{name}**).");
        sb.AppendLine("Almost all useful work happens on that remote session, not on this Windows machine.");
        sb.AppendLine();
        sb.AppendLine("## Connection");
        sb.AppendLine();
        sb.AppendLine($"- **Type:** {kind}");
        sb.AppendLine($"- **Target:** {target}");
        sb.AppendLine($"- **Workspace path:** `{relativePath.Replace('\\', '/')}`");
        sb.AppendLine($"- **Terminal tab session:** {Math.Max(1, sessionNumber)}");
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
        sb.AppendLine($"## Remote tools ({McpServerName})");
        sb.AppendLine();
        sb.AppendLine("Project configs in this folder register an MCP server that reaches this");
        sb.AppendLine("connection. They launch a small local adapter which talks to JeekRemoteManager");
        sb.AppendLine("over a named pipe, so there is no URL, port, or token to expire:");
        sb.AppendLine();
        sb.AppendLine($"- **MCP server name:** `{McpServerName}`");
        sb.AppendLine(
            $"- **Pinned connection:** `{connectionPath.Replace('\\', '/')}` "
            + "(you may omit the `connection` argument)");
        sb.AppendLine($"- **Adapter:** `{AgentWorkspaceLink.AdapterPath}`");
        sb.AppendLine();
        foreach (var line in AgentMcpConfigCatalog.DocTableLines())
            sb.AppendLine(line);
        sb.AppendLine();
        sb.AppendLine("Open **this directory** as the project/workspace root in your agent or editor");
        sb.AppendLine("so those files load automatically. The adapter starts JeekRemoteManager");
        sb.AppendLine("if it is closed and reconnects by itself if it restarts; if no terminal tab is");
        sb.AppendLine("open for this connection, call `session_open` first (`session_list` shows what");
        sb.AppendLine("is live).");
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

    /// <summary>Connection name, falling back to the workspace path's leaf folder.</summary>
    private static string ResolveDisplayName(string relativePath, Connection? connection)
    {
        var name = connection?.Name?.Trim();
        return string.IsNullOrEmpty(name)
            ? Path.GetFileName(relativePath.Replace('\\', '/'))
            : name;
    }

    private static string ResolveConnectionKind(Connection? connection) =>
        connection?.IsWsl == true
            ? "WSL"
            : connection?.IsRdp == true
                ? "RDP"
                : "SSH";

    private static string ResolveConnectionTarget(Connection? connection) =>
        connection?.IsWsl == true
            ? (string.IsNullOrWhiteSpace(connection.WslDistro)
                ? "default WSL distribution"
                : connection.WslDistro.Trim())
            : string.IsNullOrWhiteSpace(connection?.Host)
                ? "(unknown host)"
                : $"{connection!.Username}@{connection.Host}:{connection.Port}";

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
}
