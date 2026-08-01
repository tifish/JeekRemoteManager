using System.Linq;
using System.Text.Json.Nodes;

namespace JeekRemoteManager.Services;

/// <summary>
/// Tool surface of the product MCP server — JeekRemoteManager as a second front-end for an
/// agent. Scoped to connections first: browse the tree, open and address terminal sessions,
/// drive the live shell, and create a connection. Settings and connection editing come later.
///
/// Two rules run through the whole surface:
/// <list type="bullet">
/// <item>Passwords are write-only. No tool returns a password, a passphrase, or the encrypted
/// <c>jrm1</c> blob — only <c>hasPassword</c> style booleans (see
/// <see cref="ProductMcpServer"/>, which builds responses from an explicit field whitelist).</item>
/// <item>Anything needing the user (master password, two-factor prompts, destructive
/// confirmation) happens in the GUI. Tools surface and activate the window; they never take
/// a secret as an argument.</item>
/// </list>
/// </summary>
public static class ProductMcpContract
{
    public const string SessionHelp =
        "Sessions are addressed by the connection's tree path, with a suffix for extra tabs on " +
        "the same connection: 'vps/bwg', 'vps/bwg (2)'. Call session_list to see what is open " +
        "and session_open to start one.";

    public static JsonArray BuildToolList() => new(
        // --- Directory and metadata (read-only) ---
        Tool("connection_list",
            "List saved connections. Returns tree path, name, type (SSH/WSL/RDP), and target for each. Never returns credentials.",
            new()
            {
                ["filter"] = Prop("string", "Case-insensitive substring matched against name, path, host, and user."),
                ["folder"] = Prop("string", "Limit to this folder of the tree, e.g. 'vps'."),
                ["limit"] = Prop("integer", "Maximum entries to return (default 200)."),
            }),
        Tool("connection_get",
            "Full configuration of one connection: type, target, login commands, panel options, notes. "
            + "Passwords and key passphrases are reported only as hasPassword / hasKeyPassphrase booleans.",
            new() { ["connection"] = Prop("string", "Connection tree path, e.g. 'vps/bwg'.") },
            ["connection"]),
        Tool("connection_create",
            "Create a saved connection. Set its password afterwards with connection_set_password — "
            + "this tool takes no secrets.",
            CreateFields(),
            ["name"]),

        Tool("connection_update",
            "Change fields of a saved connection. Only the fields you pass are touched; a new "
            + "'name' renames it. Credentials are not settable here — use connection_set_password.",
            EditableFields(new()
            {
                ["connection"] = Prop("string", "Connection tree path to update."),
            }),
            ["connection"]),
        Tool("connection_move",
            "Move a connection to another folder of the tree. The folder is created if missing.",
            new()
            {
                ["connection"] = Prop("string", "Connection tree path to move."),
                ["folder"] = Prop("string", "Destination folder, e.g. 'vps/asia'; empty = root."),
            },
            ["connection", "folder"]),
        Tool("connection_delete",
            "Delete a saved connection. Always asks the user to confirm in the JeekRemoteManager "
            + "window first; returns an error if they decline. Open terminal tabs are left alone.",
            new() { ["connection"] = Prop("string", "Connection tree path to delete.") },
            ["connection"]),

        // --- Credentials (write-only) ---
        Tool("connection_set_password",
            "Set a connection's password or key passphrase. Default mode 'prompt' opens the input in the "
            + "JeekRemoteManager window so the secret never passes through this channel; mode 'value' "
            + "writes a value you supply and should only be used when the user asked for that explicitly. "
            + "There is no matching read tool.",
            new()
            {
                ["connection"] = Prop("string", "Connection tree path."),
                ["target"] = Prop("string", "password (default) or key_passphrase."),
                ["mode"] = Prop("string", "prompt (default; user types it in the GUI) or value."),
                ["value"] = Prop("string", "Secret to store; only read when mode is 'value'."),
            },
            ["connection"]),

        // --- Tree folders ---
        Tool("folder_create",
            "Create a folder in the connection tree, including any missing parents.",
            new() { ["folder"] = Prop("string", "Folder path, e.g. 'vps/asia'.") },
            ["folder"]),
        Tool("folder_delete",
            "Delete a folder and everything inside it. Always asks the user to confirm in the "
            + "JeekRemoteManager window first; returns an error if they decline.",
            new() { ["folder"] = Prop("string", "Folder path to delete.") },
            ["folder"]),

        Tool("folder_move",
            "Move a folder under another parent, rename it, or both.",
            new()
            {
                ["folder"] = Prop("string", "Folder path to move."),
                ["parent"] = Prop("string", "New parent folder; empty string = tree root."),
                ["name"] = Prop("string", "New folder name."),
            },
            ["folder"]),

        // --- Migration and host keys ---
        Tool("connections_import",
            "Bulk-import connections from another SSH client. Existing connections are left "
            + "alone — duplicates are skipped, not overwritten. FinalShell passwords cannot be "
            + "decrypted and must be filled in afterwards.",
            new()
            {
                ["source"] = Prop("string", "xshell, securecrt, or finalshell."),
                ["path"] = Prop("string", "That client's sessions folder on this machine."),
            },
            ["source", "path"]),
        Tool("known_hosts_list",
            "Trusted SSH host-key fingerprints, keyed by host:port.",
            new()),
        Tool("known_hosts_forget",
            "Drop a stored host fingerprint — the equivalent of ssh-keygen -R, for when a server "
            + "was rebuilt and its key legitimately changed. The next connection is then treated "
            + "as first contact instead of failing the mismatch check.",
            new()
            {
                ["host"] = Prop("string", "Host name or IP as saved on the connection."),
                ["port"] = Prop("integer", "TCP port; default 22."),
            },
            ["host"]),

        // --- Reusable server scripts ---
        Tool("script_list",
            "Reusable server scripts, grouped into suites: the scripts each suite contains and the "
            + "parameters it declares (name, type, default, enum options). Values stored for a "
            + "connection are never returned, and Secret parameters report no default. Also returns "
            + "the user script root for agents that prefer editing the files directly.",
            new()),
        Tool("script_get",
            "Get one script suite, including its parameter definition, script contents, and local "
            + "paths. Secret parameters report no default.",
            new() { ["suite"] = Prop("string", "Suite name from script_list.") },
            ["suite"]),
        Tool("script_save",
            "Create or update a user script suite. Passing 'parameters' replaces params.conf; "
            + "omitting it preserves the current definition. Named scripts are created or "
            + "overwritten, while other scripts in the suite are preserved. Reloads scripts immediately.",
            new()
            {
                ["suite"] = Prop("string", "User suite name. Must be one directory name, not a path."),
                ["parameters"] = ScriptParametersSchema(),
                ["scripts"] = ScriptContentsSchema(),
            },
            ["suite"]),
        Tool("script_reload",
            "Reload script suites from disk after an agent edits files under userRoot directly.",
            new()),
        Tool("script_run",
            "Run one script of a suite on an open session. Parameter values come from that "
            + "connection's saved binding; anything in 'params' overrides them for this run only "
            + "and is not saved. The script runs in the session's shell, so its output also "
            + "appears in the terminal.",
            SessionArgs(new()
            {
                ["suite"] = Prop("string", "Suite name from script_list."),
                ["script"] = Prop("string", "Script name (or its display title) inside that suite."),
                ["params"] = new JsonObject
                {
                    ["type"] = "object",
                    ["description"] = "Parameter overrides as name/value pairs, for this run only.",
                },
            }),
            ["suite", "script"]),

        Tool("script_run_batch",
            "Run one script across several connections — the 'apply this to all of them' case. "
            + "Sessions are opened as needed, each connection keeps its own saved parameter "
            + "binding, and one failure does not stop the rest. Returns a per-connection result.",
            new()
            {
                ["connections"] = new JsonObject
                {
                    ["type"] = "array",
                    ["description"] = "Connection tree paths to run on.",
                },
                ["suite"] = Prop("string", "Suite name from script_list."),
                ["script"] = Prop("string", "Script name (or its display title) inside that suite."),
                ["params"] = new JsonObject
                {
                    ["type"] = "object",
                    ["description"] = "Parameter overrides applied to every connection, for this run only.",
                },
                ["open_missing"] = Prop("boolean", "Open a session for connections that have none (default true)."),
                ["sequential"] = Prop("boolean", "Run one connection at a time instead of all at once (default false)."),
            },
            ["connections", "suite", "script"]),
        Tool("public_key_install",
            "Append a local SSH public key to the session account's authorized_keys. Idempotent: "
            + "an already-present key is reported rather than duplicated.",
            SessionArgs(new()
            {
                ["public_key"] = Prop("string", "The key text itself (ssh-ed25519 …)."),
                ["public_key_path"] = Prop("string", "Or a local .pub file to read it from."),
            })),

        // --- Session lifecycle (GUI actions) ---
        Tool("session_list",
            "Terminal tabs currently open, with their session id, connection, and live state. " + SessionHelp,
            new()),
        Tool("session_open",
            "Open a terminal tab for a connection and return its session id. May block while the user "
            + "completes login steps in the window (master password, two-factor); if that takes too long "
            + "the tool returns status 'awaiting_user' and the session id to poll with session_list.",
            new()
            {
                ["connection"] = Prop("string", "Connection tree path to open."),
                ["duplicate"] = Prop("boolean", "Open an extra tab on the same connection, reusing the authenticated transport (default false)."),
                ["activate"] = Prop("boolean", "Bring the window and the new tab to the front (default true)."),
                ["wait_seconds"] = Prop("integer", "How long to wait for the shell before returning awaiting_user (default 30, max 300)."),
            },
            ["connection"]),
        Tool("session_close",
            "Close a terminal tab. " + SessionHelp,
            new() { ["session"] = Prop("string", "Session id to close.") },
            ["session"]),
        Tool("session_activate",
            "Select a session's tab and bring the JeekRemoteManager window to the front — use this when "
            + "the user has to do something in the GUI. " + SessionHelp,
            new() { ["session"] = Prop("string", "Session id to show.") },
            ["session"]),

        Tool("session_move",
            "Move an open terminal tab to a new position. Positions are zero-based in session_list "
            + "order and include terminal sessions only; the fixed editor tab is not moved. "
            + "The currently active tab remains active.",
            new()
            {
                ["session"] = Prop("string", "Session id to move."),
                ["position"] = Prop("integer", "Zero-based target position among open terminal sessions."),
            },
            ["session", "position"]),

        // --- Inside a session ---
        Tool("terminal_status",
            "Read-only snapshot of a session: connected? shell lock free? command or transfer running?",
            SessionArgs()),
        Tool("terminal_run",
            "Run a non-interactive command on the session's shell and return its output. Same shell, cwd, "
            + "and environment as the user sees; does not open a new SSH session.",
            SessionArgs(new()
            {
                ["command"] = Prop("string", "Command line to run."),
                ["timeout_seconds"] = Prop("integer", "Auto-interrupt after this many seconds."),
            }),
            ["command"]),
        Tool("terminal_run_danger",
            "Same as terminal_run but for destructive work (deletes, drops, force-push, disk wipes): the "
            + "user is asked to confirm in the window before it runs.",
            SessionArgs(new()
            {
                ["command"] = Prop("string", "Command line to run."),
                ["timeout_seconds"] = Prop("integer", "Auto-interrupt after this many seconds."),
            }),
            ["command"]),
        Tool("terminal_run_batch",
            "Run the same non-interactive command across several SSH/WSL connections. Sessions are "
            + "opened as needed, concurrency is bounded, one failure does not stop the rest, and the "
            + "result includes output or an error for every connection. Dangerous commands are "
            + "confirmed once in the JeekRemoteManager window with the complete target list.",
            BatchCommandArgs(),
            ["connections", "command"]),
        Tool("terminal_run_batch_danger",
            "Same as terminal_run_batch, but always treats the command as destructive and asks for "
            + "one confirmation covering the command and every target connection.",
            BatchCommandArgs(),
            ["connections", "command"]),
        Tool("terminal_interrupt",
            "Force-interrupt the running command. Safe to call while terminal_run is still in flight.",
            SessionArgs()),
        Tool("terminal_reconnect",
            "Rebuild the SSH/WSL channel when the session is unhealthy.",
            SessionArgs()),
        Tool("terminal_scrollback",
            "Last N lines of plain text from the session's terminal buffer.",
            SessionArgs(new() { ["lines"] = Prop("integer", "Lines to return (default 200).") })),
        Tool("terminal_send_keys",
            "Write raw keys to the shell without capturing output — for pagers and interactive prompts.",
            SessionArgs(new() { ["text"] = Prop("string", "Text or key sequence to send.") }),
            ["text"]),
        Tool("file_upload",
            "Upload local Windows files to the session's remote host over the interactive shell "
            + "(ZMODEM on SSH, so jump hosts work).",
            SessionArgs(new()
            {
                ["sources"] = new JsonObject { ["type"] = "array", ["description"] = "Local file paths." },
                ["destination"] = Prop("string", "Remote directory; empty = the shell's current directory."),
            }),
            ["sources"]),
        Tool("file_download",
            "Download files from the session's remote host to this Windows machine.",
            SessionArgs(new()
            {
                ["sources"] = new JsonObject { ["type"] = "array", ["description"] = "Remote file paths." },
                ["destination"] = Prop("string", "Local directory; empty = the user's Downloads folder."),
            }),
            ["sources"]),
        Tool("monitor_snapshot",
            "CPU / memory / load / disk snapshot when the session's monitor panel has samples.",
            SessionArgs()));

