using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace JeekRemoteManager.Services;

/// <summary>
/// Wraps a long-lived <c>claude</c> CLI subprocess in headless stream-json mode as a
/// single multi-turn chat session. One process stays alive for the whole conversation:
/// each user message is written to stdin as one NDJSON line, and stdout events are parsed
/// and surfaced as strongly-typed events. This is the same protocol the official Agent SDK
/// speaks; there is no .NET SDK, so we drive the CLI directly.
/// </summary>
public sealed class ClaudeChatSession : IAgentChatSession
{
    // Full local agent tools are allowed (Bash/Read/Edit/WebSearch/…). Remote server work
    // still goes through the chat harness via ```bash fences — see BuildAssistantSystemPrompt.
    // bypassPermissions skips interactive prompts so the headless CLI can act autonomously.

    private readonly string _executablePath;
    private readonly string _workingDirectory;
    private readonly string? _appendSystemPrompt;
    private readonly string? _resumeSessionId;
    private readonly string? _model;
    private readonly string? _effort;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly StringBuilder _currentText = new();

    private Process? _process;
    private volatile bool _disposed;
    private bool _started;

    public ClaudeChatSession(
        string executablePath,
        string workingDirectory,
        string? appendSystemPrompt = null,
        string? resumeSessionId = null,
        string? model = null,
        string? effort = null)
    {
        _executablePath = executablePath;
        _workingDirectory = workingDirectory;
        _appendSystemPrompt = appendSystemPrompt;
        _resumeSessionId = resumeSessionId;
        _model = model;
        _effort = effort;
    }

    /// <summary>The session id reported by the CLI's <c>init</c> event; use it to resume later.</summary>
    public string? SessionId { get; private set; }

    /// <summary>Raised once, when the CLI reports its <c>system/init</c> event.</summary>
    public event Action<string>? SessionInitialized;

    /// <summary>Incremental assistant answer text (a <c>text_delta</c>). Thinking is ignored.</summary>
    public event Action<string>? TextDelta;

    /// <summary>The authoritative final result replaces any partial stream preview.</summary>
    public event Action<string>? TextReplaced;

    /// <summary>A turn finished; carries the final text plus cost/usage.</summary>
    public event Action<AgentTurnResult>? TurnCompleted;

    /// <summary>A protocol or process error (stderr line, parse failure, or crash).</summary>
    public event Action<string>? Errored;

    /// <summary>The subprocess exited.</summary>
    public event Action? Exited;

    /// <summary>Launches the subprocess. Call once before the first <see cref="SendAsync"/>.</summary>
    public void Start()
    {
        if (_started)
            return;
        _started = true;

        Directory.CreateDirectory(_workingDirectory);

        var psi = new ProcessStartInfo
        {
            FileName = _executablePath,
            WorkingDirectory = _workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };

        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add("--input-format");
        psi.ArgumentList.Add("stream-json");
        psi.ArgumentList.Add("--output-format");
        psi.ArgumentList.Add("stream-json");
        psi.ArgumentList.Add("--verbose");
        // Headless stream-json has no channel to answer permission prompts, so the model's
        // built-in tools (which act on the local Windows machine) run unconfirmed.
        psi.ArgumentList.Add("--permission-mode");
        psi.ArgumentList.Add("bypassPermissions");
        if (!string.IsNullOrWhiteSpace(_model))
        {
            psi.ArgumentList.Add("--model");
            psi.ArgumentList.Add(_model);
        }
        if (!string.IsNullOrWhiteSpace(_effort))
        {
            psi.ArgumentList.Add("--effort");
            psi.ArgumentList.Add(_effort);
        }
        if (!string.IsNullOrWhiteSpace(_appendSystemPrompt))
        {
            psi.ArgumentList.Add("--append-system-prompt");
            psi.ArgumentList.Add(_appendSystemPrompt);
        }
        if (!string.IsNullOrWhiteSpace(_resumeSessionId))
        {
            psi.ArgumentList.Add("--resume");
            psi.ArgumentList.Add(_resumeSessionId);
        }

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.Exited += (_, _) =>
        {
            if (!_disposed)
                Exited?.Invoke();
        };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            Errored?.Invoke($"Failed to start Claude CLI: {ex.Message}");
            return;
        }

