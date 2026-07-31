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
/// workspace, plus an MCP server entry in each config listed in
/// <see cref="AgentMcpConfigCatalog"/>. Agents opened in that folder then reach the connection
/// without the user opening the workspace. Unlike the generated workspace, this folder belongs
/// to the user: entries are merged in and removed again, never written over.
///
/// This is a one-shot write, not an association: the MCP entry launches the local
/// <c>JeekRemoteManagerMcp</c> adapter over a named pipe, so there is no URL, port, or token that could
/// expire and nothing to keep in sync afterwards.
/// </summary>
public static class AgentProjectLink
{
    private static readonly ILogger Log = LogManager.CreateLogger(nameof(AgentProjectLink));

    private const string BlockLabel = "JeekRemoteManager link";
    private const string ApplicationMarker = "application";

    /// <summary>MCP server name used for the unpinned, application-wide product surface.</summary>
    public const string ApplicationMcpServerName = "jeek-remote-manager";

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Writes the reference block and MCP configs into <paramref name="projectDirectory"/>.
    /// Re-running replaces the block in place. Returns the normalized project path.
    /// </summary>
    public static string WriteInto(AgentWorkspaceLink link, string projectDirectory)
    {
        var project = NormalizeDirectory(projectDirectory);
        var workspace = NormalizeDirectory(link.WorkspaceDirectory);
        ValidateProjectDirectory(project, workspace);
        Apply(
            project,
            link.NormalizedRelativePath,
            link.ProjectMcpServerName,
            BuildReferenceBlock(link),
            link.NormalizedConnectionPath,
            link.McpToolsAutoApprove);
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
            RemoveBlocks(project, link.NormalizedRelativePath, link.ProjectMcpServerName);
        return project;
    }

    /// <summary>
    /// Writes the application-wide product MCP entry into a local project. Unlike
    /// <see cref="WriteInto"/>, the adapter is not pinned to a connection, so the agent can browse
    /// and manage the whole connection tree and address any terminal session.
    /// </summary>
    public static string WriteApplicationInto(string projectDirectory, bool mcpToolsAutoApprove)
    {
        var project = NormalizeDirectory(projectDirectory);
        ValidateProjectDirectory(project);
        Apply(
            project,
            ApplicationMarker,
            ApplicationMcpServerName,
            BuildApplicationReferenceBlock(),
            connectionPath: null,
            mcpToolsAutoApprove);
        return project;
    }

    /// <summary>Removes only the application-wide block and MCP entry from a project.</summary>
    public static string RemoveApplicationFrom(string projectDirectory)
    {
        var project = NormalizeDirectory(projectDirectory);
        if (Directory.Exists(project))
            RemoveBlocks(project, ApplicationMarker, ApplicationMcpServerName);
        return project;
    }

    private static void Apply(
        string projectDirectory,
        string marker,
        string serverName,
        string body,
        string? connectionPath,
        bool mcpToolsAutoApprove)
    {
        UpsertMarkdownBlock(Path.Combine(projectDirectory, "AGENTS.md"), marker, body);

        // Agents with their own context file name (Claude) do not read AGENTS.md by
        // themselves. Create the same thin include the workspace uses when the project has none;
        // otherwise only duplicate the block when that file does not already import AGENTS.md.
        foreach (var include in AgentMcpConfigCatalog.ContextIncludeFiles)
        {
            var path = Path.Combine(projectDirectory, include);
            if (!File.Exists(path))
                File.WriteAllText(path, AgentMcpConfigCatalog.ContextIncludeBody + "\n", Utf8);
            else if (!ImportsAgentsMd(ReadTextOrEmpty(path)))
                UpsertMarkdownBlock(path, marker, body);
        }

        WriteProjectMcpConfigs(
            projectDirectory,
            marker,
            serverName,
            connectionPath,
            mcpToolsAutoApprove);
    }

