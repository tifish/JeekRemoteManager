using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using JeekTools;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace JeekRemoteManager.Services;

/// <summary>
/// Wraps a long-lived <c>codex app-server</c> subprocess (JSON-RPC over stdio, one message
/// per line) as a single multi-turn chat session. The handshake is
/// initialize → initialized → thread/start; each user message becomes a turn/start request,
/// and the answer streams back as item/agentMessage/delta notifications followed by
/// turn/completed. The thread runs with full local access and approvals disabled: Codex's
/// own tools act on the local Windows machine, while commands for the remote server flow
/// through the chat harness.
/// </summary>
public sealed class CodexChatSession : IAgentChatSession
{
    private static readonly ILogger Log = LogManager.CreateLogger(nameof(CodexChatSession));

    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(30);

    private static readonly Regex AnsiEscapeRegex = new("\x1b\\[[0-9;]*m", RegexOptions.Compiled);

    // Matches the tracing lines codex writes to stderr, e.g.
    // "2026-07-06T15:27:58.838173Z ERROR codex_core::tools::router: ..." (after ANSI stripping).
    private static readonly Regex TracingLineRegex = new(
        @"^\d{4}-\d{2}-\d{2}T\S+ +(TRACE|DEBUG|INFO|WARN|ERROR)\b",
        RegexOptions.Compiled);

    private static bool IsTracingLine(string text)
    {
        if (TracingLineRegex.IsMatch(text))
            return true;

        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object
                   && root.TryGetProperty("timestamp", out var timestamp)
                   && timestamp.ValueKind == JsonValueKind.String
                   && root.TryGetProperty("level", out var level)
                   && level.ValueKind == JsonValueKind.String
                   && level.GetString() is "TRACE" or "DEBUG" or "INFO" or "WARN" or "ERROR"
                   && root.TryGetProperty("fields", out var fields)
                   && fields.ValueKind == JsonValueKind.Object
                   && root.TryGetProperty("target", out var target)
                   && target.ValueKind == JsonValueKind.String;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static readonly JsonSerializerOptions WireOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _executablePath;
    private readonly string _workingDirectory;
    private readonly string? _developerInstructions;
    private readonly string? _model;
    private readonly string? _effort;
    private readonly string? _resumeThreadId;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly StringBuilder _currentText = new();
    private readonly TaskCompletionSource<string> _threadReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Process? _process;
    private volatile bool _disposed;
    private bool _started;
    private int _nextRequestId;
    private int _initializeRequestId = -1;
    private int _threadStartRequestId = -1;
    private int _turnStartRequestId = -1;
    private int _steerRequestId = -1;
    private volatile string? _currentTurnId;
    private TaskCompletionSource<string>? _activeTurnReady;
    private TaskCompletionSource<bool>? _steerResponse;
    private string? _activeAgentMessageItemId;
    private int _activeAgentMessageStart = -1;
    private readonly Dictionary<string, string> _terminalTurnErrors = new(StringComparer.Ordinal);
    private long _lastOutputTokens;
    private int _numTurns;

    public CodexChatSession(
        string executablePath,
        string workingDirectory,
        string? developerInstructions = null,
        string? model = null,
        string? effort = null,
        string? resumeThreadId = null)
    {
        _executablePath = executablePath;
        _workingDirectory = workingDirectory;
        _developerInstructions = developerInstructions;
        _model = model;
        _effort = effort;
        _resumeThreadId = resumeThreadId;
    }

    /// <summary>The Codex thread id reported by the <c>thread/start</c> response.</summary>
    public string? SessionId { get; private set; }

    public bool SupportsSteering => true;

    public event Action<string>? SessionInitialized;
    public event Action<string>? TextDelta;
    public event Action<string>? TextReplaced;
    public event Action<AgentTurnResult>? TurnCompleted;
    public event Action<string>? Errored;
    public event Action? Exited;

    private static Task<IReadOnlyList<AgentModelInfo>?>? _modelListCache;

    /// <summary>
    /// Queries the models Codex advertises (id, display name, supported reasoning efforts)
    /// by driving a short-lived <c>codex app-server</c> through initialize → model/list.
    /// The result is cached process-wide (a failed probe is retried on the next call);
    /// returns <c>null</c> on any failure so callers can keep their static fallback.
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
        using var process = new Process { StartInfo = CreateAppServerStartInfo(executablePath, null) };
        try
        {
            process.Start();

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
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
                @params = new { clientInfo = new { name = "jeek-remote-manager", version = "1.0" } },
            }).ConfigureAwait(false);

