using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;

namespace JeekRemoteManager.Services;

/// <summary>
/// The project-level MCP config files JeekRemoteManager writes so an agent opened on a folder
/// reaches this connection, and how each one spells the same stdio entry.
///
/// Two callers share this list with different write semantics:
/// <see cref="AgentCliWorkspace"/> owns its generated workspace and rewrites whole files, while
/// <see cref="AgentProjectLink"/> writes into the user's own project and must merge — marked
/// blocks for TOML, key-by-key merges for JSON. Keeping the file list, root key names, and entry
/// shape here is what stops those two from drifting apart as agents are added.
/// </summary>
public static class AgentMcpConfigCatalog
{
    public enum ConfigFormat
    {
        /// <summary>JSON object of server name → entry, under <see cref="Target.JsonRootKey"/>.</summary>
        Json,

        /// <summary>TOML <c>[mcp_servers.&lt;name&gt;]</c> table.</summary>
        Toml,
    }

    /// <param name="Label">Agent names shown in the generated AGENTS.md table.</param>
    /// <param name="RelativePath">Config path relative to the project/workspace root, forward-slashed.</param>
    /// <param name="JsonRootKey">
    /// Root key holding the server map, for <see cref="ConfigFormat.Json"/> only. Claude, Copilot
    /// CLI and Cursor read <c>mcpServers</c>; VS Code reads <c>servers</c> and silently ignores a
    /// file that uses the other spelling — no error, the tools just never appear.
    /// </param>
    /// <param name="SupportsApprovalMode">
    /// Codex only: <c>default_tools_approval_mode</c> must live in this file rather than a
    /// <c>codex -c</c> override, because a partial <c>mcp_servers.*</c> patch without
    /// <c>url</c>/<c>command</c> fails to load with "invalid transport".
    /// </param>
    public sealed record Target(
        string Label,
        string RelativePath,
        ConfigFormat Format,
        string? JsonRootKey = null,
        bool SupportsApprovalMode = false)
    {
        /// <summary>True when the config sits in a dedicated folder we may tidy up when empty.</summary>
        public bool HasOwnFolder => RelativePath.Contains('/');

        /// <summary>Absolute path of this config under <paramref name="root"/>.</summary>
        public string ResolvePath(string root) =>
            Path.Combine(root, RelativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>
    /// Every config written for a workspace or a linked project. Claude and Copilot CLI share
    /// <c>.mcp.json</c>; VS Code needs its own file only because of the root key name.
    /// </summary>
    public static IReadOnlyList<Target> All { get; } =
    [
        new("Claude Code / Desktop, Copilot CLI", ".mcp.json", ConfigFormat.Json, "mcpServers"),
        new("VS Code (Copilot)", ".vscode/mcp.json", ConfigFormat.Json, "servers"),
        new("Codex", ".codex/config.toml", ConfigFormat.Toml, SupportsApprovalMode: true),
        new("Grok", ".grok/config.toml", ConfigFormat.Toml),
    ];

    /// <summary>
    /// The stdio entry every JSON config uses: launch the local adapter pinned to one connection.
    /// There is no URL, port, or token here, so the file stays valid across app restarts.
    /// </summary>
    public static JsonObject BuildJsonEntry(string adapterPath, string connectionPath) =>
        new()
        {
            ["type"] = "stdio",
            ["command"] = adapterPath,
            ["args"] = new JsonArray("--connection", connectionPath),
        };

    /// <summary>The same entry as a TOML table body, without any surrounding comments or markers.</summary>
    public static string BuildTomlEntry(
        Target target,
        string serverName,
        string adapterPath,
        string connectionPath,
        bool mcpToolsAutoApprove)
    {
        var sb = new StringBuilder();
        sb.Append("[mcp_servers.").Append(serverName).Append("]\n");
        sb.Append("command = \"").Append(EscapeToml(adapterPath)).Append("\"\n");
        sb.Append("args = [\"--connection\", \"").Append(EscapeToml(connectionPath)).Append("\"]\n");
        if (target.SupportsApprovalMode)
        {
            sb.Append("default_tools_approval_mode = \"")
              .Append(mcpToolsAutoApprove ? "approve" : "prompt")
              .Append("\"\n");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Rows of the "which agent reads which file" table in the generated docs. Returned as lines
    /// so each caller keeps its own line endings.
    /// </summary>
    public static IEnumerable<string> DocTableLines()
    {
        yield return "| Agent | Config file |";
        yield return "|-------|-------------|";
        foreach (var target in All)
            yield return $"| {target.Label} | `{target.RelativePath}` |";
    }

    internal static string EscapeToml(string value) =>
        value.Replace("\\", "\\\\", System.StringComparison.Ordinal)
             .Replace("\"", "\\\"", System.StringComparison.Ordinal);
}
