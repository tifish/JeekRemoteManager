using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text.Json;
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
//   { "command": "cmd",
//     "args": ["/c", ".\\JeekRemoteManagerMcp.cmd", "--connection", "vps/bwg"],
//     "cwd": "." }
//
// The stable per-user install is the agent entrypoint (so builds can overwrite bin\ without
// fighting a running MCP process). It reads the app path and pipe names from HKCU; Release is
// the default route. Debug worktrees pass --instance <id> or --app <worktree\bin\app.exe> so
// parallel instances stay separate. A side-by-side copy next to the app remains supported for
// packaging and first-run install into the fixed path.

var options = AdapterOptions.Parse(args);

using var stdin = new StreamReader(Console.OpenStandardInput(), AdapterText.Utf8);
await using var stdout = new StreamWriter(Console.OpenStandardOutput(), AdapterText.Utf8) { AutoFlush = true };
using var stdoutGate = new SemaphoreSlim(1, 1);

async Task WriteStdoutAsync(string line)
{
    await stdoutGate.WaitAsync().ConfigureAwait(false);
    try
    {
        await stdout.WriteLineAsync(line).ConfigureAwait(false);
    }
    finally
    {
        stdoutGate.Release();
    }
}

using var connection = new PipeConnection(options, WriteStdoutAsync);

// The tool list the client last received, serialized, or null before it asked. Used to tell
// the client when the app it reaches now offers a different list than the one it cached —
// after the app starts, restarts, or is rebuilt with new tools.
string? clientToolsJson = null;
var clientToolsGate = new object();
var inFlight = new ConcurrentDictionary<int, Task>();
var nextTaskId = 0;

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
        await WriteStdoutAsync(
            AdapterText.RpcError(null, -32700, $"Parse error: {ex.Message}").ToJsonString()).ConfigureAwait(false);
        continue;
    }

    if (message is not null)
    {
        var taskId = Interlocked.Increment(ref nextTaskId);
        var task = HandleAsync(message);
        inFlight[taskId] = task;
        _ = task.ContinueWith(
            _ => inFlight.TryRemove(taskId, out var removed),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}

await Task.WhenAll(inFlight.Values).ConfigureAwait(false);

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
        var offline = OfflineResponse(method, id, ex.Message);
        if (method == "tools/list")
        {
            lock (clientToolsGate)
                clientToolsJson = ToolsJson(offline);
        }
        if (AdapterText.ExpectsResponse(message))
            await WriteStdoutAsync(offline.ToJsonString()).ConfigureAwait(false);
        return;
    }

    if (response is not null)
    {
        if (method == "initialize")
            response = AdvertiseListChanged(response);
        else if (method == "tools/list")
        {
            lock (clientToolsGate)
                clientToolsJson = ToolsJson(JsonNode.Parse(response));
        }
        await WriteStdoutAsync(response).ConfigureAwait(false);
    }

    // A fresh pipe means a different app process than the one the client's tool list came
    // from (it just started, or restarted). Compare, and ask the client to re-list if the
    // tools differ — without this the client keeps the list from session start forever.
    if (connection.TakeFreshConnection() && method != "tools/list")
    {
        lock (clientToolsGate)
        {
            if (clientToolsJson is null)
                return;
        }
        await NotifyIfToolsChangedAsync().ConfigureAwait(false);
    }
}

async Task NotifyIfToolsChangedAsync()
{
    string? current;
    try
    {
        var reply = await connection.SendAsync(
            new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = "jrm-adapter-tools-refresh-" + Guid.NewGuid().ToString("N"),
                ["method"] = "tools/list",
            },
            expectsResponse: true,
            mayLaunch: false).ConfigureAwait(false);
        current = reply is null ? null : ToolsJson(JsonNode.Parse(reply));
    }
    catch
    {
        return;
    }

    lock (clientToolsGate)
    {
        if (current is null || current == clientToolsJson)
            return;

        clientToolsJson = current;
    }
    await WriteStdoutAsync(
        new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/tools/list_changed" }.ToJsonString())
        .ConfigureAwait(false);
}

