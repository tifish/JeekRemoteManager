using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using JeekTools;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace JeekRemoteManager.Services;

/// <summary>
/// Per-terminal product MCP server (loopback HTTP JSON-RPC). Exposes only remote
/// terminal/file tools for agent CLIs — never the Debug MCP object-graph surface.
/// </summary>
public sealed class AgentRemoteMcpServer : IAsyncDisposable
{
    private static readonly ILogger Log = LogManager.CreateLogger(nameof(AgentRemoteMcpServer));
    private static readonly string[] ProtocolVersions = ["2024-11-05", "2025-03-26", "2025-06-18"];

    private readonly IAgentRemoteTools _tools;

    /// <summary>When true, destructive commands skip JRM's additional confirmation dialog.</summary>
    public bool AutoApproveDangerousCommands { get; set; }
    private readonly string _token;
    private HttpListener? _listener;
    private int _port;
    private int _stopped;

    public AgentRemoteMcpServer(IAgentRemoteTools tools)
    {
        _tools = tools;
        _token = Convert.ToHexString(Guid.NewGuid().ToByteArray())[..16].ToLowerInvariant();
    }

    /// <summary>Full MCP endpoint URL once <see cref="Start"/> succeeds.</summary>
    public string? EndpointUrl { get; private set; }

    public void Start()
    {
        if (_listener is not null)
            return;

        Exception? lastError = null;
        for (var port = 18737; port < 18837; port++)
        {
            foreach (var host in new[] { "127.0.0.1", "localhost" })
            {
                var listener = new HttpListener();
                // Token in the path scopes the listener and makes accidental probes harder.
                listener.Prefixes.Add($"http://{host}:{port}/agent/{_token}/");
                try
                {
                    listener.Start();
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    listener.Close();
                    continue;
                }

                _listener = listener;
                _port = port;
                EndpointUrl = $"http://{host}:{port}/agent/{_token}/mcp";
                _ = Task.Run(() => ListenLoopAsync(listener));
                Log.ZLogInformation($"Agent remote MCP listening on {EndpointUrl} for {_tools.ConnectionLabel}");
                return;
            }
        }

        throw new InvalidOperationException(
            $"Could not bind agent MCP listener: {lastError?.Message ?? "no free port"}");
    }