    /// <summary>
    /// The fields create and update both accept, kept in one place so the two cannot drift.
    /// Credentials are deliberately absent — they only go through connection_set_password.
    /// </summary>
    private static JsonObject EditableFields(JsonObject leading)
    {
        leading["name"] = Prop("string", "Display name; also the file name on disk.");
        leading["host"] = Prop("string", "Host name or IP (SSH/RDP).");
        leading["port"] = Prop("integer", "TCP port; defaults to 22 (SSH) or 3389 (RDP).");
        leading["username"] = Prop("string", "Login user (SSH/RDP).");
        leading["private_key_path"] = Prop("string", "Private key file for SSH.");
        leading["terminal_type"] = Prop("string", "TERM sent on login; default xterm-256color.");
        leading["login_commands"] = Prop("string",
            "Commands typed after login, one per line; supports #input, #select, #pagekey, #key, "
            + "#template 1 through #template 4, and structured bastion sections "
            + "#enter / #duplicate / #leave. {{name}}, {{host}}, {{port}}, and {{username}} "
            + "resolve from the current connection after template expansion.");
        leading["wsl_distro"] = Prop("string", "WSL distribution name; empty = default distribution.");
        leading["wsl_start_directory"] = Prop("string", "Start directory inside WSL; empty = home.");
        leading["notes"] = Prop("string", "Free-form note.");
        leading["auto_open_monitor_panel"] = Prop("boolean", "Open the server monitor after login.");
        leading["auto_open_file_browser_panel"] = Prop("boolean", "Open the file browser after login.");
        return leading;
    }

