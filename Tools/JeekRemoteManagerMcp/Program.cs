using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json.Nodes;
using JeekRemoteManager.Services;

[assembly: SupportedOSPlatform("windows")]

// JeekRemoteManager MCP stdio adapter.
//
// An agent launches this executable as an ordinary stdio MCP server; it forwards JSON-RPC
// to the running app over a named pipe. Nothing here knows about ports, so the client
// config a user puts in their project never goes stale:
//
//   { "command": "C:\\Users\\...\\AppData\\Local\\JeekRemoteManager\\Mcp\\JeekRemoteManagerMcp.exe",
//     "args": ["--connection", "vps/bwg"] }
//
// The fixed adapter reads the app path and pipe names from HKCU. Release is the default route;
// Debug configs pass their path-derived instance id so parallel worktrees stay separate. A
// side-by-side adapter remains supported for development and direct Debug MCP checks.

var options = AdapterOptions.Parse(args);

using var stdin = new StreamReader(Console.OpenStandardInput(), AdapterText.Utf8);
await using var stdout = new StreamWriter(Console.OpenStandardOutput(), AdapterText.Utf8) { AutoFlush = true };

using var connection = new PipeConnection(options);

while (await stdin.ReadLineAsync().ConfigureAwait(false) is { } line)
{
    if (string.IsNullOrWhiteSpace(line))
        continue;

    JsonNode? message;
    try
    {
        message = JsonNode.Parse(line);
    }
    catch (Exception ex)
    {
        await stdout.WriteLineAsync(
            AdapterText.RpcError(null, -32700, $"Parse error: {ex.Message}").ToJsonString()).ConfigureAwait(false);
        continue;
    }

    if (message is not null)
        await HandleAsync(message).ConfigureAwait(false);
}

async Task HandleAsync(JsonNode message)
{
    var envelope = message as JsonObject;
    var method = envelope?["method"]?.GetValue<string>();
    var id = envelope?["id"]?.DeepClone();

    // Only a real tool call is worth starting the GUI for: MCP clients open stdio servers
    // when a session begins, and popping a window on every session start would be rude.
    var mayLaunch = options.AutoLaunch && method == "tools/call";
    if (envelope is not null && method == "tools/call")
        ApplyDefaultArguments(envelope);

    string? response;
    try
    {
        response = await connection
            .SendAsync(message, AdapterText.ExpectsResponse(message), mayLaunch)
            .ConfigureAwait(false);
    }
    catch (Exception ex)
    {
        await stdout.WriteLineAsync(OfflineResponse(method, id, ex.Message).ToJsonString()).ConfigureAwait(false);
        return;
    }

    if (response is not null)
        await stdout.WriteLineAsync(response).ConfigureAwait(false);
}

// Fills in the connection this adapter was pinned to, so a linked project does not have to
// name it on every call. An explicit argument always wins.
void ApplyDefaultArguments(JsonObject call)
{
    if (options.Connection is not { Length: > 0 } connectionPath)
        return;
    if (call["params"] is not JsonObject parameters)
        return;

    if (parameters["arguments"] is not JsonObject toolArgs)
    {
        toolArgs = [];
        parameters["arguments"] = toolArgs;
    }

    if (toolArgs["connection"] is null)
        toolArgs["connection"] = connectionPath;
}

// The app is unreachable. Keep the session usable instead of failing the handshake: the
// client stays connected, and only real tool calls report why nothing happened.
JsonNode OfflineResponse(string? method, JsonNode? id, string reason) => method switch
{
    "initialize" => AdapterText.RpcResult(id, new JsonObject
    {
        ["protocolVersion"] = "2025-06-18",
        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
        ["serverInfo"] = new JsonObject
        {
            ["name"] = options.ServerName,
            ["title"] = "JeekRemoteManager",
            ["version"] = "1",
        },
    }),
    "ping" => AdapterText.RpcResult(id, new JsonObject()),
    "tools/list" => AdapterText.RpcResult(id, new JsonObject { ["tools"] = new JsonArray() }),
    "tools/call" => AdapterText.RpcResult(id, new JsonObject
    {
        ["content"] = new JsonArray(new JsonObject
        {
            ["type"] = "text",
            ["text"] = $"JeekRemoteManager is not reachable on {options.DescribePipes()}. "
                       + $"Start the app and retry. Details: {reason}",
        }),
        ["isError"] = true,
    }),
    _ => AdapterText.RpcError(id, -32601, $"Method not available while JeekRemoteManager is closed: {method}"),
};

