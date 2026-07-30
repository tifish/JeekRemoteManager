using System.Linq;
using System.Text.Json.Nodes;

namespace JeekRemoteManager.Services;

public sealed class DebugMcpDiscovery
{
    public string Url { get; set; } = "";

    /// <summary>Named pipe accepting Debug MCP sessions (preferred over <see cref="Url"/>).</summary>
    public string PipeName { get; set; } = "";
    public int ProcessId { get; set; }
    public string ExecutablePath { get; set; } = "";
    public string InstanceId { get; set; } = "";
    public string InstanceLabel { get; set; } = "";
    public string WorkspaceRoot { get; set; } = "";
    public string ConfigRoot { get; set; } = "";
    public string RuntimeTempRoot { get; set; } = "";
}

public static class DebugMcpContract
{
    public const string SupportedProtocolVersion = "2025-06-18";
    public static readonly string[] KnownProtocolVersions = ["2024-11-05", "2025-03-26", SupportedProtocolVersion];

    public const string PathHelp =
        "Paths start from a root: App (the Application), Desktop (the desktop lifetime), " +
        "MainWindow, or MainVm (MainWindow.DataContext). Segments: '.Member' reads a property or field " +
        "(non-public included), '[0]' indexes a list, '[\"key\"]' indexes a dictionary, and " +
        "'#Name' finds a named control in the visual tree below the current object. " +
        "Examples: MainVm.Nodes[0].Name, MainWindow.#Tree.SelectedItem";

