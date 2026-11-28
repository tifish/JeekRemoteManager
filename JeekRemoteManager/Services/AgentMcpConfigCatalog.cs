using System;
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
    /// Root key holding the server map, for <see cref="ConfigFormat.Json"/> only. Antigravity,
    /// Claude, Copilot CLI and Cursor read <c>mcpServers</c>; OpenCode reads <c>mcp</c>.
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
    /// Paths and environment variables used to locate user-level global MCP configs.
    /// Tests pass <see cref="Isolated"/> so probes never touch the user's real configs.
    /// </summary>
    public sealed record GlobalConfigEnvironment(
        string UserProfile,
        string AppData,
        string LocalAppData,
        Func<string, string?> GetEnvironmentVariable)
    {
        public static GlobalConfigEnvironment Current { get; } = new(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetEnvironmentVariable);

        /// <summary>Isolated environment rooted under one temporary folder for tests.</summary>
        public static GlobalConfigEnvironment Isolated(
            string rootDirectory,
            IReadOnlyDictionary<string, string?>? environmentVariables = null)
        {
            var full = Path.GetFullPath(rootDirectory);
            return new(
                UserProfile: full,
                AppData: Path.Combine(full, "AppData", "Roaming"),
                LocalAppData: Path.Combine(full, "AppData", "Local"),
                GetEnvironmentVariable: name =>
                    environmentVariables is not null
                    && environmentVariables.TryGetValue(name, out var value)
                        ? value
                        : null);
        }
    }

    /// <summary>
    /// One user-level global MCP config written so an agent can reach JeekRemoteManager from any
    /// session without linking individual project folders.
    /// </summary>
    public sealed record GlobalTarget(
        string Id,
        string Label,
        ConfigFormat Format,
        Func<GlobalConfigEnvironment, string> ResolvePathFunc,
        string? JsonRootKey = null,
        bool SupportsApprovalMode = false,
        JsonEntryStyle JsonStyle = JsonEntryStyle.Stdio,
        bool IncludeAllTools = false)
    {
        /// <summary>Absolute path of this global config in <paramref name="environment"/>.</summary>
        public string ResolvePath(GlobalConfigEnvironment? environment = null) =>
            Path.GetFullPath(ResolvePathFunc(environment ?? GlobalConfigEnvironment.Current));

        /// <summary>
        /// Projects this global target onto <see cref="Target"/> so JSON/TOML entry builders and
        /// root-setting helpers stay shared with project configs.
        /// </summary>
        internal Target AsTarget() => new(
            Label,
            Id,
            Format,
            JsonRootKey,
            SupportsApprovalMode,
            JsonStyle,
            IncludeAllTools);
    }

    /// <summary>
    /// Every config written for a workspace or a linked project, sorted alphabetically by label.
    /// Claude, Copilot CLI/Desktop, and the Pi extension share <c>.mcp.json</c>.
    /// </summary>
    public static IReadOnlyList<Target> All { get; } =
    [
        new("Antigravity (CLI / 2.0 / IDE)", ".agents/mcp_config.json", ConfigFormat.Json, "mcpServers"),
        new("Claude Code / Desktop, Copilot CLI / Desktop, Pi extension", ".mcp.json",
            ConfigFormat.Json, "mcpServers", IncludeAllTools: true),
        new("Codex", ".codex/config.toml", ConfigFormat.Toml, SupportsApprovalMode: true),
        new("Cursor (IDE / CLI)", ".cursor/mcp.json", ConfigFormat.Json, "mcpServers"),
        new("Grok", ".grok/config.toml", ConfigFormat.Toml),
        new("OpenCode", "opencode.json", ConfigFormat.Json, "mcp",
            JsonStyle: JsonEntryStyle.OpenCodeLocal),
    ];

    /// <summary>
    /// Every user-level global MCP config supported for application-wide writing, sorted
    /// alphabetically by label. T3 Code inherits the global configs of the agents it hosts.
    /// </summary>
    public static IReadOnlyList<GlobalTarget> GlobalAll { get; } =
    [
        new(
            "antigravity",
            "Antigravity (CLI / 2.0 / IDE)",
            ConfigFormat.Json,
            env => Path.Combine(env.UserProfile, ".gemini", "config", "mcp_config.json"),
            JsonRootKey: "mcpServers"),
        new(
            "claude-code",
            "Claude Code",
            ConfigFormat.Json,
            env => Path.Combine(env.UserProfile, ".claude.json"),
            JsonRootKey: "mcpServers"),
        new(
            "claude-desktop",
            "Claude Desktop",
            ConfigFormat.Json,
            ResolveClaudeDesktopConfigPath,
            JsonRootKey: "mcpServers"),
        new(
            "codex",
            "Codex",
            ConfigFormat.Toml,
            env => Path.Combine(
                ResolveEnvHome(env, "CODEX_HOME", Path.Combine(env.UserProfile, ".codex")),
                "config.toml"),
            SupportsApprovalMode: true),
        new(
            "cursor",
            "Cursor (IDE / CLI)",
            ConfigFormat.Json,
            env => Path.Combine(env.UserProfile, ".cursor", "mcp.json"),
            JsonRootKey: "mcpServers"),
        new(
            "copilot",
            "GitHub Copilot (CLI / Desktop)",
            ConfigFormat.Json,
            env => Path.Combine(
                ResolveEnvHome(env, "COPILOT_HOME", Path.Combine(env.UserProfile, ".copilot")),
                "mcp-config.json"),
            JsonRootKey: "mcpServers",
            IncludeAllTools: true),
        new(
            "grok",
            "Grok",
            ConfigFormat.Toml,
            env => Path.Combine(
                ResolveEnvHome(env, "GROK_HOME", Path.Combine(env.UserProfile, ".grok")),
                "config.toml")),
        new(
            "opencode",
            "OpenCode",
            ConfigFormat.Json,
            env => Path.Combine(env.UserProfile, ".config", "opencode", "opencode.json"),
            JsonRootKey: "mcp",
            JsonStyle: JsonEntryStyle.OpenCodeLocal),
        new(
            "pi",
            "Pi",
            ConfigFormat.Json,
            env => Path.Combine(
                ResolveEnvHome(env, "PI_CODING_AGENT_DIR", Path.Combine(env.UserProfile, ".pi", "agent")),
                "mcp.json"),
            JsonRootKey: "mcpServers"),
    ];

    private static string ResolveEnvHome(
        GlobalConfigEnvironment environment,
        string variableName,
        string fallback)
    {
        var configured = environment.GetEnvironmentVariable(variableName);
        return string.IsNullOrWhiteSpace(configured) ? fallback : configured.Trim();
    }

    /// <summary>
    /// Claude Desktop packaged as MSIX (Microsoft Store) reads from its package LocalCache
    /// rather than <c>%APPDATA%\Claude</c>. Prefer that folder when an installed Claude package
    /// has created it; otherwise use the standard Roaming path.
    /// </summary>
    private static string ResolveClaudeDesktopConfigPath(GlobalConfigEnvironment environment)
    {
        var packagesRoot = Path.Combine(environment.LocalAppData, "Packages");
        try
        {
            if (Directory.Exists(packagesRoot))
            {
                foreach (var packageDir in Directory.EnumerateDirectories(packagesRoot, "Claude_*")
                             .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    var roamingClaude = Path.Combine(packageDir, "LocalCache", "Roaming", "Claude");
                    if (Directory.Exists(roamingClaude))
                        return Path.Combine(roamingClaude, "claude_desktop_config.json");
                }
            }
        }
        catch
        {
            // Fall through to the standard Roaming path.
        }

        return Path.Combine(environment.AppData, "Claude", "claude_desktop_config.json");
    }

    /// <summary>
    /// Context files that only redirect an agent to <c>AGENTS.md</c>, so the operating rules for a
    /// connection are written once. Only Claude needs one — every other agent here reads
    /// <c>AGENTS.md</c> by that name already.
    /// </summary>
    public static IReadOnlyList<string> ContextIncludeFiles { get; } = ["CLAUDE.md"];

    /// <summary>The import line those files hold.</summary>
    public const string ContextIncludeBody = "@AGENTS.md";

    /// <summary>
    /// Launcher written into a linked project. Agents run this script instead of an expanded
    /// <c>C:\Users\…</c> path so the files can be committed and work on every machine.
    /// </summary>
    public const string ProjectLauncherFileName = "JeekRemoteManagerMcp.cmd";

    /// <summary>Relative command agents pass to <c>cmd /c</c> from the project root.</summary>
    public const string ProjectLauncherRelativeCommand = @".\" + ProjectLauncherFileName;

    /// <summary>Comment that marks a launcher we wrote and may delete on unlink.</summary>
    public const string ProjectLauncherMarker = "Portable JeekRemoteManager product MCP adapter";

    /// <summary>
    /// How one MCP config launches the adapter. Generated workspaces use the absolute
    /// per-user exe; linked projects use <c>cmd /c .\JeekRemoteManagerMcp.cmd</c> so nothing
    /// in the committed files contains a username.
    /// </summary>
    public sealed record AdapterLaunch(
        string Command,
        IReadOnlyList<string> Arguments,
        string? WorkingDirectory = null)
    {
        /// <summary>Absolute exe plus optional <c>--instance</c> / <c>--connection</c> / <c>--session</c>.</summary>
        public static AdapterLaunch Direct(
            string adapterPath,
            string? connectionPath,
            string? instanceId = null,
            string? sessionId = null) =>
            new(adapterPath, ToArgumentList(connectionPath, instanceId, sessionId));

        /// <summary>
        /// Portable project launch: <c>cmd /c .\JeekRemoteManagerMcp.cmd</c> plus optional
        /// <c>--instance</c> / <c>--connection</c> / <c>--session</c>. Release omits <c>--instance</c> so the
        /// committed files talk to the installed app; Debug includes it so agents opened on
        /// the folder reach the worktree that wrote the config, not a different instance.
        /// </summary>
        public static AdapterLaunch PortableProject(
            string? connectionPath,
            string? instanceId = null,
            string? sessionId = null)
        {
            var args = new List<string> { "/c", ProjectLauncherRelativeCommand };
            args.AddRange(ToArgumentList(connectionPath, instanceId, sessionId));
            return new("cmd", args, ".");
        }

        public bool HasArguments => Arguments.Count > 0;
    }

    /// <summary>
    /// True when <paramref name="projectDirectory"/> is a JeekRemoteManager worktree: the
    /// debug launcher at the root is unique to this repository. Those folders already have
    /// Debug MCP and must not receive product MCP configs.
    /// </summary>
    public static bool ProjectLooksLikeJeekRemoteManagerWorktree(string projectDirectory) =>
        File.Exists(Path.Combine(projectDirectory, "JeekRemoteManagerDebugMcp.cmd"));

    /// <summary>
    /// Batch script that expands <c>%LocalAppData%</c> at launch so a linked project can
    /// commit the file and run it on every computer.
    /// </summary>
    public static string BuildProjectLauncherScript()
    {
        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.Append("rem ").Append(ProjectLauncherMarker).AppendLine(".");
        sb.AppendLine("rem Uses %LocalAppData% so this file can be committed and works on every computer.");
        sb.AppendLine(@"set ""ADAPTER=%LocalAppData%\JeekRemoteManager\Mcp\JeekRemoteManagerMcp.exe""");
        sb.AppendLine();
        sb.AppendLine(@"if not exist ""%ADAPTER%"" (");
        sb.AppendLine("  echo The fixed JeekRemoteManager MCP adapter is not installed at: 1>&2");
        sb.AppendLine("  echo   %ADAPTER% 1>&2");
        sb.AppendLine("  echo Launch JeekRemoteManager once, then retry. 1>&2");
        sb.AppendLine("  exit /b 1");
        sb.AppendLine(")");
        sb.AppendLine();
        sb.AppendLine(@"""%ADAPTER%"" %*");
        return sb.ToString();
    }

    /// <summary>True when <paramref name="text"/> is a launcher this catalog generated.</summary>
    public static bool IsGeneratedProjectLauncher(string text) =>
        text.Contains(ProjectLauncherMarker, StringComparison.Ordinal);

    /// <summary>
    /// The stdio entry every JSON config uses: launch the local adapter, optionally pinned to one
    /// connection. Omitting <paramref name="connectionPath"/> exposes the application-wide product
    /// MCP surface. There is no URL, port, or token here, so the file stays valid across restarts.
    /// </summary>
    public static JsonObject BuildJsonEntry(
        string adapterPath,
        string? connectionPath,
        string? instanceId = null,
        string? sessionId = null) =>
        BuildStdioEntry(AdapterLaunch.Direct(adapterPath, connectionPath, instanceId, sessionId));

    /// <summary>Builds the spelling required by one JSON target.</summary>
    public static JsonObject BuildJsonEntry(
        Target target,
        string adapterPath,
        string? connectionPath,
        string? instanceId = null,
        string? sessionId = null) =>
        BuildJsonEntry(target, AdapterLaunch.Direct(adapterPath, connectionPath, instanceId, sessionId));

    /// <summary>Builds the spelling required by one JSON target from a prepared launch.</summary>
    public static JsonObject BuildJsonEntry(Target target, AdapterLaunch launch)
    {
        if (target.JsonStyle == JsonEntryStyle.Stdio)
        {
            var entry = BuildStdioEntry(launch);
            // Copilot CLI requires a tool filter. Claude accepts the same field, so the shared
            // .mcp.json can explicitly enable this server without widening other configs.
            if (target.IncludeAllTools)
                entry["tools"] = new JsonArray("*");
            return entry;
        }

        var command = new JsonArray { launch.Command };
        foreach (var argument in launch.Arguments)
            command.Add(argument);
        var local = new JsonObject
        {
            ["type"] = "local",
            ["command"] = command,
            ["enabled"] = true,
        };
        ApplyWorkingDirectory(local, launch);
        return local;
    }

    /// <summary>
    /// Adds target-specific root settings without widening approval to the agent's local tools.
    /// OpenCode prefixes MCP tools with the server name, so only that prefix is allowed.
    /// </summary>
    public static void ApplyJsonRootSettings(
        Target target,
        JsonObject root,
        string serverName)
    {
        if (target.JsonStyle == JsonEntryStyle.OpenCodeLocal)
        {
            EnsureObject(root, "permission")[$"{serverName}_*"] = "allow";
        }
    }

    /// <summary>Removes only the root setting written for one server.</summary>
    public static void RemoveJsonRootSettings(
        Target target,
        JsonObject root,
        string serverName)
    {
        if (target.JsonStyle == JsonEntryStyle.OpenCodeLocal)
        {
            if (root["permission"] is not JsonObject permission)
                return;
            permission.Remove($"{serverName}_*");
            if (permission.Count == 0)
                root.Remove("permission");
        }
    }

    private static JsonObject EnsureObject(JsonObject parent, string key)
    {
        if (parent[key] is JsonObject existing)
            return existing;

        var created = new JsonObject();
        parent[key] = created;
        return created;
    }

    /// <summary>The same entry as a TOML table body, without any surrounding comments or markers.</summary>
    public static string BuildTomlEntry(
        Target target,
        string serverName,
        string adapterPath,
        string? connectionPath,
        string? instanceId = null,
        string? sessionId = null) =>
        BuildTomlEntry(
            target,
            serverName,
            AdapterLaunch.Direct(adapterPath, connectionPath, instanceId, sessionId));

    /// <summary>The same entry as a TOML table body from a prepared launch.</summary>
    public static string BuildTomlEntry(
        Target target,
        string serverName,
        AdapterLaunch launch)
    {
        var sb = new StringBuilder();
        sb.Append("[mcp_servers.").Append(serverName).Append("]\n");
        sb.Append("command = \"").Append(EscapeToml(launch.Command)).Append("\"\n");
        if (launch.HasArguments)
        {
            sb.Append("args = [");
            for (var i = 0; i < launch.Arguments.Count; i++)
            {
                if (i > 0)
                    sb.Append(", ");
                sb.Append('"').Append(EscapeToml(launch.Arguments[i])).Append('"');
            }
            sb.Append("]\n");
        }
        if (launch.WorkingDirectory is { Length: > 0 } cwd)
            sb.Append("cwd = \"").Append(EscapeToml(cwd)).Append("\"\n");
        if (target.SupportsApprovalMode)
        {
            sb.Append("default_tools_approval_mode = \"")
              .Append("approve")
              .Append("\"\n");
        }

        return sb.ToString();
    }

    private static JsonObject BuildStdioEntry(AdapterLaunch launch)
    {
        var entry = new JsonObject
        {
            ["type"] = "stdio",
            ["command"] = launch.Command,
        };
        ApplyArguments(entry, launch);
        ApplyWorkingDirectory(entry, launch);
        return entry;
    }

    private static void ApplyArguments(JsonObject entry, AdapterLaunch launch)
    {
        if (!launch.HasArguments)
            return;
        var args = new JsonArray();
        foreach (var argument in launch.Arguments)
            args.Add(argument);
        entry["args"] = args;
    }

    private static void ApplyWorkingDirectory(JsonObject entry, AdapterLaunch launch)
    {
        if (launch.WorkingDirectory is { Length: > 0 } cwd)
            entry["cwd"] = cwd;
    }

    private static IReadOnlyList<string> ToArgumentList(
        string? connectionPath,
        string? instanceId,
        string? sessionId = null)
    {
        var args = new List<string>();
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
        if (!string.IsNullOrWhiteSpace(sessionId)
            && !string.Equals(sessionId, connectionPath, StringComparison.OrdinalIgnoreCase))
        {
            args.Add("--session");
            args.Add(sessionId);
        }

        return args;
    }

    /// <summary>
    /// Rows of the "which agent reads which file" table in the generated docs. Returned as lines
    /// so each caller keeps its own line endings.
    /// </summary>
    public static IEnumerable<string> DocTableLines(IEnumerable<Target>? targets = null)
    {
        yield return "| Agent | Config file |";
        yield return "|-------|-------------|";
        foreach (var target in targets ?? All)
            yield return $"| {target.Label} | `{target.RelativePath}` |";
    }

    internal static string EscapeToml(string value)
    {
        var sb = new System.Text.StringBuilder(value.Length);
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\\': sb.Append(@"\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\b': sb.Append(@"\b"); break;
                case '\t': sb.Append(@"\t"); break;
                case '\n': sb.Append(@"\n"); break;
                case '\f': sb.Append(@"\f"); break;
                case '\r': sb.Append(@"\r"); break;
                default:
                    if (ch < 0x20 || ch == 0x7f)
                        sb.Append($"\\u{(int)ch:X4}");
                    else
                        sb.Append(ch);
                    break;
            }
        }

        return sb.ToString();
    }
}