    private static JsonObject CreateFields()
    {
        var properties = EditableFields(new JsonObject
        {
            ["folder"] = Prop("string", "Destination folder in the tree; empty = root."),
            ["type"] = Prop("string", "ssh (default), wsl, or rdp."),
        });
        properties["open"] = Prop("boolean", "Open a session immediately after creating (default false).");
        return properties;
    }

    /// <summary>Every in-session tool takes the same addressing arguments.</summary>
    private static JsonObject SessionArgs(JsonObject? extra = null)
    {
        var properties = new JsonObject
        {
            ["session"] = Prop("string", "Session id from session_list. " + SessionHelp),
            ["connection"] = Prop("string",
                "Connection tree path instead of a session id; resolves to that connection's open session."),
        };

        if (extra is not null)
        {
            foreach (var property in extra.ToList())
            {
                extra.Remove(property.Key);
                properties[property.Key] = property.Value;
            }
        }

        return properties;
    }

    private static JsonObject BatchCommandArgs() => new()
    {
        ["connections"] = new JsonObject
        {
            ["type"] = "array",
            ["description"] = "Explicit connection tree paths to run on.",
        },
        ["command"] = Prop("string", "Command line to run on every connection."),
        ["timeout_seconds"] = Prop("integer", "Per-connection auto-interrupt timeout."),
        ["open_missing"] = Prop("boolean", "Open sessions for connections that have none (default true)."),
        ["max_parallel"] = Prop("integer", "Maximum simultaneous commands (default 4, range 1-16)."),
    };

