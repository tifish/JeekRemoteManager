using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using JeekTools;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace JeekRemoteManager.Services;

/// <summary>
/// Identity and live endpoint of one terminal tab's agent workspace, as needed to link
/// the connection into an unrelated local project folder.
/// </summary>
/// <param name="WorkspaceDirectory">Absolute workspace folder under <c>AgentWorkspaces</c>.</param>
/// <param name="RelativePath">Tree path of that workspace, session suffix included (<c>vps/bwg (2)</c>).</param>
/// <param name="ConnectionPath">Tree path of the connection itself, no session suffix.</param>
public sealed record AgentWorkspaceLink(
    string WorkspaceDirectory,
    string RelativePath,
    string ConnectionPath,
    string DisplayName,
    string ConnectionKind,
    string Target,
    bool McpToolsAutoApprove)
{
    /// <summary>Tree path with forward slashes — used in markers, headings, and slugs.</summary>
    public string NormalizedRelativePath => RelativePath.Replace('\\', '/').Trim('/');

    /// <summary>Connection tree path with forward slashes, passed to the adapter as --connection.</summary>
    public string NormalizedConnectionPath => ConnectionPath.Replace('\\', '/').Trim('/');

    /// <summary>
    /// MCP server name used inside linked projects. Suffixed per connection so several
    /// JeekRemoteManager tabs can be linked into the same project without colliding.
    /// </summary>
    public string ProjectMcpServerName =>
        $"{AgentCliWorkspace.McpServerName}-{AgentProjectLink.Slugify(NormalizedRelativePath)}";

    /// <summary>
    /// The stdio adapter agents launch. It sits beside the app, derives the pipe name from
    /// its own folder, and reconnects on its own — so a linked project's config is written
    /// once and never goes stale, unlike the loopback URL this replaced.
    /// </summary>
    public static string AdapterPath =>
        Path.Combine(AppContext.BaseDirectory, "JeekRemoteManagerMcp.exe");
}

/// <summary>
/// Writes a JeekRemoteManager connection into an arbitrary local project folder: a marked
/// block in the project's <c>AGENTS.md</c> (and <c>CLAUDE.md</c>) pointing at the tab's agent
/// workspace, plus an MCP server entry in the project's Claude/Codex/Grok configs. Agents
/// opened in that folder then reach the connection without the user opening the workspace.
///
/// This is a one-shot write, not an association: the MCP entry launches the local
/// <c>JeekRemoteManagerMcp</c> adapter over a named pipe, so there is no URL, port, or token that could
/// expire and nothing to keep in sync afterwards.
/// </summary>
public static class AgentProjectLink
{
    private static readonly ILogger Log = LogManager.CreateLogger(nameof(AgentProjectLink));

    private const string BlockLabel = "JeekRemoteManager link";

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Writes the reference block and MCP configs into <paramref name="projectDirectory"/>.
    /// Re-running replaces the block in place. Returns the normalized project path.
    /// </summary>
    public static string WriteInto(AgentWorkspaceLink link, string projectDirectory)
    {
        var project = NormalizeDirectory(projectDirectory);
        if (!Directory.Exists(project))
            throw new DirectoryNotFoundException(project);

        var workspace = NormalizeDirectory(link.WorkspaceDirectory);
        if (string.Equals(project, workspace, StringComparison.OrdinalIgnoreCase)
            || IsInsideWorkspaceRoot(project))
        {
            throw new InvalidOperationException(
                "Pick a project folder outside the JeekRemoteManager agent workspaces.");
        }

        Apply(link, project);
        return project;
    }

    /// <summary>
    /// Takes this connection's block and MCP entry back out of a project folder. Everything
    /// the project had before is preserved; files that existed only for us are deleted.
    /// </summary>
    public static string RemoveFrom(AgentWorkspaceLink link, string projectDirectory)
    {
        var project = NormalizeDirectory(projectDirectory);
        if (Directory.Exists(project))
            RemoveBlocks(link, project);
        return project;
    }

