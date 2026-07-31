using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    public enum JsonEntryStyle
    {
        /// <summary><c>{ type: "stdio", command, args }</c>.</summary>
        Stdio,

        /// <summary>OpenCode's <c>{ type: "local", command: [exe, ...args] }</c>.</summary>
        OpenCodeLocal,
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
        bool SupportsApprovalMode = false,
        JsonEntryStyle JsonStyle = JsonEntryStyle.Stdio,
        bool IncludeAllTools = false)
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
        new("Claude Code / Desktop, Copilot CLI / Desktop, Pi extension", ".mcp.json",
            ConfigFormat.Json, "mcpServers", IncludeAllTools: true),
        new("OpenCode", "opencode.json", ConfigFormat.Json, "mcp",
            JsonStyle: JsonEntryStyle.OpenCodeLocal),
        new("OMP", ".omp/mcp.json", ConfigFormat.Json, "mcpServers"),
        new("VS Code (Copilot)", ".vscode/mcp.json", ConfigFormat.Json, "servers"),
        new("Cursor", ".cursor/mcp.json", ConfigFormat.Json, "mcpServers"),
        new("Antigravity (CLI / 2.0 / IDE)", ".agents/mcp_config.json", ConfigFormat.Json, "mcpServers"),
        new("Codex", ".codex/config.toml", ConfigFormat.Toml, SupportsApprovalMode: true),
        new("Grok", ".grok/config.toml", ConfigFormat.Toml),
    ];

    /// <summary>
    /// Context files that only redirect an agent to <c>AGENTS.md</c>, so the operating rules for a
    /// connection are written once. Only Claude needs one — every other agent here reads
    /// <c>AGENTS.md</c> by that name already.
    /// </summary>
    public static IReadOnlyList<string> ContextIncludeFiles { get; } = ["CLAUDE.md"];

    /// <summary>The import line those files hold.</summary>
    public const string ContextIncludeBody = "@AGENTS.md";

    /// <summary>
    /// The stdio entry every JSON config uses: launch the local adapter, optionally pinned to one
    /// connection. Omitting <paramref name="connectionPath"/> exposes the application-wide product
    /// MCP surface. There is no URL, port, or token here, so the file stays valid across restarts.
    /// </summary>
    public static JsonObject BuildJsonEntry(
        string adapterPath,
        string? connectionPath,
        string? instanceId = null)
    {
        var entry = new JsonObject
        {
            ["type"] = "stdio",
            ["command"] = adapterPath,
        };
        var args = BuildAdapterArguments(connectionPath, instanceId);
        if (args.Count > 0)
            entry["args"] = args;
        return entry;
    }

    /// <summary>Builds the spelling required by one JSON target.</summary>
    public static JsonObject BuildJsonEntry(
        Target target,
        string adapterPath,
        string? connectionPath,
        string? instanceId = null)
    {
        if (target.JsonStyle == JsonEntryStyle.Stdio)
        {
            var entry = BuildJsonEntry(adapterPath, connectionPath, instanceId);
            // Copilot CLI requires a tool filter. Claude accepts the same field, so the shared
            // .mcp.json can explicitly enable this server without widening other configs.
            if (target.IncludeAllTools)
                entry["tools"] = new JsonArray("*");
            return entry;
        }

        var command = new JsonArray { adapterPath };
        foreach (var argument in BuildAdapterArguments(connectionPath, instanceId))
            command.Add(argument?.DeepClone());
        return new JsonObject
        {
            ["type"] = "local",
            ["command"] = command,
            ["enabled"] = true,
        };
    }

    /// <summary>
    /// Adds target-specific root settings without widening approval to the agent's local tools.
    /// OpenCode prefixes MCP tools with the server name, so only that prefix is allowed.
    /// </summary>
    public static void ApplyJsonRootSettings(
        Target target,
        JsonObject root,
        string serverName,
        bool mcpToolsAutoApprove)
    {
        if (target.JsonStyle != JsonEntryStyle.OpenCodeLocal)
            return;

        if (root["permission"] is not JsonObject permission)
        {
            permission = new JsonObject();
            root["permission"] = permission;
        }
        permission[$"{serverName}_*"] = mcpToolsAutoApprove ? "allow" : "ask";
    }

    /// <summary>Removes only the root setting written for one server.</summary>
    public static void RemoveJsonRootSettings(
        Target target,
        JsonObject root,
        string serverName)
    {
        if (target.JsonStyle != JsonEntryStyle.OpenCodeLocal
            || root["permission"] is not JsonObject permission)
        {
            return;
        }

        permission.Remove($"{serverName}_*");
        if (permission.Count == 0)
            root.Remove("permission");
    }

    /// <summary>The same entry as a TOML table body, without any surrounding comments or markers.</summary>
    public static string BuildTomlEntry(
        Target target,
        string serverName,
        string adapterPath,
        string? connectionPath,
        bool mcpToolsAutoApprove,
        string? instanceId = null)
    {
        var sb = new StringBuilder();
        sb.Append("[mcp_servers.").Append(serverName).Append("]\n");
        sb.Append("command = \"").Append(EscapeToml(adapterPath)).Append("\"\n");
        var args = BuildAdapterArguments(connectionPath, instanceId)
            .Select(node => node?.GetValue<string>() ?? "")
            .ToArray();
        if (args.Length > 0)
        {
            sb.Append("args = [");
            for (var i = 0; i < args.Length; i++)
            {
                if (i > 0)
                    sb.Append(", ");
                sb.Append('"').Append(EscapeToml(args[i])).Append('"');
            }
            sb.Append("]\n");
        }
        if (target.SupportsApprovalMode)
        {
            sb.Append("default_tools_approval_mode = \"")
              .Append(mcpToolsAutoApprove ? "approve" : "prompt")
              .Append("\"\n");
        }

        return sb.ToString();
    }

    private static JsonArray BuildAdapterArguments(string? connectionPath, string? instanceId)
    {
        var args = new JsonArray();
        if (!string.IsNullOrWhiteSpace(instanceId))
        {
            args.Add("--instance");
            args.Add(instanceId);
        }
        if (!string.IsNullOrWhiteSpace(connectionPath))
        {
            args.Add("--connection");
            args.Add(connectionPath);
        }
        return args;
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