    public static JsonArray BuildToolList() => new(
        Tool("describe", "Overview of the running app: instance, windows, roots, path syntax, and log file. Start here.", new()),
        Tool("get_value", "Read a value from the app's object graph. " + PathHelp,
            new() { ["path"] = Prop("string", "Object path to read."), ["depth"] = Prop("integer", "Nested expansion depth, 0-5 (default 1).") }, ["path"]),
        Tool("set_value", "Write a property, field, or list element on the UI thread. " + PathHelp,
            new() { ["path"] = Prop("string", "Object path to write."), ["value"] = new JsonObject { ["description"] = "New JSON value; {$path: ...} passes a live object." } }, ["path", "value"]),
        Tool("invoke", "Execute an ICommand or call a method on the UI thread. " + PathHelp,
            new() { ["path"] = Prop("string", "Object path ending with a command or method."), ["args"] = new JsonObject { ["type"] = "array", ["description"] = "JSON arguments." }, ["depth"] = Prop("integer", "Return expansion depth, 0-5 (default 1).") }, ["path"]),
        Tool("list_members", "List properties, fields, and methods at a path. " + PathHelp,
            new() { ["path"] = Prop("string", "Object path to inspect.") }, ["path"]),
        Tool("visual_tree", "Dump the visual tree below a visual.",
            new() { ["path"] = Prop("string", "Starting Visual path (default MainWindow)."), ["max_depth"] = Prop("integer", "Maximum depth (default 12).") }),
        Tool("screenshot", "Render the main window to PNG.", new()),
        Tool("about_dialog_probe",
            "Open the real About dialog, verify its localized title, version text, and project homepage, then close it.",
            new()),
        Tool("read_logs", "Read the current app log tail.",
            new() { ["lines"] = Prop("integer", "Lines, 1-2000 (default 200)."), ["filter"] = Prop("string", "Case-insensitive filter.") }),
        Tool("ai_runtime_snapshot",
            "Snapshot each terminal tab's AI panel: provider, run mode (Cli/WindowsTerminal/Desktop), running/install state, SSH terminal visibility, MCP URL, and command execution counts.",
            new()),
        Tool("terminal_tab_focus_check",
            "Temporarily creates two terminal tabs and verifies that each restores its own in-memory focused control after switching.",
            new()),
        Tool("ai_cli_ctrl_c_check",
            "Temporarily creates a terminal tab and verifies AI CLI Ctrl+C: copies when text is selected and never sends 0x03 to the CLI.",
            new()),
        Tool("agent_cli_locate_check",
            "Report the resolved executable path (or install hint) for every agent CLI the AI panel offers; optionally resolve one path through the locator's link resolution.",
            new() { ["path"] = Prop("string", "Optional file path to run through ResolveRealPath.") }),
        Tool("agent_cli_mcp_config_check",
            "Refresh and verify one generated AI workspace has accurate AGENTS.md connection context, the installed JeekRemoteManagerMcp adapter, Claude approval, and every project MCP config in the catalog (.mcp.json, .vscode/mcp.json, .codex, .grok) pinned to the requested connection under the root key each agent reads.",
            new() { ["connection"] = Prop("string", "Connection tree path under AgentWorkspaces (default vps/bwg).") }),
        Tool("agent_endpoint_check",
            "Verify custom API endpoints end to end without revealing the key: reports each agent's stored endpoint (hasApiKey only), the environment variables its launch would set (values masked), and the Codex model_provider block written into a workspace config. Optionally sets a throwaway endpoint first and restores the previous one afterwards.",
            new()
            {
                ["agent"] = Prop("string", "Claude | Codex (default: report both)."),
                ["set_base_url"] = Prop("string", "Temporarily apply this base URL to run the check."),
                ["set_api_key"] = Prop("string", "Temporary key used with set_base_url; never echoed back."),
                ["set_model"] = Prop("string", "Optional temporary model id."),
                ["probe_env"] = Prop("boolean", "Also run cmd /c set under a pseudo console to prove the environment block reaches the child process (default false)."),
                ["action"] = Prop("string", "add | select | delete — edits the saved endpoint list the picker shows (requires 'agent'). Omit to only report."),
                ["name"] = Prop("string", "Display name for action=add."),
                ["profile_id"] = Prop("string", "Endpoint id for action=select (blank selects the official API) or action=delete."),
            }),
        Tool("login_menu_select_check",
            "Run the login-command \"#select <name>\" matcher against menu text: reports the parsed menu entries and which number the name would type.",
            new()
            {
                ["menu"] = Prop("string", "Menu text as the remote printed it (ANSI sequences allowed)."),
                ["name"] = Prop("string", "Machine name or IP to match, as written after #select."),
            }, ["menu", "name"]),
        Tool("login_menu_select_probe",
            "End-to-end check of the \"#select <name>\" login directive: 'open' adds a terminal tab on a local cmd.exe shell that prints a numbered menu and selects an entry by name, 'status' returns the scrollback, 'close' removes the tab.",
            new()
            {
                ["action"] = Prop("string", "open | status | close (default status)."),
                ["scenario"] = Prop("string", "single (one-screen menu, default) | paged (menu that needs Ctrl-F to reach the wanted entry)."),
                ["login_commands"] = Prop("string", "Optional login-command text overriding the scenario's script."),
            }),
        Tool("ai_render_probe",
            "Persistent AI-panel rendering probe: action 'open' adds a local terminal tab with the embedded agent CLI started, 'status' reports feed/scroll state plus visible viewport text, 'close' removes the tab.",
            new() { ["action"] = Prop("string", "open | status | close (default status).") }),
        Tool("product_mcp_check",
            "Drives the product MCP surface over its own pipe the way a user's agent would: create a throwaway connection, verify passwords are write-only (hasPassword only, never the value or blob), set one, check in-session tools refuse clearly with no session, then delete the connection.",
            new()),
        Tool("mcp_transport_check",
            "Connects to the app's own MCP named pipe as a client and runs initialize + tools/list plus a second concurrent session, verifying the pipe transport, its ACL, and the line framing.",
            new()),
        Tool("agent_project_link_check",
            "Links a throwaway project folder to a synthetic agent workspace and verifies the AGENTS.md/CLAUDE.md reference block plus every merged MCP config in the catalog (.mcp.json, .vscode/mcp.json, .codex, .grok), then refresh (no duplicates) and unlink (project content restored, our own files and folders removed).",
            new()
            {
                ["panel"] = Prop("boolean", "Also drive the live AI panel view model from the open ai_render_probe tab (default false)."),
                ["keep"] = Prop("boolean", "Keep the temporary project folder instead of deleting it (default false)."),
            }),
        Tool("auto_update_stage_check",
            "Runs the in-app update downloader end-to-end (real network): downloads the release package, extracts and verifies it in the staging folder, then cleans up.",
            new()
            {
                ["url"] = Prop("string", "Optional package URL override (default: latest release via all mirrors)."),
                ["keep"] = Prop("boolean", "Keep the staged folder instead of cleaning it up (default false)."),
            }));

    public static JsonObject InitializeResult(string name, string title, string version, string? requestedVersion)
    {
        var protocol = KnownProtocolVersions.Contains(requestedVersion) ? requestedVersion! : SupportedProtocolVersion;
        return new JsonObject
        {
            ["protocolVersion"] = protocol,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
            ["serverInfo"] = new JsonObject { ["name"] = name, ["title"] = title, ["version"] = version },
        };
    }

    private static JsonObject Tool(string name, string description, JsonObject properties, string[]? required = null)
    {
        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (required is { Length: > 0 })
            schema["required"] = new JsonArray(required.Select(JsonNode (r) => r).ToArray());
        return new JsonObject { ["name"] = name, ["description"] = description, ["inputSchema"] = schema };
    }

    private static JsonObject Prop(string type, string description) =>
        new() { ["type"] = type, ["description"] = description };
}
