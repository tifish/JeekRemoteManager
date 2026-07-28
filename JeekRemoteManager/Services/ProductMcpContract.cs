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

        // --- Reusable server scripts ---
        Tool("script_list",
            "Reusable server scripts, grouped into suites: the scripts each suite contains and the "
            + "parameters it declares (name, type, default, enum options). Values stored for a "
            + "connection are never returned, and Secret parameters report no default.",
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
            "Commands typed after login, one per line; supports #select / #pagekey / #duplicate.");
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