    private static void Apply(AgentWorkspaceLink link, string projectDirectory)
    {
        var relative = link.NormalizedRelativePath;
        var body = BuildReferenceBlock(link);

        UpsertMarkdownBlock(Path.Combine(projectDirectory, "AGENTS.md"), relative, body);

        // Claude reads CLAUDE.md. Create the same thin include the workspace uses when the
        // project has none; otherwise only duplicate the block when it does not import AGENTS.md.
        var claudeMd = Path.Combine(projectDirectory, "CLAUDE.md");
        if (!File.Exists(claudeMd))
            File.WriteAllText(claudeMd, "@AGENTS.md\n", Utf8);
        else if (!ImportsAgentsMd(ReadTextOrEmpty(claudeMd)))
            UpsertMarkdownBlock(claudeMd, relative, body);

        WriteProjectMcpConfigs(link, projectDirectory);
    }

    /// <summary>
    /// Leaves the project as it was before: the block and MCP entry go, and files or folders
    /// that exist only because we wrote them are deleted instead of left behind empty.
    /// </summary>
    private static void RemoveBlocks(AgentWorkspaceLink link, string projectDirectory)
    {
        var relative = link.NormalizedRelativePath;

        RemoveMarkdownBlock(Path.Combine(projectDirectory, "AGENTS.md"), relative);
        DeleteIfEmpty(Path.Combine(projectDirectory, "AGENTS.md"));

        // A CLAUDE.md still holding nothing but the include we created is ours to remove.
        var claudeMd = Path.Combine(projectDirectory, "CLAUDE.md");
        RemoveMarkdownBlock(claudeMd, relative);
        if (ReadTextOrEmpty(claudeMd).Trim() == "@AGENTS.md")
            TryDeleteFile(claudeMd);
        DeleteIfEmpty(claudeMd);

        RemoveTomlBlock(Path.Combine(projectDirectory, ".codex", "config.toml"), relative);
        DeleteIfEmpty(Path.Combine(projectDirectory, ".codex", "config.toml"), removeEmptyFolder: true);
        RemoveTomlBlock(Path.Combine(projectDirectory, ".grok", "config.toml"), relative);
        DeleteIfEmpty(Path.Combine(projectDirectory, ".grok", "config.toml"), removeEmptyFolder: true);

        RemoveMcpJsonServer(Path.Combine(projectDirectory, ".mcp.json"), link.ProjectMcpServerName);
    }

    #region Reference block

    private static string BuildReferenceBlock(AgentWorkspaceLink link)
    {
        var relative = link.NormalizedRelativePath;
        var connection = link.NormalizedConnectionPath;
        var server = link.ProjectMcpServerName;
        var workspaceAgents = Path.Combine(link.WorkspaceDirectory, "AGENTS.md");

        var sb = new StringBuilder();
        sb.Append("## Remote server via JeekRemoteManager — `").Append(relative).Append("`\n\n");
        sb.Append("JeekRemoteManager keeps an interactive **")
          .Append(link.ConnectionKind)
          .Append("** session open for **")
          .Append(link.DisplayName)
          .Append("** (`")
          .Append(link.Target)
          .Append("`) and exposes it to you through the **")
          .Append(server)
          .Append("** MCP server configured in this project.\n\n");
        sb.Append("- Use the `").Append(server).Append("` tools (`terminal_run`, `terminal_status`, ")
          .Append("`terminal_scrollback`, `file_upload`, `file_download`, …) for anything that must run ")
          .Append("on that server. They drive the already-open shell — same cwd, same environment — ")
          .Append("and do not open a new SSH session.\n");
        sb.Append("- This server is pinned to `").Append(connection)
          .Append("`, so you can omit the `connection` argument. If no tab is open for it, call ")
          .Append("`session_open` first; `session_list` shows what is live.\n");
        sb.Append("- Your built-in shell and file tools run on the **local machine** and on **this ")
          .Append("project folder**, never on the remote server. Never assume a local command reaches it.\n");
        sb.Append("- Use `terminal_run_danger` for destructive work (deletes, drops, force-push, ")
          .Append("disk wipes) so the user is asked to confirm in the JeekRemoteManager window.\n");
        sb.Append("- Passwords and two-factor codes are typed by the user in that window and are ")
          .Append("never accepted as tool arguments; no tool returns a stored password.\n");
        sb.Append("- Full operating rules, tool table, and safety notes for this connection:\n  `")
          .Append(workspaceAgents).Append("`\n");
        sb.Append("- The configs below launch a local adapter that talks to JeekRemoteManager over a ")
          .Append("named pipe; it starts the app if it is closed and survives restarts, so there is ")
          .Append("no URL to expire. JeekRemoteManager rewrites this block — do not edit it by hand.\n\n");
        sb.Append("| Agent | Config file |\n");
        sb.Append("|-------|-------------|\n");
        sb.Append("| Claude Code / Desktop | `.mcp.json` |\n");
        sb.Append("| Codex | `.codex/config.toml` |\n");
        sb.Append("| Grok | `.grok/config.toml` |\n");
        return sb.ToString();
    }