            var listRequested = false;
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
                    if (root.ValueKind != JsonValueKind.Object
                        || !root.TryGetProperty("id", out var idProp)
                        || !idProp.TryGetInt32(out var id)
                        || !root.TryGetProperty("result", out var result))
                    {
                        continue;
                    }

                    if (id == 1 && !listRequested)
                    {
                        listRequested = true;
                        await WriteAsync(new { jsonrpc = "2.0", method = "initialized" }).ConfigureAwait(false);
                        await WriteAsync(new { jsonrpc = "2.0", id = 2, method = "model/list", @params = new { } }).ConfigureAwait(false);
                    }
                    else if (id == 2)
                    {
                        return ParseModelList(result);
                    }
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best-effort teardown.
            }
        }
    }

    private static IReadOnlyList<AgentModelInfo>? ParseModelList(JsonElement result)
    {
        if (!result.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return null;

        var models = new List<AgentModelInfo>();
        foreach (var model in data.EnumerateArray())
        {
            if (!model.TryGetProperty("id", out var idProp) || idProp.GetString() is not { Length: > 0 } id)
                continue;
            if (model.TryGetProperty("hidden", out var hidden) && hidden.ValueKind == JsonValueKind.True)
                continue;

            var displayName = model.TryGetProperty("displayName", out var dn) ? dn.GetString() : null;
            var isDefault = model.TryGetProperty("isDefault", out var def) && def.ValueKind == JsonValueKind.True;

            var efforts = new List<string>();
            if (model.TryGetProperty("supportedReasoningEfforts", out var supported)
                && supported.ValueKind == JsonValueKind.Array)
            {
                foreach (var effort in supported.EnumerateArray())
                {
                    if (effort.TryGetProperty("reasoningEffort", out var re)
                        && re.GetString() is { Length: > 0 } value)
                    {
                        efforts.Add(value);
                    }
                }
            }

            models.Add(new AgentModelInfo(id, string.IsNullOrWhiteSpace(displayName) ? id : displayName, isDefault, efforts));
        }

        return models.Count > 0 ? models : null;
    }

    private static ProcessStartInfo CreateAppServerStartInfo(string executablePath, string? workingDirectory)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executablePath,
            ArgumentList = { "app-server" },
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
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

        var psi = CreateAppServerStartInfo(_executablePath, _workingDirectory);

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.Exited += (_, _) =>
        {
            _threadReady.TrySetException(new InvalidOperationException("The Codex CLI exited."));
            if (!_disposed)
                Exited?.Invoke();
        };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            Errored?.Invoke($"Failed to start Codex CLI: {ex.Message}");
            _threadReady.TrySetException(ex);
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
            throw new InvalidOperationException("The Codex CLI session is not running.");

        _currentTurnId = null;
        _activeTurnReady = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        // The first send can race the initialize → thread/start handshake; wait for the
        // thread id rather than failing.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(HandshakeTimeout);
        var threadId = await _threadReady.Task.WaitAsync(timeout.Token).ConfigureAwait(false);

        _currentText.Clear();
        _activeAgentMessageItemId = null;
        _activeAgentMessageStart = -1;
        _lastOutputTokens = 0;

        _turnStartRequestId = await SendRequestAsync("turn/start", new
        {
            threadId,
            input = new[] { new { type = "text", text } },
            effort = string.IsNullOrWhiteSpace(_effort) ? null : _effort,
        }, cancellationToken, id => _turnStartRequestId = id).ConfigureAwait(false);
    }

    /// <summary>Appends input to the active Codex turn. The turn id may arrive just after
    /// <c>turn/start</c>, so an immediate steer waits briefly for that response.</summary>
    public async Task SteerAsync(string text, CancellationToken cancellationToken = default)
    {
        var process = _process;
        if (_disposed || process is null || process.HasExited)
            throw new InvalidOperationException("The Codex CLI session is not running.");

        var activeTurnReady = _activeTurnReady
            ?? throw new InvalidOperationException("There is no active Codex turn to steer.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(HandshakeTimeout);
        var threadId = SessionId
            ?? await _threadReady.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        var turnId = _currentTurnId
            ?? await activeTurnReady.Task.WaitAsync(timeout.Token).ConfigureAwait(false);

        if (!ReferenceEquals(activeTurnReady, _activeTurnReady) || _currentTurnId != turnId)
            throw new InvalidOperationException("The active Codex turn finished before it could be steered.");

        var steerResponse = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _steerResponse = steerResponse;
        _steerRequestId = await SendRequestAsync("turn/steer", new
        {
            threadId,
            expectedTurnId = turnId,
            input = new[] { new { type = "text", text } },
        }, cancellationToken, id => _steerRequestId = id).ConfigureAwait(false);
        await steerResponse.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
    }

    /// <summary>Asks the app-server to abort the in-flight turn; it still ends with a
    /// normal turn/completed (status=interrupted). No-op when no turn id is known yet.</summary>
    public async Task InterruptAsync(CancellationToken cancellationToken = default)
    {
        var process = _process;
        if (_disposed || process is null || process.HasExited)
            return;

        var threadId = SessionId;
        var turnId = _currentTurnId;
        if (threadId is null || turnId is null)
            return;

        await SendRequestAsync("turn/interrupt", new { threadId, turnId }, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendInitializeAsync()
    {
        try
        {
            _initializeRequestId = await SendRequestAsync("initialize", new
            {
                clientInfo = new { name = "jeek-remote-manager", title = "JeekRemoteManager", version = "1.0" },
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _threadReady.TrySetException(ex);
            if (!_disposed)
                Errored?.Invoke($"Codex handshake failed: {ex.Message}");
        }
    }

    private async Task<int> SendRequestAsync(
        string method,
        object? @params,
        CancellationToken cancellationToken = default,
        Action<int>? requestIdAssigned = null)
    {
        var id = Interlocked.Increment(ref _nextRequestId);
        requestIdAssigned?.Invoke(id);
        await WriteMessageAsync(new { jsonrpc = "2.0", id, method, @params }, cancellationToken).ConfigureAwait(false);
        return id;
    }

    private Task SendNotificationAsync(string method) =>
        WriteMessageAsync(new { jsonrpc = "2.0", method });

    private Task SendResponseAsync(JsonElement id, object result) =>
        WriteMessageAsync(new { jsonrpc = "2.0", id, result });

    private async Task WriteMessageAsync(object message, CancellationToken cancellationToken = default)
    {
        var process = _process ?? throw new InvalidOperationException("The Codex CLI session is not running.");
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
                catch
                {
                    // A single malformed line must not kill the read loop.
                }
            }
        }
        catch (Exception ex)
        {
            if (!_disposed)
                Errored?.Invoke($"Reading Codex CLI output failed: {ex.Message}");
        }
    }

    private async Task ReadStderrLoopAsync(Process process)
    {
        try
        {
            var reader = process.StandardError;
            // codex app-server writes its internal tracing logs (with ANSI colors) to stderr.
            // Those are diagnostics, not turn failures — real errors arrive as JSON-RPC error
            // notifications or turn/completed with status=failed — so route them (and their
            // multi-line continuations) to the app log instead of the Errored event, which
            // the chat panel treats as a failed turn.
            var sawTracing = false;
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (_disposed)
                    return;
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                var text = AnsiEscapeRegex.Replace(line, "");
                if (sawTracing || IsTracingLine(text))
                {
                    sawTracing = true;
                    Log.ZLogInformation($"codex stderr: {text}");
                }
                else
                {
                    Log.ZLogWarning($"codex stderr: {text}");
                    Errored?.Invoke(text);
                }
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
            await HandleServerRequestAsync(idProp, methodProp.GetString() ?? "").ConfigureAwait(false);
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
            var message = error.TryGetProperty("message", out var msg) ? msg.GetString() ?? "unknown error" : "unknown error";
            if (id == _steerRequestId && Interlocked.Exchange(ref _steerResponse, null) is { } steerResponse)
            {
                Interlocked.Exchange(ref _steerRequestId, -1);
                steerResponse.TrySetException(new InvalidOperationException(message));
                return;
            }
            if (id == _initializeRequestId || id == _threadStartRequestId)
                _threadReady.TrySetException(new InvalidOperationException(message));
            if (id == _turnStartRequestId)
                _activeTurnReady?.TrySetException(new InvalidOperationException(message));
            if (!_disposed)
                Errored?.Invoke($"Codex error: {message}");
            return;
        }

        if (!root.TryGetProperty("result", out var result))
            return;

        if (id == _steerRequestId && Interlocked.Exchange(ref _steerResponse, null) is { } steerResponseResult)
        {
            Interlocked.Exchange(ref _steerRequestId, -1);
            steerResponseResult.TrySetResult(true);
            return;
        }

        if (id == _initializeRequestId)
        {
            await SendNotificationAsync("initialized").ConfigureAwait(false);
            _threadStartRequestId = string.IsNullOrWhiteSpace(_resumeThreadId)
                ? await SendRequestAsync("thread/start", new
                {
                    cwd = _workingDirectory,
                    sandbox = "danger-full-access",
                    approvalPolicy = "never",
                    model = string.IsNullOrWhiteSpace(_model) ? null : _model,
                    developerInstructions = string.IsNullOrWhiteSpace(_developerInstructions) ? null : _developerInstructions,
                }).ConfigureAwait(false)
                : await SendRequestAsync("thread/resume", new
                {
                    threadId = _resumeThreadId,
                    cwd = _workingDirectory,
                    sandbox = "danger-full-access",
                    approvalPolicy = "never",
                    model = string.IsNullOrWhiteSpace(_model) ? null : _model,
                    developerInstructions = string.IsNullOrWhiteSpace(_developerInstructions) ? null : _developerInstructions,
                }).ConfigureAwait(false);
        }
        else if (id == _threadStartRequestId)
        {
            if (result.TryGetProperty("thread", out var thread)
                && thread.TryGetProperty("id", out var threadId)
                && threadId.GetString() is { } tid)
            {
                SessionId = tid;
                SessionInitialized?.Invoke(tid);
                _threadReady.TrySetResult(tid);
            }
            else
            {
                _threadReady.TrySetException(new InvalidOperationException("Codex thread/start returned no thread id."));
            }
        }
        else if (id == _turnStartRequestId)
        {
            // The turn/start response returns the created turn immediately; its id is what
            // turn/interrupt needs.
            if (result.TryGetProperty("turn", out var turnObj)
                && turnObj.TryGetProperty("id", out var turnIdProp)
                && turnIdProp.GetString() is { } turnId)
            {
                _currentTurnId = turnId;
                // The response can precede actual activation by several seconds while MCP
                // servers start. Only turn/started below releases same-turn steering.
            }
            else
                _activeTurnReady?.TrySetException(new InvalidOperationException("Codex turn/start returned no turn id."));
        }
    }

    // With sandbox=danger-full-access and approvalPolicy=never these should never arrive,
    // but if one does, approve it — the session runs unrestricted by design; an unanswered
    // request would hang the turn.
    private Task HandleServerRequestAsync(JsonElement id, string method) => method switch
    {
        "item/commandExecution/requestApproval" or "item/fileChange/requestApproval"
            or "item/permissions/requestApproval"
            => SendResponseAsync(id.Clone(), new { decision = "accept" }),
        "execCommandApproval" or "applyPatchApproval"
            => SendResponseAsync(id.Clone(), new { decision = "approved" }),
        _ => WriteMessageAsync(new
        {
            jsonrpc = "2.0",
            id = id.Clone(),
            error = new { code = -32601, message = $"unsupported request: {method}" },
        }),
    };

    private void HandleNotification(string method, JsonElement root)
    {
        if (!root.TryGetProperty("params", out var p))
            return;

        switch (method)
        {
            case "item/started":
                if (p.TryGetProperty("item", out var startedItem)
                    && startedItem.TryGetProperty("type", out var startedItemType)
                    && startedItemType.GetString() == "agentMessage"
                    && startedItem.TryGetProperty("id", out var startedItemId))
                {
                    BeginAgentMessageItem(startedItemId.GetString());
                }
                break;

            case "item/agentMessage/delta":
                if (p.TryGetProperty("delta", out var delta) && delta.GetString() is { Length: > 0 } text)
                {
                    var itemId = p.TryGetProperty("itemId", out var deltaItemId)
                        ? deltaItemId.GetString()
                        : null;
                    EnsureAgentMessageContentStarted(itemId);
                    _currentText.Append(text);
                    TextDelta?.Invoke(text);
                }
                break;

            case "item/completed":
                if (p.TryGetProperty("item", out var item)
                    && item.TryGetProperty("type", out var itemType)
                    && itemType.GetString() == "agentMessage"
                    && item.TryGetProperty("text", out var itemText))
                {
                    var completedText = itemText.GetString() ?? "";
                    // Empty items carry no content to add. In particular, an empty final
                    // item must not erase the commentary and tool items already emitted.
                    if (completedText.Length == 0)
                        break;

                    var itemId = item.TryGetProperty("id", out var completedItemId)
                        ? completedItemId.GetString()
                        : null;
                    EnsureAgentMessageContentStarted(itemId);

                    // item/completed is authoritative for this item only. Correct its
                    // streamed slice while retaining every earlier non-empty item in the
                    // turn (commentary, tool requests, and final answer alike).
                    _currentText.Length = _activeAgentMessageStart;
                    _currentText.Append(completedText);
                    TextReplaced?.Invoke(_currentText.ToString());
                }
                break;

            case "thread/tokenUsage/updated":
                if (p.TryGetProperty("tokenUsage", out var usage)
                    && usage.TryGetProperty("last", out var last)
                    && last.TryGetProperty("outputTokens", out var ot)
                    && ot.TryGetInt64(out var tokens))
                {
                    _lastOutputTokens = tokens;
                }
                break;

            case "turn/started":
                // Fallback for the turn id (also taken from the turn/start response).
                if (p.TryGetProperty("turn", out var startedTurn)
                    && startedTurn.TryGetProperty("id", out var startedTurnId)
                    && startedTurnId.GetString() is { } newTurnId)
                {
                    _currentTurnId = newTurnId;
                    _activeTurnReady?.TrySetResult(newTurnId);
                }
                break;

            case "turn/completed":
                var completedTurnId = p.TryGetProperty("turn", out var completedTurn)
                    && completedTurn.TryGetProperty("id", out var completedTurnIdProperty)
                        ? completedTurnIdProperty.GetString()
                        : null;
                if (completedTurnId is null || completedTurnId == _currentTurnId)
                {
                    _currentTurnId = null;
                    _activeTurnReady = null;
                }
                HandleTurnCompleted(p, completedTurnId);
                break;

            case "error":
                // Error notifications are scoped to a turn and are followed by
                // turn/completed. Ending the UI turn here races that authoritative event
                // and makes the panel appear to stop while Codex is still winding down.
                // Retain the terminal message as a fallback for the matching completion.
                if (p.TryGetProperty("willRetry", out var retry)
                    && retry.ValueKind != JsonValueKind.True
                    && p.TryGetProperty("error", out var err)
                    && err.TryGetProperty("message", out var errMsg)
                    && errMsg.GetString() is { Length: > 0 } terminalError)
                {
                    if (p.TryGetProperty("turnId", out var errorTurnId)
                        && errorTurnId.GetString() is { Length: > 0 } turnId)
                    {
                        _terminalTurnErrors[turnId] = terminalError;
                    }
                    else
                    {
                        // Older or malformed protocol output cannot be reconciled with a
                        // later completion, so keep the existing immediate-failure path.
                        Errored?.Invoke(terminalError);
                    }
                }
                break;
        }
    }

    private void BeginAgentMessageItem(string? itemId)
    {
        if (string.IsNullOrEmpty(itemId) || itemId == _activeAgentMessageItemId)
            return;

        _activeAgentMessageItemId = itemId;
        _activeAgentMessageStart = -1;
    }

    private void EnsureAgentMessageContentStarted(string? itemId)
    {
        BeginAgentMessageItem(itemId);
        if (_activeAgentMessageStart >= 0)
            return;

        if (_currentText.Length > 0)
        {
            const string separator = "\n\n";
            _currentText.Append(separator);
            TextDelta?.Invoke(separator);
        }

        _activeAgentMessageStart = _currentText.Length;
    }

    private void HandleTurnCompleted(JsonElement p, string? completedTurnId)
    {
        var isError = false;
        var errorMessage = "";
        if (p.TryGetProperty("turn", out var turn))
        {
            isError = turn.TryGetProperty("status", out var status) && status.GetString() == "failed";
            if (isError
                && turn.TryGetProperty("error", out var err)
                && err.ValueKind == JsonValueKind.Object
                && err.TryGetProperty("message", out var msg))
            {
                errorMessage = msg.GetString() ?? "";
            }
        }

        if (completedTurnId is not null
            && _terminalTurnErrors.Remove(completedTurnId, out var terminalError)
            && isError
            && errorMessage.Length == 0)
        {
            errorMessage = terminalError;
        }

        var text = _currentText.ToString();
        if (isError && text.Length == 0)
            text = errorMessage;

        // Codex reports token usage but no dollar cost.
        TurnCompleted?.Invoke(new AgentTurnResult(text, 0, _lastOutputTokens, ++_numTurns, isError));
    }

    /// <summary>Exercises the real Codex notification path without starting a subprocess.
    /// Used by the running app's Debug MCP surface to verify turn-error ordering.</summary>
    internal static string DebugTurnErrorLifecycle(string terminalError)
    {
        var session = new CodexChatSession("codex", ".");
        var stoppedBeforeCompletion = false;
        AgentTurnResult? completed = null;
        session.Errored += _ => stoppedBeforeCompletion = true;
        session.TurnCompleted += result => completed = result;

        using (var error = JsonDocument.Parse(JsonSerializer.Serialize(new
               {
                   @params = new
                   {
                       threadId = "debug-thread",
                       turnId = "debug-turn",
                       willRetry = false,
                       error = new { message = terminalError },
                   },
               })))
        {
            session.HandleNotification("error", error.RootElement);
        }

        var stateAfterError = !stoppedBeforeCompletion && completed is null ? "waiting" : "stopped";
        using (var completion = JsonDocument.Parse(
                   "{\"params\":{\"threadId\":\"debug-thread\",\"turn\":{\"id\":\"debug-turn\",\"status\":\"failed\"}}}"))
        {
            session.HandleNotification("turn/completed", completion.RootElement);
        }

        return $"{stateAfterError}|{(completed?.IsError == true ? "failed" : "not-failed")}|{completed?.Text}";
    }

    /// <summary>Exercises the real multi-item notification path where Codex emits
    /// commentary, a tool request, and then an empty final-answer item.</summary>
    public static string DebugAgentMessageAccumulationLifecycle()
    {
        var session = new CodexChatSession("codex", ".");
        var preview = "";
        AgentTurnResult? completed = null;
        session.TextDelta += text => preview += text;
        session.TextReplaced += text => preview = text;
        session.TurnCompleted += result => completed = result;

        void Notify(string method, string json)
        {
            using var document = JsonDocument.Parse(json);
            session.HandleNotification(method, document.RootElement);
        }

        Notify("item/started",
            "{\"params\":{\"item\":{\"type\":\"agentMessage\",\"id\":\"commentary\"}}}");
        Notify("item/agentMessage/delta",
            "{\"params\":{\"itemId\":\"commentary\",\"delta\":\"I will inspect the server.\"}}");
        Notify("item/completed",
            "{\"params\":{\"item\":{\"type\":\"agentMessage\",\"id\":\"commentary\",\"text\":\"I will inspect the server.\"}}}");
        Notify("item/started",
            "{\"params\":{\"item\":{\"type\":\"agentMessage\",\"id\":\"tool\"}}}");
        Notify("item/agentMessage/delta",
            "{\"params\":{\"itemId\":\"tool\",\"delta\":\"```jrm-tool\\nterminal.run\\nprintf ok\\n```\"}}");
        Notify("item/completed",
            "{\"params\":{\"item\":{\"type\":\"agentMessage\",\"id\":\"tool\",\"text\":\"```jrm-tool\\nterminal.run\\nprintf ok\\n```\"}}}");
        Notify("item/started",
            "{\"params\":{\"item\":{\"type\":\"agentMessage\",\"id\":\"empty-final\"}}}");
        Notify("item/completed",
            "{\"params\":{\"item\":{\"type\":\"agentMessage\",\"id\":\"empty-final\",\"text\":\"\"}}}");
        Notify("turn/completed",
            "{\"params\":{\"turn\":{\"id\":\"turn-1\",\"status\":\"completed\"}}}");

        const string expected = "I will inspect the server.\n\n```jrm-tool\nterminal.run\nprintf ok\n```";
        var preserved = preview == expected && completed?.Text == expected;
        return $"preview={preview}; completed={completed?.Text}; accumulated={preserved}";
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        _threadReady.TrySetCanceled();
        _activeTurnReady?.TrySetCanceled();
        _steerResponse?.TrySetCanceled();

        var process = _process;
        if (process is null)
            return;

        try
        {
            if (!process.HasExited)
            {
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
        }

        await Task.CompletedTask;
    }
}