static string? ToolsJson(JsonNode? reply) => reply?["result"]?["tools"]?.ToJsonString();

// The adapter can tell the client when the tool list changes, so say so in the handshake.
static string AdvertiseListChanged(string response)
{
    if (JsonNode.Parse(response) is not JsonObject reply || reply["result"] is not JsonObject result)
        return response;
    if (result["capabilities"] is not JsonObject capabilities)
        result["capabilities"] = capabilities = new JsonObject();
    if (capabilities["tools"] is not JsonObject tools)
        capabilities["tools"] = tools = new JsonObject();
    tools["listChanged"] = true;
    return reply.ToJsonString();
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
// client stays connected, and only real tool calls report why nothing happened. The tool list
// comes from the same contract the app serves (both files are linked into this project): an
// empty list would leave the agent with nothing to call, and since only a tools/call starts
// the app, the product surface could then never come up at all.
JsonNode OfflineResponse(string? method, JsonNode? id, string reason) => method switch
{
    "initialize" => AdapterText.RpcResult(id, new JsonObject
    {
        ["protocolVersion"] = "2025-06-18",
        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = true } },
        ["serverInfo"] = new JsonObject
        {
            ["name"] = options.ServerName,
            ["title"] = "JeekRemoteManager",
            ["version"] = "1",
        },
    }),
    "ping" => AdapterText.RpcResult(id, new JsonObject()),
    "tools/list" => AdapterText.RpcResult(id, new JsonObject
    {
        ["tools"] = options.IsDebugSurface
            ? DebugMcpContract.BuildToolList()
            : ProductMcpContract.BuildToolList(),
    }),
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

        // Explicit --app pins a worktree/install even when this process is the fixed per-user
        // adapter (not side-by-side). Instance id is always derived from the app directory.
        string? appDirectory = null;
        if (appPath is { Length: > 0 })
        {
            appPath = Path.GetFullPath(appPath);
            appDirectory = Path.GetDirectoryName(appPath);
        }

        var routeInstance = instance
                            ?? (appDirectory is { Length: > 0 }
                                ? McpPipeNames.InstanceId(appDirectory)
                                : null)
                            ?? (isSideBySide ? McpPipeNames.InstanceId(baseDirectory) : "release");
        var explicitlyRouted = instance is not null || appDirectory is { Length: > 0 };

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
            var strictRoute = explicitlyRouted || !isSideBySide;
            pipes = derived == bare || strictRoute ? [derived] : [derived, bare];
        }

        // Debug worktrees are driven by a developer who already has the app open; only the
        // product surface starts it on demand.
        launch ??= !surface.Equals("debug", StringComparison.OrdinalIgnoreCase);

        return new AdapterOptions(pipes, surface, connection, launch.Value, appPath);
    }
}