    /// <summary>
    /// Leaves the project as it was before: the block and MCP entry go, and files or folders
    /// that exist only because we wrote them are deleted instead of left behind empty.
    /// </summary>
    private static void RemoveBlocks(string projectDirectory, string marker, string serverName)
    {
        RemoveMarkdownBlock(Path.Combine(projectDirectory, "AGENTS.md"), marker);
        DeleteIfEmpty(Path.Combine(projectDirectory, "AGENTS.md"));

        // An include file still holding nothing but the line we created is ours to remove.
        foreach (var include in AgentMcpConfigCatalog.ContextIncludeFiles)
        {
            var path = Path.Combine(projectDirectory, include);
            RemoveMarkdownBlock(path, marker);
            if (ReadTextOrEmpty(path).Trim() == AgentMcpConfigCatalog.ContextIncludeBody)
                TryDeleteFile(path);
            DeleteIfEmpty(path);
        }

        foreach (var target in AgentMcpConfigCatalog.All)
        {
            var path = target.ResolvePath(projectDirectory);
            if (target.Format == AgentMcpConfigCatalog.ConfigFormat.Json)
            {
                // Drops the file itself when our entry was all it held; the folder may still
                // be the project's own (.vscode), so only remove it once it is empty.
                RemoveMcpJsonServer(path, target.JsonRootKey!, serverName);
                if (target.HasOwnFolder)
                    TryDeleteEmptyFolder(path);
            }
            else
            {
                RemoveTomlBlock(path, marker);
                DeleteIfEmpty(path, removeEmptyFolder: target.HasOwnFolder);
            }
        }
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
        foreach (var line in AgentMcpConfigCatalog.DocTableLines())
            sb.Append(line).Append('\n');
        return sb.ToString();
    }

    private static string BuildApplicationReferenceBlock()
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Control JeekRemoteManager through MCP");
        sb.AppendLine();
        sb.Append("JeekRemoteManager exposes its whole application through the **")
          .Append(ApplicationMcpServerName)
          .AppendLine("** MCP server configured in this project.");
        sb.AppendLine();
        sb.AppendLine("- Start with `connection_list` to inspect saved SSH, WSL, and RDP connections.");
        sb.AppendLine("- Use `session_list` and `session_open` to find or open terminal tabs, then pass the returned session or connection to `terminal_status`, `terminal_run`, file-transfer, and monitor tools.");
        sb.AppendLine("- This application-wide server is not pinned to one connection. It can manage the connection tree and control any open or saved connection, subject to the tool's confirmation rules.");
        sb.AppendLine("- Use `terminal_run_danger` for destructive work so the user is asked to confirm in the JeekRemoteManager window.");
        sb.AppendLine("- Passwords and two-factor codes are entered in that window and are never returned by MCP tools.");
        sb.AppendLine("- The local adapter starts JeekRemoteManager if needed and talks to it over a named pipe; there is no URL, port, or token to expire.");
        sb.AppendLine();
        foreach (var line in AgentMcpConfigCatalog.DocTableLines())
            sb.AppendLine(line);
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
    private static void WriteProjectMcpConfigs(
        string projectDirectory,
        string marker,
        string serverName,
        string? connectionPath,
        bool mcpToolsAutoApprove)
    {
        var adapter = AgentWorkspaceLink.AdapterPath;

        foreach (var target in AgentMcpConfigCatalog.All)
        {
            var path = target.ResolvePath(projectDirectory);
            if (target.Format == AgentMcpConfigCatalog.ConfigFormat.Json)
            {
                MergeMcpJson(
                    path,
                    target.JsonRootKey!,
                    serverName,
                    AgentMcpConfigCatalog.BuildJsonEntry(adapter, connectionPath));
            }
            else
            {
                UpsertTomlBlock(
                    path,
                    marker,
                    AgentMcpConfigCatalog.BuildTomlEntry(
                        target,
                        serverName,
                        adapter,
                        connectionPath,
                        mcpToolsAutoApprove));
            }
        }
    }

    private static void MergeMcpJson(string path, string rootKey, string serverName, JsonObject entry)
    {
        var root = ParseJsonObject(path) ?? new JsonObject();
        if (root[rootKey] is not JsonObject servers)
        {
            servers = new JsonObject();
            root[rootKey] = servers;
        }

        servers[serverName] = entry;
        WriteJson(path, root);
    }

    private static void RemoveMcpJsonServer(string path, string rootKey, string serverName)
    {
        if (ParseJsonObject(path) is not { } root)
            return;
        if (root[rootKey] is not JsonObject servers || !servers.Remove(serverName))
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
        if (removeEmptyFolder)
            TryDeleteEmptyFolder(path);
    }

    /// <summary>Removes the folder holding <paramref name="path"/> once nothing is left in it.</summary>
    private static void TryDeleteEmptyFolder(string path)
    {
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

    private static void ValidateProjectDirectory(string project, string? generatedWorkspace = null)
    {
        if (!Directory.Exists(project))
            throw new DirectoryNotFoundException(project);

        if ((generatedWorkspace is not null
             && string.Equals(project, generatedWorkspace, StringComparison.OrdinalIgnoreCase))
            || IsInsideWorkspaceRoot(project))
        {
            throw new InvalidOperationException(
                "Pick a project folder outside the JeekRemoteManager agent workspaces.");
        }
    }

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

    #endregion
}