        _process = process;
        _ = Task.Run(() => ReadStdoutLoopAsync(process));
        _ = Task.Run(() => ReadStderrLoopAsync(process));
    }

    /// <summary>Sends one user message as an NDJSON line on stdin.</summary>
    public async Task SendAsync(string text, CancellationToken cancellationToken = default)
    {
        var process = _process;
        if (_disposed || process is null || process.HasExited)
            throw new InvalidOperationException("The Claude CLI session is not running.");

        var line = JsonSerializer.Serialize(new
        {
            type = "user",
            message = new
            {
                role = "user",
                content = new[] { new { type = "text", text } },
            },
        });

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _currentText.Clear();
            await process.StandardInput.WriteAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await process.StandardInput.WriteAsync("\n".AsMemory(), cancellationToken).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Asks the CLI to abort the in-flight turn (stream-json control request).
    /// The turn still ends with a normal <c>result</c> event.</summary>
    public async Task InterruptAsync(CancellationToken cancellationToken = default)
    {
        var process = _process;
        if (_disposed || process is null || process.HasExited)
            return;

        var line = JsonSerializer.Serialize(new
        {
            type = "control_request",
            request_id = $"interrupt-{Interlocked.Increment(ref _interruptCounter)}",
            request = new { subtype = "interrupt" },
        });

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

    private int _interruptCounter;

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
                    HandleLine(line);
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
                Errored?.Invoke($"Reading Claude CLI output failed: {ex.Message}");
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
                if (!string.IsNullOrWhiteSpace(line))
                    Errored?.Invoke(line);
            }
        }
        catch
        {
            // stderr drain is best-effort.
        }
    }

    private void HandleLine(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var typeProp))
            return;

        switch (typeProp.GetString())
        {
            case "system":
                if (root.TryGetProperty("subtype", out var subtype)
                    && subtype.GetString() == "init"
                    && root.TryGetProperty("session_id", out var sid))
                {
                    SessionId = sid.GetString();
                    if (SessionId is not null)
                        SessionInitialized?.Invoke(SessionId);
                }
                break;

            case "stream_event":
                HandleStreamEvent(root);
                break;

            case "result":
                HandleResult(root);
                break;
        }
    }

    private void HandleStreamEvent(JsonElement root)
    {
        if (!root.TryGetProperty("event", out var evt)
            || !evt.TryGetProperty("type", out var evtType)
            || evtType.GetString() != "content_block_delta"
            || !evt.TryGetProperty("delta", out var delta)
            || !delta.TryGetProperty("type", out var deltaType)
            || deltaType.GetString() != "text_delta"
            || !delta.TryGetProperty("text", out var textProp))
        {
            return;
        }

        var text = textProp.GetString();
        if (string.IsNullOrEmpty(text))
            return;

        _currentText.Append(text);
        TextDelta?.Invoke(text);
    }

    private void HandleResult(JsonElement root)
    {
        var isError = root.TryGetProperty("is_error", out var err) && err.ValueKind == JsonValueKind.True;
        var text = root.TryGetProperty("result", out var resultProp) ? resultProp.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(text))
            text = _currentText.ToString();

        if (!string.IsNullOrEmpty(text))
            TextReplaced?.Invoke(text);

        var cost = root.TryGetProperty("total_cost_usd", out var costProp) && costProp.TryGetDouble(out var c) ? c : 0;
        var numTurns = root.TryGetProperty("num_turns", out var ntProp) && ntProp.TryGetInt32(out var nt) ? nt : 0;

        long outputTokens = 0;
        if (root.TryGetProperty("usage", out var usage)
            && usage.TryGetProperty("output_tokens", out var ot)
            && ot.TryGetInt64(out var otv))
        {
            outputTokens = otv;
        }

        TurnCompleted?.Invoke(new AgentTurnResult(text, cost, outputTokens, numTurns, isError));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

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
