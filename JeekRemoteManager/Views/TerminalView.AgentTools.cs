using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Diagnostics;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using JeekRemoteManager.Models;
using JeekRemoteManager.Services;
using JeekRemoteManager.ViewModels;
using Renci.SshNet;
using SvcSystems.UI.Terminal;
using JeekTools;

namespace JeekRemoteManager.Views;

/// <summary>The terminal as a tool surface for agents: command capture, status, scrollback, keys and file transfer.</summary>
public partial class TerminalView
{
    /// <summary>Adapts this terminal tab's harness to the product MCP tool surface.</summary>
    private sealed class TerminalAgentRemoteTools(TerminalView owner) : IAgentRemoteTools
    {
        public string ConnectionLabel => owner.BuildAiConversationLabel();

        public bool IsWsl => owner._connection?.IsWsl == true;

        public Task<string> RunCommandAsync(
            string command,
            int? timeoutSeconds = null,
            CancellationToken cancellationToken = default) =>
            owner.RunCapturedAsync(command, timeoutSeconds, cancellationToken);

        public Task<string> TransferFilesAsync(AgentFileTransfer transfer, CancellationToken cancellationToken = default) =>
            owner.TransferFilesAsync(transfer, cancellationToken);

        public Task<string> RunTerminalActionAsync(AgentTerminalAction action, CancellationToken cancellationToken = default) =>
            owner.RunAiTerminalActionAsync(action, cancellationToken);

        public Task<string> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(owner.BuildAgentTerminalStatus());

        public Task<string> GetConnectionInfoAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(owner.BuildAgentConnectionInfo());

        public Task<string> GetScrollbackAsync(int lines, CancellationToken cancellationToken = default) =>
            owner.GetAgentScrollbackAsync(lines);

        public Task<string> SendKeysAsync(string text, CancellationToken cancellationToken = default) =>
            Task.FromResult(owner.SendKeysForAgent(text));

        public Task<string> GetMonitorSnapshotAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(owner.BuildAgentMonitorSnapshot());
    }

    /// <summary>
    /// Runs a command on the server's interactive shell and returns its captured output
    /// (exit code + stdout/stderr), so the AI assistant can act on the result. Reuses the
    /// same payload runner as script execution, and echoes the command into the terminal so
    /// the user sees exactly what the assistant ran.
    /// </summary>
    internal async Task<string> RunCapturedAsync(
        string command,
        int? timeoutSeconds = null,
        CancellationToken cancellationToken = default)
    {
        if (_disposed)
            return "[terminal closed]";

        try
        {
            await WaitUntilConnectedAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            return $"[not connected: {ex.Message}]";
        }

        await _scriptLock.WaitAsync(cancellationToken);
        var commandStarted = false;
        var startedAt = Environment.TickCount64;
        CancellationTokenSource? timeoutCts = null;
        try
        {
            if (_disposed || _shellClosed || _channel is null)
                return "[not connected]";

            Interlocked.Exchange(ref _isAiCommandRunning, 1);
            Interlocked.Increment(ref _aiCommandExecutionCount);
            commandStarted = true;

            Dispatcher.UIThread.Post(() =>
                FeedLine($"\r\n\u001b[35m[AI]\u001b[0m $ {AiCommandTerminalText.NormalizeForTerminalEcho(command)}"));

            var runToken = cancellationToken;
            if (timeoutSeconds is > 0)
            {
                timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds.Value));
                runToken = timeoutCts.Token;
            }