    private static bool ImportsAgentsMd(string claudeMd) =>
        claudeMd.Contains("@AGENTS.md", StringComparison.OrdinalIgnoreCase);

    #endregion

    #region Project MCP configs

    /// <summary>
    /// Merges this connection's MCP server into the project's agent configs without disturbing
    /// entries the project already had. The entry is a stdio launch of the adapter pinned to
    /// this connection — no URL, no port, no token, so nothing here expires between app runs.
    /// </summary>
    private static void WriteProjectMcpConfigs(AgentWorkspaceLink link, string projectDirectory)
    {
        var adapter = AgentWorkspaceLink.AdapterPath;
        var connection = link.NormalizedConnectionPath;
        var server = link.ProjectMcpServerName;
        var relative = link.NormalizedRelativePath;

        MergeMcpJson(Path.Combine(projectDirectory, ".mcp.json"), server, adapter, connection);

        var codexApproval = link.McpToolsAutoApprove ? "approve" : "prompt";
        UpsertTomlBlock(
            Path.Combine(projectDirectory, ".codex", "config.toml"),
            relative,
            $"[mcp_servers.{server}]\n"
            + $"command = \"{EscapeToml(adapter)}\"\n"
            + $"args = [\"--connection\", \"{EscapeToml(connection)}\"]\n"
            + $"default_tools_approval_mode = \"{codexApproval}\"\n");

        UpsertTomlBlock(
            Path.Combine(projectDirectory, ".grok", "config.toml"),
            relative,
            $"[mcp_servers.{server}]\n"
            + $"command = \"{EscapeToml(adapter)}\"\n"
            + $"args = [\"--connection\", \"{EscapeToml(connection)}\"]\n");
    }

    private static void MergeMcpJson(string path, string serverName, string adapter, string connection)
    {
        var root = ParseJsonObject(path) ?? new JsonObject();
        if (root["mcpServers"] is not JsonObject servers)
        {
            servers = new JsonObject();
            root["mcpServers"] = servers;
        }

        servers[serverName] = new JsonObject
        {
            ["type"] = "stdio",
            ["command"] = adapter,
            ["args"] = new JsonArray("--connection", connection),
        };

        WriteJson(path, root);
    }

    private static void RemoveMcpJsonServer(string path, string serverName)
    {
        if (ParseJsonObject(path) is not { } root)
            return;
        if (root["mcpServers"] is not JsonObject servers || !servers.Remove(serverName))
            return;

        // Nothing but the (now empty) server map left: the file only existed for this link.
        if (servers.Count == 0 && root.Count == 1)
        {
            TryDeleteFile(path);
            return;
        }

        WriteJson(path, root);
    }

