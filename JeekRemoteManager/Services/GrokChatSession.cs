using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using JeekTools;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace JeekRemoteManager.Services;

/// <summary>
/// Long-lived multi-turn chat session backed by Grok Build's ACP (Agent Client Protocol)
/// server: <c>grok agent stdio</c>. JSON-RPC over stdio — handshake is
/// <c>initialize</c> → <c>session/new</c>; each user message is <c>session/prompt</c>,
/// answer text streams as <c>session/update</c> (<c>agent_message_chunk</c>), and the turn
/// finishes when the <c>session/prompt</c> response returns. Local tools run on the host
/// with <c>--always-approve</c>; remote server work still uses ```bash fences.
/// </summary>
public sealed class GrokChatSession : IAgentChatSession
{
    private static readonly ILogger Log = LogManager.CreateLogger(nameof(GrokChatSession));

    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(45);

    private static readonly JsonSerializerOptions WireOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _executablePath;
    private readonly string _workingDirectory;
    private readonly string? _rules;
    private readonly string? _model;
    private readonly string? _effort;
    private readonly string? _resumeSessionId;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly StringBuilder _currentText = new();
    private readonly TaskCompletionSource<string> _sessionReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Process? _process;
    private volatile bool _disposed;
    private bool _started;
    private int _nextRequestId;
    private int _initializeRequestId = -1;
    private int _sessionNewRequestId = -1;
    private int _promptRequestId = -1;
    private volatile bool _acceptPromptUpdates;
    private long _lastOutputTokens;
    private double _lastCostUsd;
    private int _numTurns;

    public GrokChatSession(
        string executablePath,
        string workingDirectory,
        string? rules = null,
        string? model = null,
        string? effort = null,
        string? resumeSessionId = null)
    {
        _executablePath = executablePath;
        _workingDirectory = workingDirectory;
        _rules = rules;
        _model = model;
        _effort = effort;
        _resumeSessionId = resumeSessionId;
    }

    /// <summary>ACP session id from <c>session/new</c>.</summary>
    public string? SessionId { get; private set; }

    public event Action<string>? SessionInitialized;
    public event Action<string>? TextDelta;
    public event Action<AgentTurnResult>? TurnCompleted;
    public event Action<string>? Errored;
    public event Action? Exited;

    private static Task<IReadOnlyList<AgentModelInfo>?>? _modelListCache;

    /// <summary>
    /// Loads Grok's model catalog. Prefers <c>~/.grok/models_cache.json</c>; falls back to a
    /// short-lived ACP <c>initialize</c> that advertises models in <c>_meta.modelState</c>.
    /// Cached process-wide; a failed probe is retried.
    /// </summary>
    public static Task<IReadOnlyList<AgentModelInfo>?> ListModelsCachedAsync(string executablePath)
    {
        var cache = _modelListCache;
        if (cache is null
            || cache.IsCanceled
            || cache.IsFaulted
            || (cache.IsCompletedSuccessfully && cache.Result is null))
        {
            _modelListCache = cache = ListModelsAsync(executablePath);
        }

        return cache;
    }

    private static async Task<IReadOnlyList<AgentModelInfo>?> ListModelsAsync(string executablePath)
    {
        var fromCache = TryReadModelsCache();
        if (fromCache is { Count: > 0 })
            return fromCache;

        return await ListModelsFromAcpAsync(executablePath).ConfigureAwait(false);
    }

