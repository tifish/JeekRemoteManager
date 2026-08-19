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
        Tool("terminal_tab_title_check",
            "Build and measure the real terminal-tab title controls, verifying long-name tail/tooltip behavior, emphasized adjacent differences, and four-digit numeric context around the actual difference.",
            new()),
        Tool("terminal_tab_focus_check",
            "Temporarily creates two terminal tabs and verifies that each restores its own in-memory focused control after switching.",
            new()),
        Tool("terminal_tab_lifecycle_check",
            "Creates and closes several real terminal tabs through the production close path and verifies the batch closed by the previous run has been released. Run it twice: the first run only primes the batch, because views closed inside a call stay reachable from that call's own frames.",
            new()),
        Tool("terminal_connection_actions_check",
            "Drives the connection tree's Connect, New session, and New TCP connection paths and verifies their tab and transport reuse semantics.",
            new()),
        Tool("terminal_output_coalescing_check",
            "Feeds a burst of packets through a real terminal tab and verifies they are rendered in one UI batch.",
            new()),
        Tool("connection_tree_load_check",
            "Reads a generated connection tree in an isolated temp root and verifies the read runs off the UI thread, returns the whole tree, and leaves the dispatcher responsive while a reload is in flight.",
            new()),
        Tool("connection_tree_reload_order_check",
            "Overlaps two background tree reloads so the earlier read finishes last, and verifies the stale snapshot is discarded instead of resurrecting nodes the newer read saw removed.",
            new()),
        Tool("connection_write_watcher_check",
            "Creates, edits and deletes a connection through the product MCP paths and verifies each write reloads the tree once, with the file watcher recognising it as the app's own change.",
            new()),
        Tool("sftp_retry_policy_check",
            "Drops the SFTP transport mid-operation and verifies listings and transfers are replayed on a fresh connection while deletes, renames and mkdirs are not.",
            new()),
        Tool("ssh_auth_prompt_check",
            "Runs the keyboard-interactive prompt filler against localized, multi-prompt, OTP, and echoed sshd challenges, verifies leftover OTP prompts are asked of the user rather than filled with the password, and verifies a configured but missing private key file is named in the failure.",
            new()),
        Tool("host_key_trust_check",
            "Verifies first-seen SSH host keys are saved without prompting, while changed remembered keys require replacement confirmation.",
            new()),
        Tool("zmodem_detector_latency_check",
            "Verifies the ZMODEM trigger detector releases ordinary terminal output immediately, including single echoed keystrokes, while still holding back and reassembling triggers split across packets.",
            new()),
        Tool("zmodem_subpacket_limit_check",
            "Drives a real ZMODEM receive session against a peer that never sends a frame terminator and verifies the reader fails the frame instead of accumulating without limit.",
            new()),
        Tool("terminal_output_backpressure_check",
            "Floods the terminal session output buffer past its cap without draining and verifies memory stays bounded, the newest output survives, and the dropped-byte tally is reported once.",
            new()),
        Tool("monitor_suspend_check",
            "Verifies server monitor sampling follows tab visibility: it keeps running through a grace period when the tab goes to the background, suspends once that elapses, and resumes immediately when the tab is shown again.",
            new()),
        Tool("terminal_font_sync_check",
            "Adjusts the shared SSH terminal font size by one step and verifies the SSH terminal, its embedded AI CLI panel, and the global AI CLI panel all update together, then restores the original size.",
            new()),
        Tool("ai_panel_lifecycle_check",
            "Opens and closes a real terminal AI panel without launching a CLI, verifies disposal, and verifies a new tab does not inherit the open state.",
            new()),
        Tool("file_browser_session_lifecycle_check",
            "Drives the file-browser visibility lifecycle with an in-process SFTP-shaped session, verifying active transfers block release and reopening reconnects.",
            new()),
        Tool("ai_cli_ctrl_c_check",
            "Temporarily creates a terminal tab and verifies AI CLI Ctrl+C: copies when text is selected and never sends 0x03 to the CLI.",
            new()),
        Tool("agent_cli_locate_check",
            "Report every AI panel surface as installed, available through an official web launcher, installable in a visible external console, or downloadable from a website; optionally resolve one path through the locator's link resolution.",
            new() { ["path"] = Prop("string", "Optional file path to run through ResolveRealPath.") }),
        Tool("agent_discovery_cache_check",
            "Verifies the agent-discovery cache serves repeated probes but expires, so an agent installed outside the app appears without restarting. Takes about six seconds.",
            new()),
        Tool("agent_cli_mcp_config_check",
            "Refresh and verify one generated AI workspace has accurate AGENTS.md connection context, the fixed JeekRemoteManagerMcp.exe, a valid registry route to this instance, Claude approval, the bundled Pi extension, and every project MCP config in the catalog pinned to the requested connection under the exact shape each agent reads.",
            new() { ["connection"] = Prop("string", "Connection tree path under AgentWorkspaces (default vps/bwg).") }),
        Tool("login_menu_select_check",
            "Run the login-command \"#select <name>\" matcher against menu text: reports the parsed menu entries and which number the name would type.",
            new()
            {
                ["menu"] = Prop("string", "Menu text as the remote printed it (ANSI sequences allowed)."),
                ["name"] = Prop("string", "Machine name or IP to match, as written after #select."),
            }, ["menu", "name"]),
        Tool("login_command_flow_check",
            "Parse a structured bastion login workflow and report exactly what fresh, duplicate/monitor, #reuse-enter, and #reuse-leave flows execute, plus validation and #key encoding.",
            new()
            {
                ["login_commands"] = Prop("string", "Login-command text; defaults to a numeric-menu bastion example."),
                ["key"] = Prop("string", "Optional key name to encode through the same #key parser (default Enter)."),
            }),
        Tool("login_command_completion_check",
            "Open a temporary real login-command editor and verify # marker filtering, popup state, and accepted replacement text.",
            new()),
        Tool("login_command_variable_check",
            "Resolve login-command variables from a safe current-connection whitelist and verify template ordering, escaping, empty values, unknown variables, and source diagnostics.",
            new()),
        Tool("bastion_login_template_check",
            "Create two temporary same-bastion connections and verify default template association, four fixed fragments, expansion, persistence, and surrounding-blank-line trimming.",
            new()),
        Tool("bastion_template_preset_check",
            "Open the real bastion-template dialog, insert the typical preset, save it into an isolated editor, and verify empty connection commands are filled, existing commands are preserved, and sudo -i runs on fresh, reuse-enter, and duplicate flows.",
            new()),
        Tool("conpty_teardown_race_check",
            "Start real ConPTY sessions and hammer Write/Resize from worker threads while disposing them, verifying no ObjectDisposedException escapes and the pseudo console is never touched after it is closed.",
            new()),
        Tool("bastion_channel_limit_check",
            "Verify shell-channel opens and the bastion transition queue are bounded, late channels are disposed, and terminal/monitor paths expose visible fallback behavior.",
            new()),
        Tool("bastion_reuse_landing_check",
            "Classify bastion channel landings and verify a target switch runs the old connection's #reuse-leave, then the new connection's #reuse-enter.",
            new()),
        Tool("connection_editor_switch_check",
            "Switch among real SSH connections on the UI thread, restore the prior selection, verify each editor is constructed, and report timing.",
            new()),
        Tool("login_menu_select_probe",
            "End-to-end check of the \"#select <name>\" login directive: 'open' adds a terminal tab on a local cmd.exe shell that prints a numbered menu and selects an entry by name, 'status' returns the scrollback, 'close' removes the tab.",
            new()
            {
                ["action"] = Prop("string", "open | status | close (default status)."),
                ["scenario"] = Prop("string", "single (one-screen menu, default) | paged (menu that needs Ctrl-F) | switch (delayed menu after #reuse-leave and #key Enter)."),
                ["login_commands"] = Prop("string", "Optional login-command text overriding the scenario's script."),
            }),
        Tool("ai_render_probe",
            "Persistent AI-panel rendering probe: action 'open' adds a local terminal tab with the embedded agent CLI started, 'status' reports feed/scroll state plus visible viewport text, 'hide' closes and disposes the AI runtime while keeping the tab, and 'close' removes the tab.",
            new() { ["action"] = Prop("string", "open | status | hide | close (default status).") }),
        Tool("product_mcp_check",
            "Drives the product MCP surface over its own pipe the way a user's agent would: create a throwaway connection, verify passwords are write-only, exercise session open/list/move/close and in-session addressing, then delete the connection.",
            new()),
        Tool("mcp_transport_check",
            "Connects to the app's own MCP named pipe as a client and runs initialize + tools/list plus a second concurrent session, verifying the pipe transport, its ACL, and the line framing.",
            new()),
        Tool("agent_project_link_check",
            "Links a throwaway project folder to a synthetic agent workspace and verifies the AGENTS.md/CLAUDE.md reference block plus every merged MCP config in the catalog uses the portable cmd launcher (no username path), then a selective rewrite (unused files/folders removed), refresh (no duplicates) and unlink (project content restored). Also checks the AI options menu exposes the connection MCP write action.",
            new()
            {
                ["panel"] = Prop("boolean", "Also drive the live AI panel view model from the open ai_render_probe tab (default false)."),
                ["keep"] = Prop("boolean", "Keep the temporary project folder instead of deleting it (default false)."),
            }),
        Tool("agent_application_link_check",
            "Drives the MCP write dialog against a throwaway project by clicking its buttons. Verifies disabled actions on an empty/missing folder, Select all/none, typing a folder to auto-check written agents, Write of a subset, worktree rejection, Remove all, Cancel without saving, last-directory prefill, the connection-flavor dialog, the portable launcher, and preservation of existing project config.",
            new() { ["keep"] = Prop("boolean", "Keep the temporary project folder instead of deleting it (default false).") }),
        Tool("global_agent_check",
            "Verifies the in-app global AI Agent starts closed, can be opened, fully closed, and reopened without launching a third-party CLI, then checks its application-wide workspace, unpinned MCP config, connection-only options, and multi-connection product tools.",
            new()),
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