    private static JsonObject? ParseJsonObject(string path)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            return JsonNode.Parse(
                File.ReadAllText(path),
                documentOptions: new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                }) as JsonObject;
        }
        catch (JsonException)
        {
            // Unreadable project config: start from scratch rather than failing the link.
            return null;
        }
    }

    private static void WriteJson(string path, JsonObject root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(
            path,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n",
            Utf8);
    }

    #endregion

    #region Marked blocks

    private static string MarkdownBegin(string relative) => $"<!-- BEGIN {BlockLabel}: {relative} -->";

    private static string MarkdownEnd(string relative) => $"<!-- END {BlockLabel}: {relative} -->";

    private static string TomlBegin(string relative) => $"# >>> {BlockLabel}: {relative} >>>";

    private static string TomlEnd(string relative) => $"# <<< {BlockLabel}: {relative} <<<";

    private static void UpsertMarkdownBlock(string path, string relative, string body)
    {
        var begin = MarkdownBegin(relative);
        var end = MarkdownEnd(relative);
        var existing = ReadTextOrEmpty(path);
        var block = begin + "\n\n" + body.TrimEnd('\r', '\n') + "\n\n" + end + "\n";

        var (start, stop) = FindBlock(existing, begin, end);
        var updated = start >= 0
            ? existing[..start] + block + existing[stop..]
            : existing.Length == 0
                ? block
                : existing.TrimEnd('\r', '\n') + "\n\n" + block;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, updated, Utf8);
    }

    private static void RemoveMarkdownBlock(string path, string relative)
    {
        if (!File.Exists(path))
            return;

        var existing = ReadTextOrEmpty(path);
        var updated = RemoveBlock(existing, MarkdownBegin(relative), MarkdownEnd(relative));
        if (!ReferenceEquals(updated, existing))
            File.WriteAllText(path, updated, Utf8);
    }

    /// <summary>
    /// Replaces the block, always re-appending it at the end of the file: a TOML table
    /// header captures every key that follows it, so our table must never sit above
    /// bare keys belonging to the project's own configuration.
    /// </summary>
    private static void UpsertTomlBlock(string path, string relative, string body)
    {
        var begin = TomlBegin(relative);
        var end = TomlEnd(relative);
        var existing = RemoveBlock(ReadTextOrEmpty(path), begin, end).TrimEnd('\r', '\n');
        var block = begin + "\n"
                    + "# Generated by JeekRemoteManager — per-connection remote tools. Do not edit.\n"
                    + body.TrimEnd('\r', '\n') + "\n"
                    + end + "\n";

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, existing.Length == 0 ? block : existing + "\n\n" + block, Utf8);
    }

    private static void RemoveTomlBlock(string path, string relative)
    {
        if (!File.Exists(path))
            return;

        var existing = ReadTextOrEmpty(path);
        var updated = RemoveBlock(existing, TomlBegin(relative), TomlEnd(relative));
        if (!ReferenceEquals(updated, existing))
            File.WriteAllText(path, updated, Utf8);
    }

    /// <summary>Start index of the begin marker and the index just past the end marker (-1 when absent).</summary>
    private static (int Start, int Stop) FindBlock(string text, string begin, string end)
    {
        var start = text.IndexOf(begin, StringComparison.Ordinal);
        if (start < 0)
            return (-1, -1);

        var endIndex = text.IndexOf(end, start, StringComparison.Ordinal);
        var stop = endIndex < 0 ? text.Length : endIndex + end.Length;

        // Swallow the single line break that terminated the block, not following blank lines.
        if (stop < text.Length && text[stop] == '\r')
            stop++;
        if (stop < text.Length && text[stop] == '\n')
            stop++;
        return (start, stop);
    }

    private static string RemoveBlock(string text, string begin, string end)
    {
        var (start, stop) = FindBlock(text, begin, end);
        if (start < 0)
            return text;

        var head = text[..start].TrimEnd('\r', '\n');
        var tail = text[stop..].TrimStart('\r', '\n');
        if (head.Length == 0)
            return tail;
        return tail.Length == 0 ? head + "\n" : head + "\n\n" + tail;
    }

    #endregion

    #region Helpers

    private static string ReadTextOrEmpty(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : "";
        }
        catch (IOException)
        {
            return "";
        }
    }

    /// <summary>Drops a file left blank by unlinking, and optionally its now-empty folder.</summary>
    private static void DeleteIfEmpty(string path, bool removeEmptyFolder = false)
    {
        if (!File.Exists(path) || ReadTextOrEmpty(path).Trim().Length != 0)
            return;

        TryDeleteFile(path);
        if (!removeEmptyFolder)
            return;

        try
        {
            var folder = Path.GetDirectoryName(path);
            if (folder is not null
                && Directory.Exists(folder)
                && !Directory.EnumerateFileSystemEntries(folder).Any())
            {
                Directory.Delete(folder);
            }
        }
        catch (IOException)
        {
            // Best-effort tidy-up.
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // Best-effort tidy-up.
        }
    }

    private static string NormalizeDirectory(string path) =>
        Path.GetFullPath(path.Trim())
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool IsInsideWorkspaceRoot(string path)
    {
        var root = NormalizeDirectory(AgentCliWorkspace.RootPath) + Path.DirectorySeparatorChar;
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Connection path to a TOML/MCP-safe suffix: <c>vps/bwg (2)</c> → <c>vps-bwg-2</c>.</summary>
    internal static string Slugify(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (char.IsAsciiLetterOrDigit(c))
                sb.Append(char.ToLowerInvariant(c));
            else if (c is '_')
                sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-')
                sb.Append('-');
        }

        var slug = sb.ToString().Trim('-');
        return slug.Length == 0 ? "connection" : slug;
    }

    private static string EscapeToml(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    #endregion
}
