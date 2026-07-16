using System.Linq;
using System.Text.Json.Nodes;

namespace JeekRemoteManager.Services;

public sealed class DebugMcpDiscovery
{
    public string Url { get; set; } = "";
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

    private const string PathHelp =
        "Paths start from a root: App (the Application), Desktop (the desktop lifetime), " +
        "MainWindow, or MainVm (MainWindow.DataContext). Segments: '.Member' reads a property or field " +
        "(non-public included), '[0]' indexes a list, '[\"key\"]' indexes a dictionary, and " +
        "'#Name' finds a named control in the visual tree below the current object. " +
        "Examples: MainVm.Nodes[0].Name, MainWindow.#ConnectionTree.SelectedItem";

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
        Tool("read_logs", "Read the current app log tail.",
            new() { ["lines"] = Prop("integer", "Lines, 1-2000 (default 200)."), ["filter"] = Prop("string", "Case-insensitive filter.") }),
        Tool("ai_runtime_snapshot",
            "Snapshot each terminal tab's AI CLI panel: provider, running/install state, SSH terminal visibility, MCP URL, and command execution counts.",
            new()));

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