    private async Task ListenLoopAsync(HttpListener listener)
    {
        while (listener.IsListening && Volatile.Read(ref _stopped) == 0)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (HttpListenerException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.ZLogWarning(ex, $"Agent MCP accept failed");
                continue;
            }

            _ = Task.Run(() => HandleContextAsync(context));
        }
    }

    private async Task HandleContextAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        try
        {
            if (!IsLoopback(request.RemoteEndPoint?.Address))
            {
                response.StatusCode = 403;
                return;
            }

            // CORS preflight for streamable HTTP clients that probe from local origins.
            if (request.HttpMethod.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                response.StatusCode = 204;
                response.Headers["Access-Control-Allow-Origin"] = "*";
                response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
                response.Headers["Access-Control-Allow-Headers"] = "content-type, accept, mcp-session-id";
                return;
            }

            if (!request.Url!.AbsolutePath.EndsWith("/mcp", StringComparison.OrdinalIgnoreCase))
            {
                response.StatusCode = 404;
                return;
            }

            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase))
            {
                // Streamable HTTP clients may open GET for SSE; answer with empty stream end.
                response.StatusCode = 405;
                response.Headers["Allow"] = "POST, OPTIONS";
                return;
            }

            if (!request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                response.StatusCode = 405;
                return;
            }

            string body;
            using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
                body = await reader.ReadToEndAsync().ConfigureAwait(false);

            JsonNode? reply = null;
            if (!string.IsNullOrWhiteSpace(body))
            {
                var parsed = JsonNode.Parse(body);
                reply = parsed switch
                {
                    JsonObject single => await HandleMessageAsync(single).ConfigureAwait(false),
                    JsonArray batch => await HandleBatchAsync(batch).ConfigureAwait(false),
                    _ => RpcError(null, -32600, "Invalid request"),
                };
            }

            response.Headers["Access-Control-Allow-Origin"] = "*";
            response.ContentType = "application/json; charset=utf-8";
            if (reply is null)
            {
                response.StatusCode = 202;
                return;
            }

            var bytes = Encoding.UTF8.GetBytes(reply.ToJsonString());
            response.StatusCode = 200;
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.ZLogWarning(ex, $"Agent MCP request failed");
            try { response.StatusCode = 500; } catch { /* ignore */ }
        }
        finally
        {
            try { response.Close(); } catch { /* ignore */ }
        }
    }

    private async Task<JsonNode?> HandleBatchAsync(JsonArray batch)
    {
        var results = new JsonArray();
        foreach (var item in batch)
        {
            if (item is not JsonObject message)
                continue;
            if (await HandleMessageAsync(message).ConfigureAwait(false) is { } result)
                results.Add(result);
        }

        return results.Count > 0 ? results : null;
    }

    private async Task<JsonNode?> HandleMessageAsync(JsonObject message)
    {
        var id = message["id"]?.DeepClone();
        var isRequest = id != null;
        var method = message["method"]?.GetValue<string>();
        if (method is null)
            return null;

        try
        {
            return method switch
            {
                "initialize" => RpcResult(id, HandleInitialize(message["params"] as JsonObject)),
                "notifications/initialized" => null,
                "ping" => RpcResult(id, new JsonObject()),
                "tools/list" => RpcResult(id, new JsonObject { ["tools"] = BuildToolList() }),
                "tools/call" => RpcResult(id, await HandleToolCallAsync(message["params"] as JsonObject).ConfigureAwait(false)),
                _ when method.StartsWith("notifications/", StringComparison.Ordinal) => null,
                _ => isRequest ? RpcError(id, -32601, $"Method not found: {method}") : null,
            };
        }
        catch (Exception ex)
        {
            Log.ZLogError(ex, $"Agent MCP method {method} failed");
            return isRequest ? RpcError(id, -32603, ex.Message) : null;
        }
    }

    private JsonObject HandleInitialize(JsonObject? parameters)
    {
        var requested = parameters?["protocolVersion"]?.GetValue<string>();
        var protocol = requested is not null && Array.IndexOf(ProtocolVersions, requested) >= 0
            ? requested
            : ProtocolVersions[^1];
        return new JsonObject
        {
            ["protocolVersion"] = protocol,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = "jeek-remote-manager",
                ["title"] = $"JeekRemoteManager ({_tools.ConnectionLabel})",
                ["version"] = "1",
            },
            ["instructions"] =
                "Tools operate the interactive remote terminal currently open in JeekRemoteManager " +
                $"(connection: {_tools.ConnectionLabel}). They do not open a new SSH session. " +
                "Local shell tools still run on the Windows host.",
        };
    }

    private JsonArray BuildToolList()
    {
        var transferHint = _tools.IsWsl
            ? "On WSL, prefer cp between /mnt/c/... and the distro; this tool still works when useful."
            : "Uses ZMODEM through the live SSH terminal (requires lrzsz on the server).";

        return new JsonArray(
            Tool("terminal_run",
                "Run a non-interactive command or short script on the remote server's interactive shell " +
                "and return captured output (exit code + stdout/stderr).",
                Prop("command", "string", "Remote command to execute."),
                ["command"]),
            Tool("terminal_run_danger",
                "Like terminal_run, but always asks the user to confirm first. Use for destructive " +
                "or hard-to-reverse work (rm -rf, DROP TABLE, force-push, disk wipe, …).",
                Prop("command", "string", "Remote command to execute after confirmation."),
                ["command"]),
            Tool("terminal_interrupt",
                "Send interrupt (Ctrl-C) and restore shell input on the remote terminal.",
                new JsonObject(),
                null),
            Tool("terminal_reconnect",
                "Rebuild the SSH/WSL channel when interrupt did not restore a usable shell.",
                new JsonObject(),
                null),
            Tool("file_upload",
                "Upload local Windows file(s) to a remote directory. " + transferHint,
                new JsonObject
                {
                    ["sources"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject { ["type"] = "string" },
                        ["description"] = "Local Windows file paths.",
                    },
                    ["destination"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Remote directory (optional; defaults to shell cwd).",
                    },
                },
                ["sources"]),
            Tool("file_download",
                "Download remote file(s) to a local Windows directory. " + transferHint,
                new JsonObject
                {
                    ["sources"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject { ["type"] = "string" },
                        ["description"] = "Remote file paths.",
                    },
                    ["destination"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Local Windows directory (optional; defaults to Downloads).",
                    },
                },
                ["sources"]));
    }

    private async Task<JsonObject> HandleToolCallAsync(JsonObject? parameters)
    {
        var name = parameters?["name"]?.GetValue<string>()
            ?? throw new ArgumentException("tools/call requires name");
        var args = parameters?["arguments"] as JsonObject ?? new JsonObject();

        var text = name switch
        {
            "terminal_run" => await RunCommandToolAsync(args, forceDanger: false).ConfigureAwait(false),
            "terminal_run_danger" => await RunCommandToolAsync(args, forceDanger: true).ConfigureAwait(false),
            "terminal_interrupt" => await _tools.RunTerminalActionAsync(AgentTerminalAction.ForceInterrupt)
                .ConfigureAwait(false),
            "terminal_reconnect" => await _tools.RunTerminalActionAsync(AgentTerminalAction.Reconnect)
                .ConfigureAwait(false),
            "file_upload" => await TransferToolAsync(args, isUpload: true).ConfigureAwait(false),
            "file_download" => await TransferToolAsync(args, isUpload: false).ConfigureAwait(false),
            _ => throw new ArgumentException($"Unknown tool: {name}"),
        };

        return ToolTextResult(text);
    }

    private async Task<string> RunCommandToolAsync(JsonObject args, bool forceDanger)
    {
        var command = args["command"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrEmpty(command))
            return "[error] command is required";

        if (RequiresDangerConfirmation(command, forceDanger))
        {
            var approved = await _tools.ConfirmDangerousCommandAsync(command).ConfigureAwait(false);
            if (!approved)
                return "[cancelled] user rejected the dangerous command";
        }

        return await _tools.RunCommandAsync(command).ConfigureAwait(false);
    }

    /// <summary>Returns the host-side safety decision without executing a command, for tests and Debug MCP.</summary>
    public bool RequiresDangerConfirmation(string command, bool dangerTagged) =>
        !AutoApproveDangerousCommands
        && (dangerTagged || DangerousCommandDetector.IsDangerous(command));

    private async Task<string> TransferToolAsync(JsonObject args, bool isUpload)
    {
        if (args["sources"] is not JsonArray sourcesNode || sourcesNode.Count == 0)
            return "[error] sources must be a non-empty array of paths";

        var sources = new System.Collections.Generic.List<string>();
        foreach (var item in sourcesNode)
        {
            var path = item?.GetValue<string>()?.Trim();
            if (!string.IsNullOrEmpty(path))
                sources.Add(path);
        }

        if (sources.Count == 0)
            return "[error] sources must be a non-empty array of paths";

        var destination = args["destination"]?.GetValue<string>();
        var transfer = new AgentFileTransfer(isUpload, sources, destination);
        return await _tools.TransferFilesAsync(transfer).ConfigureAwait(false);
    }

    private static JsonObject ToolTextResult(string text) =>
        new()
        {
            ["content"] = new JsonArray(
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = text,
                }),
            ["isError"] = text.StartsWith("[error]", StringComparison.OrdinalIgnoreCase)
                || text.StartsWith("[cancelled]", StringComparison.OrdinalIgnoreCase),
        };

    private static JsonObject Tool(string name, string description, JsonObject properties, string[]? required)
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
        };
        if (required is { Length: > 0 })
            schema["required"] = new JsonArray(Array.ConvertAll(required, r => (JsonNode)r));

        return new JsonObject
        {
            ["name"] = name,
            ["description"] = description,
            ["inputSchema"] = schema,
        };
    }

    private static JsonObject Prop(string name, string type, string description) =>
        new() { [name] = new JsonObject { ["type"] = type, ["description"] = description } };

    // Fix Prop usage - Tool expects properties object, not single Prop for first tools.
    // The two terminal_run tools pass Prop(...) which returns { command: {...} } - correct.

    private static JsonObject RpcResult(JsonNode? id, JsonNode result) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    private static JsonObject RpcError(JsonNode? id, int code, string message) =>
        new()
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
        };

    private static bool IsLoopback(IPAddress? address) =>
        address is not null && IPAddress.IsLoopback(address);

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
            return ValueTask.CompletedTask;

        try { _listener?.Stop(); } catch { /* ignore */ }
        try { _listener?.Close(); } catch { /* ignore */ }
        _listener = null;
        EndpointUrl = null;
        return ValueTask.CompletedTask;
    }
}