/// <summary>JSON-RPC helpers and the encoding shared by both ends of the adapter.</summary>
internal static class AdapterText
{
    internal static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    internal static bool ExpectsResponse(JsonNode message) => message switch
    {
        JsonObject single => single["id"] is not null,
        JsonArray batch => batch.Any(item => item is JsonObject entry && entry["id"] is not null),
        _ => true,
    };

    internal static JsonObject RpcResult(JsonNode? id, JsonNode result) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    internal static JsonObject RpcError(JsonNode? id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };
}

/// <summary>Command line of the adapter.</summary>
internal sealed record AdapterOptions(
    IReadOnlyList<string> PipeNames,
    string Surface,
    string? Connection,
    bool AutoLaunch,
    string AppPath)
{
    public string ServerName => IsDebugSurface
        ? "jeek-remote-manager-debug"
        : "jeek-remote-manager";

    public bool IsDebugSurface => Surface.Equals("debug", StringComparison.OrdinalIgnoreCase);

    public string DescribePipes() =>
        string.Join(" or ", PipeNames.Select(name => $@"\\.\pipe\{name}"));

    public static AdapterOptions Parse(string[] args)
    {
        var surface = "product";
        string? pipe = null;
        string? instance = null;
        string? connection = null;
        string? appPath = null;
        bool? launch = null;

        for (var i = 0; i < args.Length; i++)
        {
            var value = i + 1 < args.Length ? args[i + 1] : null;
            switch (args[i])
            {
                case "--surface" when value is not null:
                    surface = value;
                    i++;
                    break;
                case "--pipe" when value is not null:
                    pipe = value;
                    i++;
                    break;
                case "--instance" when value is not null:
                    instance = value;
                    i++;
                    break;
                case "--connection" when value is not null:
                    connection = value;
                    i++;
                    break;
                case "--app" when value is not null:
                    appPath = value;
                    i++;
                    break;
                case "--launch":
                    launch = true;
                    break;
                case "--no-launch":
                    launch = false;
                    break;
            }
        }

        var baseDirectory = AppContext.BaseDirectory;
        var sideBySideAppPath = Path.Combine(baseDirectory, "JeekRemoteManager.exe");
        var isSideBySide = File.Exists(sideBySideAppPath);
        var routeInstance = instance
                            ?? (isSideBySide ? McpPipeNames.InstanceId(baseDirectory) : "release");
        McpRegisteredInstance? registered = null;
        if (!isSideBySide
            && McpAdapterRegistry.TryReadInstance(routeInstance, out var resolved))
        {
            registered = resolved;
        }

        appPath ??= registered?.AppPath ?? sideBySideAppPath;

        // A fixed adapter uses the exact registered pipe. A side-by-side development adapter can
        // derive its Debug id, but still tries the bare Release pipe as a compatibility fallback.
        List<string> pipes;
        if (pipe is { Length: > 0 })
        {
            pipes = [pipe];
        }
        else if (registered is not null)
        {
            var registeredPipe = surface.Equals("debug", StringComparison.OrdinalIgnoreCase)
                ? registered.DebugPipeName
                : registered.ProductPipeName;
            pipes =
            [
                registeredPipe.Length > 0
                    ? registeredPipe
                    : McpPipeNames.Resolve(surface, routeInstance),
            ];
        }
        else
        {
            var derived = McpPipeNames.Resolve(surface, routeInstance);
            var bare = McpPipeNames.Resolve(surface, null);
            // An explicitly routed or fixed adapter must never fall back to Release: if a Debug
            // worktree is offline, reaching the user's installed instance would be dangerous.
            var strictRoute = instance is not null || !isSideBySide;
            pipes = derived == bare || strictRoute ? [derived] : [derived, bare];
        }

        // Debug worktrees are driven by a developer who already has the app open; only the
        // product surface starts it on demand.
        launch ??= !surface.Equals("debug", StringComparison.OrdinalIgnoreCase);

        return new AdapterOptions(pipes, surface, connection, launch.Value, appPath);
    }
}