            try
            {
                var result = await ExecuteRemotePayloadAsync(command, runToken);
                await FeedCompletionLineAndRefreshPromptAsync(
                    $"\u001b[35m[AI exit {result.ExitCode}]\u001b[0m");

                var output = CleanShellOutput(result.Output ?? string.Empty).Trim();
                Interlocked.Increment(ref _aiCommandCompletionCount);
                var durationMs = Environment.TickCount64 - startedAt;
                return $"[exit {result.ExitCode}]\n[duration_ms {durationMs}]\n{output}";
            }
            catch (OperationCanceledException) when (
                timeoutCts is not null
                && timeoutCts.IsCancellationRequested
                && !cancellationToken.IsCancellationRequested)
            {
                // Product-side timeout: force full recovery so the channel is usable again.
                ForceInterruptTerminalCommand();
                var durationMs = Environment.TickCount64 - startedAt;
                Interlocked.Increment(ref _aiCommandCompletionCount);
                return $"[timeout after {timeoutSeconds}s; interrupted]\n[duration_ms {durationMs}]";
            }
        }
        catch (OperationCanceledException)
        {
            return "[command cancelled or timed out]";
        }
        catch (TerminalConnectionLostException)
        {
            var reconnectTask = Volatile.Read(ref _aiAutoReconnectTask);
            if (reconnectTask is null)
                return "[command interrupted: server connection lost; command outcome is unknown]";

            try
            {
                var reconnected = await reconnectTask.WaitAsync(cancellationToken).ConfigureAwait(false);
                return reconnected
                    ? "[command interrupted: server connection lost; terminal reconnected automatically; command outcome is unknown]"
                    : "[command interrupted: server connection lost; automatic reconnect failed; command outcome is unknown]";
            }
            catch (OperationCanceledException)
            {
                return "[command interrupted: server connection lost; reconnect wait cancelled; command outcome is unknown]";
            }
        }
        catch (Exception ex)
        {
            return $"[command failed: {ex.Message}]";
        }
        finally
        {
            timeoutCts?.Dispose();
            if (commandStarted)
                Interlocked.Exchange(ref _isAiCommandRunning, 0);
            _scriptLock.Release();
        }
    }

    /// <summary>Runs a captured terminal command for live Debug MCP verification.</summary>
    public Task<string> RunTerminalCommandForDebugAsync(string command, int? timeoutSeconds = null) =>
        RunCapturedAsync(command, timeoutSeconds, CancellationToken.None);

    internal string BuildAgentTerminalStatus()
    {
        var transferActive = _activeZmodemQueue is not null || _aiTransferCompletion is not null;
        return
            $"connection={BuildAiConversationLabel()}\n" +
            $"connected={IsConnected}\n" +
            $"shell_closed={_shellClosed}\n" +
            $"command_lock_available={IsCommandLockAvailable}\n" +
            $"ai_command_running={IsAiCommandRunning}\n" +
            $"terminal_command_running={IsTerminalCommandRunning}\n" +
            $"user_input_suppressed={IsUserInputSuppressed}\n" +
            $"transfer_in_progress={transferActive}\n" +
            $"script_running={IsScriptRunning}\n" +
            $"login_sequence_state={LoginSequenceState}\n" +
            $"bastion_session_state={BastionSessionState}\n" +
            $"connection_generation={ConnectionGeneration}\n" +
            $"ai_exec_count={AiCommandExecutionCount}\n" +
            $"ai_complete_count={AiCommandCompletionCount}\n" +
            $"recovery_count={TerminalRecoveryCount}\n" +
            $"is_wsl={_connection?.IsWsl == true}\n" +
            $"auto_reconnect_state={AiAutoReconnectState}";
    }

    internal string BuildAgentConnectionInfo()
    {
        var c = _connection;
        if (c is null)
            return "label=(none)\ntype=unknown\nconnected=false";

        var kind = c.IsWsl ? "WSL" : c.IsRdp ? "RDP" : "SSH";
        var target = c.IsWsl
            ? (string.IsNullOrWhiteSpace(c.WslDistro) ? "default WSL distribution" : c.WslDistro.Trim())
            : string.IsNullOrWhiteSpace(c.Host)
                ? "(unknown host)"
                : $"{c.Username}@{c.Host}:{c.Port}";

        var notes = string.IsNullOrWhiteSpace(c.Notes) ? "(none)" : c.Notes.Trim();
        return
            $"label={BuildAiConversationLabel()}\n" +
            $"type={kind}\n" +
            $"target={target}\n" +
            $"source_path={_sourcePath ?? "(none)"}\n" +
            $"connected={IsConnected}\n" +
            $"notes={notes}";
    }

    internal async Task<string> GetAgentScrollbackAsync(int lines)
    {
        lines = Math.Clamp(lines <= 0 ? 80 : lines, 1, 500);
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_disposed)
                return "[terminal closed]";
            return CaptureScrollbackText(lines);
        });
    }

    private string CaptureScrollbackText(int lines)
    {
        var terminal = _model.Terminal;
        var buffer = terminal.Buffer;
        var lastRow = buffer.Length;
        var firstRow = Math.Max(0, lastRow - lines);
        var text = new StringBuilder();

        for (var row = firstRow; row < lastRow; row++)
        {
            if (row > firstRow)
                text.Append('\n');
            if (buffer.GetLine(row) is { } line)
                text.Append(line.TranslateToString(true).TrimEnd());
        }

        var body = text.ToString().TrimEnd();
        return string.IsNullOrEmpty(body)
            ? $"[scrollback lines=0 requested={lines}]\n"
            : $"[scrollback lines={Math.Min(lines, lastRow - firstRow)} requested={lines}]\n{body}";
    }

    internal string SendKeysForAgent(string text)
    {
        if (_disposed)
            return "[terminal closed]";
        if (!IsConnected || _channel is null)
            return "[not connected]";
        if (string.IsNullOrEmpty(text))
            return "[error] text is required";

        // Unescape common agent encodings so "q\\n" works as pager quit + Enter.
        var payload = text
            .Replace("\\r\\n", "\r\n", StringComparison.Ordinal)
            .Replace("\\n", "\n", StringComparison.Ordinal)
            .Replace("\\r", "\r", StringComparison.Ordinal)
            .Replace("\\t", "\t", StringComparison.Ordinal);

        try
        {
            var bytes = _terminalEncoding.GetBytes(payload);
            WriteToShell(bytes);
            return $"[keys sent bytes={bytes.Length}]";
        }
        catch (Exception ex)
        {
            return $"[error] failed to send keys: {ex.Message}";
        }
    }

    internal string BuildAgentMonitorSnapshot()
    {
        var vm = _monitorViewModel;
        if (vm is null)
            return "[monitor unavailable]\nreason=panel never opened for this tab\nhint=open the monitor panel or enable auto-open on the connection";

        if (vm.IsFailed)
            return $"[monitor failed]\nhost={vm.HostLabel}\naddress={vm.AddressText}";

        if (!vm.HasData)
            return $"[monitor waiting]\nhost={vm.HostLabel}\naddress={vm.AddressText}\nshell_ready={vm.IsMonitorShellReady}\nsamples={vm.MonitorSampleCount}";

        var sb = new StringBuilder();
        sb.AppendLine("[monitor ok]");
        sb.AppendLine($"host={vm.HostLabel}");
        sb.AppendLine($"address={vm.AddressText}");
        sb.AppendLine($"uptime={vm.UptimeText}");
        sb.AppendLine($"latency={vm.LatencyText}");
        sb.AppendLine($"load={vm.LoadText}");
        sb.AppendLine($"cpu={vm.CpuText} ({vm.CpuPercent:0.#}%)");
        sb.AppendLine($"mem={vm.MemText} ({vm.MemPercent:0.#}%)");
        sb.AppendLine($"swap={vm.SwapText} ({vm.SwapPercent:0.#}%)");
        sb.AppendLine($"net_up={vm.UploadRateText}");
        sb.AppendLine($"net_down={vm.DownloadRateText}");
        sb.AppendLine($"samples={vm.MonitorSampleCount}");

        if (vm.Disks.Count > 0)
        {
            sb.AppendLine("disks:");
            foreach (var disk in vm.Disks.Take(12))
                sb.AppendLine($"  {disk.MountPoint} size={disk.SizeText} used={disk.UsedPercent:0.#}%");
        }

        if (vm.Processes.Count > 0)
        {
            sb.AppendLine("top_processes:");
            foreach (var proc in vm.Processes.Take(8))
                sb.AppendLine($"  cpu={proc.CpuText} mem={proc.MemText} {proc.Command}");
        }

        return sb.ToString().TrimEnd();
    }

    private async Task<string> RunAiTerminalActionAsync(
        AgentTerminalAction action,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (action == AgentTerminalAction.ForceInterrupt)
        {
            var hadActiveCommand = IsTerminalCommandRunning;
            ForceInterruptTerminalCommand();
            return hadActiveCommand
                ? "[terminal command interrupted; shell input recovery requested]"
                : "[interrupt sent; no captured terminal command was active]";
        }

        var generation = ConnectionGeneration;
        ReconnectTerminal();
        if (ConnectionGeneration == generation)
            return "[terminal reconnect was not started]";

        try
        {
            await WaitUntilConnectedAsync(cancellationToken);
            return "[terminal reconnected]";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"[terminal reconnect failed: {ex.Message}]";
        }
    }

    /// <summary>
    /// Runs an AI-requested ZMODEM file transfer through the interactive shell: types
    /// `rz`/`sz` and drives the existing transfer machinery with pre-chosen paths (no
    /// pickers). Returns a one-line outcome string for the assistant.
    /// </summary>
    internal async Task<string> TransferFilesAsync(AgentFileTransfer transfer, CancellationToken cancellationToken)
    {
        if (_disposed)
            return "[terminal closed]";
        if (_connection?.IsWsl == true)
            return "[not available on WSL: Windows drives are mounted under /mnt (C:\\ is /mnt/c) — copy files directly instead]";

        try
        {
            await WaitUntilConnectedAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            return $"[not connected: {ex.Message}]";
        }

        await _scriptLock.WaitAsync(cancellationToken);
        try
        {
            if (_disposed || _shellClosed || _channel is null)
                return "[not connected]";
            if (!_channel.SupportsBinaryTransfers)
                return "[ZMODEM transfers are not supported on this connection]";
            if (_activeZmodemQueue is not null || _aiTransferCompletion is not null)
                return "[another file transfer is already in progress]";

            return transfer.IsUpload
                ? await RunAiUploadAsync(transfer, cancellationToken)
                : await RunAiDownloadAsync(transfer, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return "[transfer cancelled]";
        }
        catch (Exception ex)
        {
            return $"[transfer failed: {ex.Message}]";
        }
        finally
        {
            _scriptLock.Release();
        }
    }

    private async Task<string> RunAiUploadAsync(AgentFileTransfer transfer, CancellationToken cancellationToken)
    {
        var missing = transfer.Sources.Where(file => !File.Exists(file)).ToList();
        if (missing.Count > 0)
            return "[upload failed: local file(s) not found: " + string.Join(", ", missing) + "]";

        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _aiTransferCompletion = completion;
        try
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!_disposed)
                    FeedLine($"\r\n\u001b[35m[AI]\u001b[0m zmodem upload: {transfer.Sources.Count} file(s)"
                        + (transfer.Destination is null ? "" : $" -> {transfer.Destination}"));
            });

            StartDropUpload(transfer.Sources, transfer.Destination);
            return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Interlocked.Exchange(ref _pendingDropUploadFiles, null);
            _activeZmodemCancellation?.Cancel();
            return "[upload cancelled]";
        }
        finally
        {
            _aiTransferCompletion = null;
        }
    }

    private async Task<string> RunAiDownloadAsync(AgentFileTransfer transfer, CancellationToken cancellationToken)
    {
        string folder;
        try
        {
            folder = string.IsNullOrWhiteSpace(transfer.Destination)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
                : transfer.Destination!;
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex)
        {
            return $"[download failed: cannot use local folder: {ex.Message}]";
        }

        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _aiTransferCompletion = completion;
        try
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!_disposed)
                    FeedLine($"\r\n\u001b[35m[AI]\u001b[0m zmodem download -> {folder}");
            });

            _pendingDownloadFolder = folder;
            var generation = Interlocked.Increment(ref _downloadRequestGeneration);
            // Ctrl+U discards anything half-typed at the prompt so `sz` runs clean.
            WriteToShell("\u0015sz " + string.Join(" ", transfer.Sources.Select(QuoteForRemoteShell)) + "\r");
            _ = ExpireDownloadRequestAsync(generation);

            return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Interlocked.Exchange(ref _pendingDownloadFolder, null);
            _activeZmodemCancellation?.Cancel();
            return "[download cancelled]";
        }
        finally
        {
            _aiTransferCompletion = null;
        }
    }
}
