using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using JeekRemoteManager.Services;

var workspace = ResolveWorkspace(args);
var discoveryPath = Path.Combine(workspace, "bin", "debug-mcp.json");
using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(65) };

while (await Console.In.ReadLineAsync() is { } line)
{
    if (string.IsNullOrWhiteSpace(line))
        continue;

    JsonNode? response;
    try
    {
        var request = JsonNode.Parse(line);
        response = request switch
        {
            JsonObject single => await HandleAsync(single),
            JsonArray batch => await HandleBatchAsync(batch),
            _ => RpcError(null, -32600, "Invalid request"),
        };
    }
    catch (Exception ex)
    {
        response = RpcError(null, -32700, $"Parse error: {ex.Message}");
    }

    if (response is not null)
    {
        await Console.Out.WriteLineAsync(response.ToJsonString());
        await Console.Out.FlushAsync();
    }
}

async Task<JsonNode?> HandleBatchAsync(JsonArray batch)
{
    var responses = new JsonArray();
    foreach (var item in batch)
        if (item is JsonObject request && await HandleAsync(request) is { } response)
            responses.Add(response);
    return responses.Count == 0 ? null : responses;
}

async Task<JsonNode?> HandleAsync(JsonObject request)
{
    var id = request["id"]?.DeepClone();
    var method = request["method"]?.GetValue<string>();
    if (method is null)
        return null;

    switch (method)
    {
        case "initialize":
            var requested = (request["params"] as JsonObject)?["protocolVersion"]?.GetValue<string>();
            return RpcResult(id, DebugMcpContract.InitializeResult(
                "jeek-remote-manager-debug-bridge",
                "JeekRemoteManager Worktree Debug Bridge",
                "1",
                requested));
        case "ping":
            return RpcResult(id, new JsonObject());
        case "tools/list":
            return RpcResult(id, new JsonObject { ["tools"] = DebugMcpContract.BuildToolList() });
        case "tools/call":
            return await ForwardToolCallAsync(request, id);
        default:
            if (method.StartsWith("notifications/", StringComparison.Ordinal))
                return null;
            return RpcError(id, -32601, $"Method not found: {method}");
    }
}

async Task<JsonNode> ForwardToolCallAsync(JsonObject request, JsonNode? id)
{
    try
    {
        var discovery = ReadAndValidateDiscovery();
        using var content = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(discovery.Url, content);
        var json = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Debug app returned HTTP {(int)response.StatusCode}: {json}");
        return JsonNode.Parse(json) ?? throw new InvalidDataException("Debug app returned an empty response.");
    }
    catch (Exception ex)
    {
        return RpcResult(id, ToolError(
            $"No usable Debug instance for workspace '{workspace}'. " +
            $"Build and launch this worktree, then retry. {ex.Message}"));
    }
}

DebugMcpDiscovery ReadAndValidateDiscovery()
{
    if (!File.Exists(discoveryPath))
        throw new FileNotFoundException("Discovery file is missing.", discoveryPath);

    var discovery = JsonSerializer.Deserialize<DebugMcpDiscovery>(File.ReadAllText(discoveryPath))
        ?? throw new InvalidDataException("Discovery file is invalid.");
    if (!string.Equals(Path.GetFullPath(discovery.WorkspaceRoot), workspace, StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException("Discovery belongs to a different worktree.");

    var process = Process.GetProcessById(discovery.ProcessId);
    if (process.HasExited)
        throw new InvalidDataException("Discovery process has exited.");
    var executable = process.MainModule?.FileName;
    if (string.IsNullOrWhiteSpace(executable)
        || !string.Equals(Path.GetFullPath(executable), Path.GetFullPath(discovery.ExecutablePath),
            StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException("Discovery process does not match the recorded executable.");
    return discovery;
}

static string ResolveWorkspace(string[] commandLine)
{
    var value = Directory.GetCurrentDirectory();
    for (var i = 0; i + 1 < commandLine.Length; i++)
        if (commandLine[i] == "--workspace")
            value = commandLine[++i];

    var directory = new DirectoryInfo(Path.GetFullPath(value));
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "JeekRemoteManager.slnx")))
        directory = directory.Parent;
    return directory?.FullName ?? throw new DirectoryNotFoundException($"Cannot find workspace above '{value}'.");
}

static JsonObject RpcResult(JsonNode? id, JsonNode result) =>
    new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

static JsonObject RpcError(JsonNode? id, int code, string message) =>
    new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

static JsonObject ToolError(string message) => new()
{
    ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = message }),
    ["isError"] = true,
};