    private static IReadOnlyList<AgentModelInfo>? TryReadModelsCache()
    {
        try
        {
            var grokHome = Environment.GetEnvironmentVariable("GROK_HOME");
            if (string.IsNullOrWhiteSpace(grokHome))
            {
                grokHome = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".grok");
            }

            var path = Path.Combine(grokHome, "models_cache.json");
            if (!File.Exists(path))
                return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("models", out var models)
                || models.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var list = new List<AgentModelInfo>();
            foreach (var prop in models.EnumerateObject())
            {
                if (!prop.Value.TryGetProperty("info", out var info))
                    continue;
                if (info.TryGetProperty("hidden", out var hidden) && hidden.ValueKind == JsonValueKind.True)
                    continue;

                var id = info.TryGetProperty("id", out var idProp) ? idProp.GetString() : prop.Name;
                if (string.IsNullOrWhiteSpace(id))
                    continue;

                var name = info.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
                var efforts = ParseReasoningEfforts(info);
                list.Add(new AgentModelInfo(
                    id,
                    string.IsNullOrWhiteSpace(name) ? id : name,
                    IsDefault: false,
                    efforts));
            }

            return MarkDefaultModel(list);
        }
        catch (Exception ex)
        {
            Log.ZLogDebug(ex, $"Failed to read Grok models cache");
            return null;
        }
    }

    private static async Task<IReadOnlyList<AgentModelInfo>?> ListModelsFromAcpAsync(string executablePath)
    {
        Process? process = null;
        try
        {
            process = new Process
            {
                StartInfo = CreateAgentStartInfo(executablePath, null, null, null),
            };
            process.Start();

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var ct = timeout.Token;

            async Task WriteAsync(object message)
            {
                var line = JsonSerializer.Serialize(message, WireOptions);
                await process.StandardInput.WriteAsync(line.AsMemory(), ct).ConfigureAwait(false);
                await process.StandardInput.WriteAsync("\n".AsMemory(), ct).ConfigureAwait(false);
                await process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
            }

            await WriteAsync(new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "initialize",
                @params = new
                {
                    protocolVersion = 1,
                    clientCapabilities = new { },
                    clientInfo = new
                    {
                        name = "jeek-remote-manager",
                        title = "JeekRemoteManager",
                        version = "1.0",
                    },
                },
            }).ConfigureAwait(false);

            while (await process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0)
                    continue;

                JsonDocument doc;
                try
                {
                    doc = JsonDocument.Parse(line);
                }
                catch
                {
                    continue;
                }

                using (doc)
                {
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("id", out var idProp)
                        || !idProp.TryGetInt32(out var id)
                        || id != 1
                        || !root.TryGetProperty("result", out var result))
                    {
                        continue;
                    }

                    return ParseModelsFromInitialize(result);
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            Log.ZLogDebug(ex, $"Failed to list Grok models via ACP initialize");
            return null;
        }
        finally
        {
            if (process is not null)
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // best-effort
                }

                process.Dispose();
            }
        }
    }

    private static IReadOnlyList<AgentModelInfo>? ParseModelsFromInitialize(JsonElement result)
    {
        // Prefer _meta.modelState.availableModels from Grok's initialize response.
        if (!result.TryGetProperty("_meta", out var meta)
            || !meta.TryGetProperty("modelState", out var modelState)
            || !modelState.TryGetProperty("availableModels", out var available)
            || available.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var currentId = modelState.TryGetProperty("currentModelId", out var cur)
            ? cur.GetString()
            : null;

        var list = new List<AgentModelInfo>();
        foreach (var model in available.EnumerateArray())
        {
            var id = model.TryGetProperty("modelId", out var idProp) ? idProp.GetString() : null;
            if (string.IsNullOrWhiteSpace(id))
                continue;

            var name = model.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
            var efforts = model.TryGetProperty("_meta", out var mMeta)
                ? ParseReasoningEfforts(mMeta)
                : [];
            var isDefault = !string.IsNullOrEmpty(currentId) && id == currentId;
            list.Add(new AgentModelInfo(
                id,
                string.IsNullOrWhiteSpace(name) ? id : name,
                isDefault,
                efforts));
        }

        if (list.Count == 0)
            return null;
        if (!list.Exists(m => m.IsDefault))
            list[0] = list[0] with { IsDefault = true };
        return list;
    }

    private static List<string> ParseReasoningEfforts(JsonElement container)
    {
        var efforts = new List<string>();
        if (!container.TryGetProperty("reasoningEfforts", out var effortsEl)
            && !container.TryGetProperty("reasoning_efforts", out effortsEl))
        {
            return efforts;
        }

        if (effortsEl.ValueKind != JsonValueKind.Array)
            return efforts;

        foreach (var effort in effortsEl.EnumerateArray())
        {
            var value = effort.TryGetProperty("value", out var v)
                ? v.GetString()
                : effort.TryGetProperty("id", out var i) ? i.GetString() : null;
            if (!string.IsNullOrWhiteSpace(value))
                efforts.Add(value);
        }

        return efforts;
    }

    private static IReadOnlyList<AgentModelInfo>? MarkDefaultModel(List<AgentModelInfo> list)
    {
        if (list.Count == 0)
            return null;

        var defaultIndex = list.FindIndex(m => m.Id is "grok-4.5" or "grok-build");
        if (defaultIndex < 0)
            defaultIndex = 0;
        list[defaultIndex] = list[defaultIndex] with { IsDefault = true };
        return list;
    }

    private static ProcessStartInfo CreateAgentStartInfo(
        string executablePath,
        string? workingDirectory,
        string? model,
        string? effort)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executablePath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };

        psi.Environment["GROK_DISABLE_AUTOUPDATER"] = "1";

        // Options belong on `grok agent`, before the transport mode name.
        psi.ArgumentList.Add("agent");
        psi.ArgumentList.Add("--always-approve");
        psi.ArgumentList.Add("--no-leader");
        if (!string.IsNullOrWhiteSpace(model))
        {
            psi.ArgumentList.Add("--model");
            psi.ArgumentList.Add(model);
        }

        if (!string.IsNullOrWhiteSpace(effort))
        {
            psi.ArgumentList.Add("--reasoning-effort");
            psi.ArgumentList.Add(effort);
        }

        psi.ArgumentList.Add("stdio");

        if (workingDirectory is not null)
            psi.WorkingDirectory = workingDirectory;

        return psi;
    }

    public void Start()
    {
        if (_started)
            return;
        _started = true;

        Directory.CreateDirectory(_workingDirectory);

        var psi = CreateAgentStartInfo(_executablePath, _workingDirectory, _model, _effort);
        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.Exited += (_, _) =>
        {
            _sessionReady.TrySetException(new InvalidOperationException("The Grok CLI exited."));
            if (!_disposed)
                Exited?.Invoke();
        };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            Errored?.Invoke($"Failed to start Grok CLI: {ex.Message}");
            _sessionReady.TrySetException(ex);
            return;
        }

        _process = process;
        _ = Task.Run(() => ReadStdoutLoopAsync(process));
        _ = Task.Run(() => ReadStderrLoopAsync(process));
        _ = Task.Run(SendInitializeAsync);
    }

    public async Task SendAsync(string text, CancellationToken cancellationToken = default)
    {
        var process = _process;
        if (_disposed || process is null || process.HasExited)
            throw new InvalidOperationException("The Grok CLI session is not running.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(HandshakeTimeout);
        var sessionId = await _sessionReady.Task.WaitAsync(timeout.Token).ConfigureAwait(false);

        _currentText.Clear();
        _lastOutputTokens = 0;
        _lastCostUsd = 0;

        _acceptPromptUpdates = true;
        try
        {
            _promptRequestId = await SendRequestAsync("session/prompt", new
            {
                sessionId,
                prompt = new[] { new { type = "text", text } },
            }, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _acceptPromptUpdates = false;
            throw;
        }
    }

    private async Task SendInitializeAsync()
    {
        try
        {
            _initializeRequestId = await SendRequestAsync("initialize", new
            {
                protocolVersion = 1,
                // Do not advertise fs/terminal client capabilities: we would then have to
                // implement those methods. Grok uses its own built-in tools instead.
                clientCapabilities = new { },
                clientInfo = new
                {
                    name = "jeek-remote-manager",
                    title = "JeekRemoteManager",
                    version = "1.0",
                },
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _sessionReady.TrySetException(ex);
            if (!_disposed)
                Errored?.Invoke($"Grok handshake failed: {ex.Message}");
        }
    }

    private async Task<int> SendRequestAsync(string method, object? @params, CancellationToken cancellationToken = default)
    {
        var id = Interlocked.Increment(ref _nextRequestId);
        await WriteMessageAsync(new { jsonrpc = "2.0", id, method, @params }, cancellationToken).ConfigureAwait(false);
        return id;
    }

    private Task SendResponseAsync(JsonElement id, object result) =>
        WriteMessageAsync(new { jsonrpc = "2.0", id, result });

    private async Task WriteMessageAsync(object message, CancellationToken cancellationToken = default)
    {
        var process = _process ?? throw new InvalidOperationException("The Grok CLI session is not running.");
        var line = JsonSerializer.Serialize(message, WireOptions);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await process.StandardInput.WriteAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await process.StandardInput.WriteAsync("\n".AsMemory(), cancellationToken).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadStdoutLoopAsync(Process process)
    {
        try
        {
            var reader = process.StandardOutput;
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (_disposed)
                    return;
                if (line.Length == 0)
                    continue;
                try
                {
                    await HandleLineAsync(line).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.ZLogDebug(ex, $"Malformed Grok ACP line");
                }
            }
        }
        catch (Exception ex)
        {
            if (!_disposed)
                Errored?.Invoke($"Reading Grok CLI output failed: {ex.Message}");
        }
    }

    private async Task ReadStderrLoopAsync(Process process)
    {
        try
        {
            var reader = process.StandardError;
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (_disposed)
                    return;
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                // Diagnostics only — the chat panel treats Errored as a failed turn.
                Log.ZLogDebug($"Grok stderr: {line}");
            }
        }
        catch
        {
            // stderr drain is best-effort.
        }
    }

    private async Task HandleLineAsync(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return;

        var hasId = root.TryGetProperty("id", out var idProp);
        var hasMethod = root.TryGetProperty("method", out var methodProp);

        if (hasMethod && hasId)
        {
            await HandleServerRequestAsync(idProp, methodProp.GetString() ?? "", root).ConfigureAwait(false);
        }
        else if (hasMethod)
        {
            HandleNotification(methodProp.GetString() ?? "", root);
        }
        else if (hasId)
        {
            await HandleResponseAsync(idProp, root).ConfigureAwait(false);
        }
    }

    private async Task HandleResponseAsync(JsonElement idProp, JsonElement root)
    {
        if (idProp.ValueKind != JsonValueKind.Number || !idProp.TryGetInt32(out var id))
            return;

        if (root.TryGetProperty("error", out var error))
        {
            var message = error.TryGetProperty("message", out var msg)
                ? msg.GetString() ?? "unknown error"
                : "unknown error";
            if (id == _initializeRequestId || id == _sessionNewRequestId)
                _sessionReady.TrySetException(new InvalidOperationException(message));
            if (id == _promptRequestId)
            {
                TurnCompleted?.Invoke(new AgentTurnResult(message, 0, 0, _numTurns, IsError: true));
                _promptRequestId = -1;
                _acceptPromptUpdates = false;
            }
            else if (!_disposed)
            {
                Errored?.Invoke($"Grok error: {message}");
            }

            return;
        }

        if (!root.TryGetProperty("result", out var result))
            return;

        if (id == _initializeRequestId)
        {
            var sessionParams = new Dictionary<string, object?>
            {
                ["cwd"] = _workingDirectory,
                ["mcpServers"] = Array.Empty<object>(),
            };
            if (!string.IsNullOrWhiteSpace(_rules))
                sessionParams["_meta"] = new { rules = _rules };
            if (!string.IsNullOrWhiteSpace(_resumeSessionId))
                sessionParams["sessionId"] = _resumeSessionId;

            _sessionNewRequestId = string.IsNullOrWhiteSpace(_resumeSessionId)
                ? await SendRequestAsync("session/new", sessionParams).ConfigureAwait(false)
                : await SendRequestAsync("session/load", sessionParams).ConfigureAwait(false);
        }
        else if (id == _sessionNewRequestId)
        {
            var sid = result.TryGetProperty("sessionId", out var sidProp)
                ? sidProp.GetString()
                : _resumeSessionId;
            if (!string.IsNullOrWhiteSpace(sid))
            {
                SessionId = sid;
                SessionInitialized?.Invoke(sid);
                _sessionReady.TrySetResult(sid);
            }
            else
            {
                _sessionReady.TrySetException(
                    new InvalidOperationException("Grok session/new returned no sessionId."));
            }
        }
        else if (id == _promptRequestId)
        {
            HandlePromptCompleted(result);
            _promptRequestId = -1;
            _acceptPromptUpdates = false;
        }
    }

    private void HandlePromptCompleted(JsonElement result)
    {
        var stopReason = result.TryGetProperty("stopReason", out var sr) ? sr.GetString() : null;
        var isError = stopReason is "refusal" or "max_tokens" or "max_turn_requests";
        // cancelled is not treated as a hard error bubble; the panel already tracks cancel.

        if (result.TryGetProperty("_meta", out var meta))
        {
            if (meta.TryGetProperty("outputTokens", out var ot) && ot.TryGetInt64(out var tokens))
                _lastOutputTokens = tokens;
            else if (meta.TryGetProperty("totalTokens", out var tt) && tt.TryGetInt64(out var total))
                _lastOutputTokens = total;

            if (meta.TryGetProperty("cost", out var cost)
                && cost.ValueKind == JsonValueKind.Object
                && cost.TryGetProperty("amount", out var amount)
                && amount.TryGetDouble(out var usd))
            {
                _lastCostUsd = usd;
            }
        }

        var text = _currentText.ToString();
        TurnCompleted?.Invoke(new AgentTurnResult(
            text,
            _lastCostUsd,
            _lastOutputTokens,
            ++_numTurns,
            isError && stopReason != "cancelled"));
    }

    private Task HandleServerRequestAsync(JsonElement id, string method, JsonElement root)
    {
        // Auto-approve tool permission prompts (we also pass --always-approve, but still
        // answer if the agent asks — keeps headless use unattended).
        if (method is "session/request_permission")
        {
            var optionId = PickAllowOptionId(root);
            return SendResponseAsync(id.Clone(), new
            {
                outcome = new
                {
                    outcome = "selected",
                    optionId,
                },
            });
        }

        // Unknown server→client requests: reject so the agent can continue without hanging.
        Log.ZLogDebug($"Unsupported Grok ACP request: {method}");
        return WriteMessageAsync(new
        {
            jsonrpc = "2.0",
            id = id.Clone(),
            error = new { code = -32601, message = $"unsupported request: {method}" },
        });
    }

    private static string PickAllowOptionId(JsonElement root)
    {
        if (!root.TryGetProperty("params", out var p)
            || !p.TryGetProperty("options", out var options)
            || options.ValueKind != JsonValueKind.Array)
        {
            return "allow-once";
        }

        string? allowAlways = null;
        string? allowOnce = null;
        string? first = null;
        foreach (var opt in options.EnumerateArray())
        {
            var id = opt.TryGetProperty("optionId", out var idProp) ? idProp.GetString() : null;
            if (string.IsNullOrEmpty(id))
                continue;
            first ??= id;
            var kind = opt.TryGetProperty("kind", out var kindProp) ? kindProp.GetString() : null;
            if (kind is "allow_always" || id.Contains("always", StringComparison.OrdinalIgnoreCase))
                allowAlways ??= id;
            else if (kind is "allow_once" || id.Contains("allow", StringComparison.OrdinalIgnoreCase))
                allowOnce ??= id;
        }

        return allowAlways ?? allowOnce ?? first ?? "allow-once";
    }

    private void HandleNotification(string method, JsonElement root)
    {
        if (!root.TryGetProperty("params", out var p))
            return;

        switch (method)
        {
            case "session/update":
                HandleSessionUpdate(p);
                break;

            // Grok-specific chatter (_x.ai/*): ignore for the chat surface.
            default:
                if (!method.StartsWith("_x.ai/", StringComparison.Ordinal)
                    && !method.StartsWith("x.ai/", StringComparison.Ordinal))
                {
                    Log.ZLogDebug($"Grok ACP notification: {method}");
                }
                break;
        }
    }

    private void HandleSessionUpdate(JsonElement p)
    {
        // session/load replays the saved transcript as ordinary session/update events.
        // The UI already restores that transcript from AiConversationStore, so only forward
        // updates belonging to a newly submitted prompt.
        if (!_acceptPromptUpdates)
            return;

        if (!p.TryGetProperty("update", out var update)
            || !update.TryGetProperty("sessionUpdate", out var kindProp))
        {
            return;
        }

        var kind = kindProp.GetString();
        switch (kind)
        {
            case "agent_message_chunk":
                if (update.TryGetProperty("content", out var content)
                    && content.TryGetProperty("text", out var textProp)
                    && textProp.GetString() is { Length: > 0 } text)
                {
                    _currentText.Append(text);
                    TextDelta?.Invoke(text);
                }
                break;

            case "agent_thought_chunk":
                // Internal reasoning; chat panel only shows answer text.
                break;

            case "usage_update":
                if (update.TryGetProperty("used", out var used) && used.TryGetInt64(out var usedTokens))
                    _lastOutputTokens = usedTokens;
                if (update.TryGetProperty("cost", out var cost)
                    && cost.ValueKind == JsonValueKind.Object
                    && cost.TryGetProperty("amount", out var amount)
                    && amount.TryGetDouble(out var usd))
                {
                    _lastCostUsd = usd;
                }
                break;

            // tool_call, tool_call_update, plan, available_commands_update — ignored for now.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        _sessionReady.TrySetCanceled();

        var process = _process;
        if (process is null)
            return;

        try
        {
            if (!process.HasExited)
            {
                // Best-effort cancel of an in-flight prompt before killing the process.
                if (SessionId is not null && _promptRequestId >= 0)
                {
                    try
                    {
                        await WriteMessageAsync(new
                        {
                            jsonrpc = "2.0",
                            method = "session/cancel",
                            @params = new { sessionId = SessionId },
                        }).ConfigureAwait(false);
                    }
                    catch
                    {
                        // ignore
                    }
                }

                try { process.StandardInput.Close(); } catch { /* ignore */ }
                if (!process.WaitForExit(1500))
                    process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best-effort teardown.
        }
        finally
        {
            process.Dispose();
            _process = null;
        }

        _writeLock.Dispose();
    }
}