/// <summary>Lazily connected, self-healing named pipe client.</summary>
internal sealed class PipeConnection(AdapterOptions options) : IDisposable
{
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;

    /// <summary>
    /// Forwards one message and returns the matching response line, or null when the
    /// message was a notification. Retries once on a broken pipe so an app restart does not
    /// end the agent's session.
    /// </summary>
    public async Task<string?> SendAsync(JsonNode message, bool expectsResponse, bool mayLaunch)
    {
        var payload = message.ToJsonString();
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var (reader, writer) = await ConnectAsync(mayLaunch).ConfigureAwait(false);
                await writer.WriteLineAsync(payload).ConfigureAwait(false);
                if (!expectsResponse)
                    return null;

                // Skip server-initiated notifications so they cannot be mistaken for the
                // reply to this request (the pipe is duplex; the app may push later).
                while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    if (line.Length == 0)
                        continue;
                    if (JsonNode.Parse(line) is JsonObject reply && reply["id"] is null)
                        continue;
                    return line;
                }

                throw new IOException("The app closed the pipe before replying.");
            }
            catch (Exception) when (attempt == 0)
            {
                Reset();
            }
        }
    }

    private async Task<(StreamReader Reader, StreamWriter Writer)> ConnectAsync(bool mayLaunch)
    {
        if (_reader is { } reader && _writer is { } writer && _pipe?.IsConnected == true)
            return (reader, writer);

        Reset();

        try
        {
            await OpenAsync(500).ConfigureAwait(false);
        }
        catch (Exception) when (mayLaunch)
        {
            LaunchApp();
            // The GUI has to start, unlock settings, and register the pipe.
            await OpenAsync(30000).ConfigureAwait(false);
        }

        return (_reader!, _writer!);
    }

    private async Task OpenAsync(int timeoutMilliseconds)
    {
        Exception? lastError = null;
        foreach (var name in options.PipeNames)
        {
            var pipe = new NamedPipeClientStream(
                ".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(timeoutMilliseconds / options.PipeNames.Count + 1).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                lastError = ex;
                await pipe.DisposeAsync().ConfigureAwait(false);
                continue;
            }

            _pipe = pipe;
            _reader = new StreamReader(pipe, AdapterText.Utf8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            _writer = new StreamWriter(pipe, AdapterText.Utf8, leaveOpen: true) { AutoFlush = true };
            return;
        }

        throw lastError ?? new IOException($"Could not connect to {options.DescribePipes()}.");
    }

    private void LaunchApp()
    {
        if (!File.Exists(options.AppPath))
            throw new FileNotFoundException("JeekRemoteManager executable not found.", options.AppPath);

        Process.Start(new ProcessStartInfo
        {
            FileName = options.AppPath,
            WorkingDirectory = Path.GetDirectoryName(options.AppPath) ?? Environment.CurrentDirectory,
            UseShellExecute = true,
        });
    }

    private void Reset()
    {
        try { _reader?.Dispose(); } catch { /* torn down */ }
        try { _writer?.Dispose(); } catch { /* torn down */ }
        try { _pipe?.Dispose(); } catch { /* torn down */ }
        _reader = null;
        _writer = null;
        _pipe = null;
    }

    public void Dispose() => Reset();
}
