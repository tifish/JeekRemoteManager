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

    private static readonly JsonSerializerOptions WireOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _executablePath;
    private readonly string _workingDirectory;
    private readonly string? _developerInstructions;
    private readonly string? _model;
    private readonly string? _effort;
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
    private volatile string? _currentTurnId;
    private string _finalText = "";
    private long _lastOutputTokens;
    private int _numTurns;

    public CodexChatSession(
        string executablePath,
        string workingDirectory,
        string? developerInstructions = null,
        string? model = null,
        string? effort = null)
    {
        _executablePath = executablePath;
        _workingDirectory = workingDirectory;
        _developerInstructions = developerInstructions;
        _model = model;
        _effort = effort;
    }

    /// <summary>The Codex thread id reported by the <c>thread/start</c> response.</summary>
    public string? SessionId { get; private set; }

    public event Action<string>? SessionInitialized;
    public event Action<string>? TextDelta;
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

        // The first send can race the initialize → thread/start handshake; wait for the
        // thread id rather than failing.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(HandshakeTimeout);
        var threadId = await _threadReady.Task.WaitAsync(timeout.Token).ConfigureAwait(false);

        _currentText.Clear();
        _finalText = "";
        _lastOutputTokens = 0;
        _currentTurnId = null;

        _turnStartRequestId = await SendRequestAsync("turn/start", new
        {
            threadId,
            input = new[] { new { type = "text", text } },
            effort = string.IsNullOrWhiteSpace(_effort) ? null : _effort,
        }, cancellationToken).ConfigureAwait(false);
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

    private async Task<int> SendRequestAsync(string method, object? @params, CancellationToken cancellationToken = default)
    {
        var id = Interlocked.Increment(ref _nextRequestId);
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
                if (sawTracing || TracingLineRegex.IsMatch(text))
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
            if (id == _initializeRequestId || id == _threadStartRequestId)
                _threadReady.TrySetException(new InvalidOperationException(message));
            if (!_disposed)
                Errored?.Invoke($"Codex error: {message}");
            return;
        }

        if (!root.TryGetProperty("result", out var result))
            return;

        if (id == _initializeRequestId)
        {
            await SendNotificationAsync("initialized").ConfigureAwait(false);
            _threadStartRequestId = await SendRequestAsync("thread/start", new
            {
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
            }
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
            case "item/agentMessage/delta":
                if (p.TryGetProperty("delta", out var delta) && delta.GetString() is { Length: > 0 } text)
                {
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
                    _finalText = itemText.GetString() ?? "";
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
                }
                break;

            case "turn/completed":
                _currentTurnId = null;
                HandleTurnCompleted(p);
                break;

            case "error":
                // Fatal turn errors also produce turn/completed with status=failed, but a
                // retried error is just noise — only surface the terminal ones.
                if (p.TryGetProperty("willRetry", out var retry) && retry.ValueKind != JsonValueKind.True
                    && p.TryGetProperty("error", out var err)
                    && err.TryGetProperty("message", out var errMsg))
                {
                    Errored?.Invoke(errMsg.GetString() ?? "unknown Codex error");
                }
                break;
        }
    }

    private void HandleTurnCompleted(JsonElement p)
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

        var text = _finalText.Length > 0 ? _finalText : _currentText.ToString();
        if (isError && text.Length == 0)
            text = errorMessage;

        // Codex reports token usage but no dollar cost.
        TurnCompleted?.Invoke(new AgentTurnResult(text, 0, _lastOutputTokens, ++_numTurns, isError));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        _threadReady.TrySetCanceled();

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