    private static JsonObject Tool(string name, string description, JsonObject properties, string[]? required = null)
    {
        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (required is { Length: > 0 })
            schema["required"] = new JsonArray(required.Select(JsonNode (r) => r).ToArray());
        return new JsonObject { ["name"] = name, ["description"] = description, ["inputSchema"] = schema };
    }

    private static JsonObject Prop(string type, string description) =>
        new() { ["type"] = type, ["description"] = description };

    private static JsonObject ScriptParametersSchema() => new()
    {
        ["type"] = "array",
        ["description"] = "Complete parameter definition. Omit to preserve it; [] clears it.",
        ["items"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["name"] = Prop("string", "Shell environment variable name."),
                ["type"] = Prop("string", "string, number, bool, secret, or enum."),
                ["default"] = Prop("string", "Optional default value."),
                ["options"] = new JsonObject
                {
                    ["type"] = "array",
                    ["description"] = "Allowed values when type is enum.",
                    ["items"] = new JsonObject { ["type"] = "string" },
                },
            },
            ["required"] = new JsonArray("name", "type"),
        },
    };

    private static JsonObject ScriptContentsSchema() => new()
    {
        ["type"] = "array",
        ["description"] = "Script files to create or overwrite. Other files are preserved.",
        ["items"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["name"] = Prop("string", "File name ending in .sh; paths are not accepted."),
                ["content"] = Prop("string", "Complete shell script content."),
            },
            ["required"] = new JsonArray("name", "content"),
        },
    };
}