/// <summary>Lazily connected, self-healing named pipe client.</summary>
internal sealed class PipeConnection(
    AdapterOptions options,
    Func<string, Task> forwardNotification) : IDisposable
{
    private readonly object _stateGate = new();
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    // Each connection owns its replies. A late failure/read from the old pipe must never
    // close its replacement or complete a retried request with the same JSON-RPC id.
    private sealed class PipeSession(NamedPipeClientStream pipe)
    {
        public NamedPipeClientStream Pipe { get; } = pipe;
        public StreamReader Reader { get; } = new(pipe, AdapterText.Utf8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        public StreamWriter Writer { get; } = new(pipe, AdapterText.Utf8, leaveOpen: true) { AutoFlush = true };
        public ConcurrentDictionary<string, TaskCompletionSource<string>> Pending { get; } = new(StringComparer.Ordinal);
    }

    private PipeSession? _session;
    private int _freshConnection;
    private bool _disposed;

    /// <summary>True once after each newly opened pipe, i.e. after reaching a (re)started app.</summary>
    public bool TakeFreshConnection()
    {
        return Interlocked.Exchange(ref _freshConnection, 0) != 0;
    }

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
            TaskCompletionSource<string>? reply = null;
            string? requestKey = null;
            PipeSession? session = null;
            try
            {
                session = await ConnectAsync(mayLaunch).ConfigureAwait(false);
                if (expectsResponse)
                {
                    requestKey = RequestKey(message)
                                 ?? throw new InvalidOperationException("A request expecting a response must have an id.");
                    reply = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                    lock (_stateGate)
                    {
                        if (!ReferenceEquals(session, _session))
                            throw new IOException("The app pipe changed before the request could be registered.");
                        if (!session.Pending.TryAdd(requestKey, reply))
                            throw new InvalidOperationException($"A request with id {requestKey} is already in flight.");
                    }
                }

                await _writeGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (!ReferenceEquals(session, _session) || !session.Pipe.IsConnected)
                        throw new IOException("The app pipe changed before the request could be written.");
                    await session.Writer.WriteLineAsync(payload).ConfigureAwait(false);
                }
                finally
                {
                    _writeGate.Release();
                }

                return reply is null ? null : await reply.Task.ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt == 0 && ex is IOException or ObjectDisposedException or TimeoutException or JsonException)
            {
                if (session is not null)
                    Reset(ex, session);
            }
            finally
            {
                if (requestKey is not null && reply is not null && session is not null)
                    session.Pending.TryRemove(new KeyValuePair<string, TaskCompletionSource<string>>(requestKey, reply));
            }
        }
    }

    private async Task<PipeSession> ConnectAsync(bool mayLaunch)
    {
        lock (_stateGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_session is { Pipe.IsConnected: true } existing)
                return existing;
        }

        await _connectGate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_stateGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_session is { Pipe.IsConnected: true } existing)
                    return existing;
            }

            Reset(new IOException("Replacing an unusable app pipe."));
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

            lock (_stateGate)
                return _session ?? throw new IOException("The app closed the pipe during connection.");
        }
        finally
        {
            _connectGate.Release();
        }
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

            var session = new PipeSession(pipe);
            lock (_stateGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _session = session;
                Interlocked.Exchange(ref _freshConnection, 1);
            }
            _ = Task.Run(() => ReadLoopAsync(session));
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

    private async Task ReadLoopAsync(PipeSession session)
    {
        Exception ended = new IOException("The app closed the pipe before replying.");
        try
        {
            while (await session.Reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0)
                    continue;

                JsonNode? message = JsonNode.Parse(line);
                var key = message is null ? null : RequestKey(message);
                if (key is not null && session.Pending.TryRemove(key, out var reply))
                    reply.TrySetResult(line);
                else if (message is JsonObject notification && notification["id"] is null)
                    await forwardNotification(line).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            ended = ex;
        }
        finally
        {
            Reset(ended, session);
        }
    }

    private static string? RequestKey(JsonNode message) => message switch
    {
        JsonObject single => single["id"]?.ToJsonString(),
        JsonArray batch => batch.OfType<JsonObject>()
            .Select(entry => entry["id"])
            .FirstOrDefault(id => id is not null)?
            .ToJsonString(),
        _ => null,
    };

    private void Reset(Exception reason, PipeSession? expectedSession = null)
    {
        PipeSession? session;
        lock (_stateGate)
        {
            if (expectedSession is not null && !ReferenceEquals(expectedSession, _session))
                return;
            session = _session;
            _session = null;
        }

        if (session is null)
            return;
        try { session.Pipe.Dispose(); } catch { /* torn down */ }
        try { session.Reader.Dispose(); } catch { /* torn down */ }
        try { session.Writer.Dispose(); } catch { /* torn down */ }
        foreach (var pending in session.Pending.ToArray())
        {
            if (session.Pending.TryRemove(pending.Key, out var reply))
                reply.TrySetException(reason);
        }
    }

    public void Dispose()
    {
        lock (_stateGate)
            _disposed = true;
        Reset(new ObjectDisposedException(nameof(PipeConnection)));
        _connectGate.Dispose();
        _writeGate.Dispose();
    }
}
