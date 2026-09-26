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

/// <summary>File transfer through the terminal: drag-and-drop upload and ZMODEM.</summary>
public partial class TerminalView
{
    // Dropping local files onto the terminal launches `rz` on the remote shell and
    // feeds the dropped files into the existing ZMODEM upload path, so it works
    // through jump hosts exactly like a manually typed `rz`. Shift+drop pastes the
    // local path(s) instead (the Windows Terminal convention).
    private void OnTerminalDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = CanAcceptFileDrop(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnTerminalDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!CanAcceptFileDrop(e))
            return;

        var paths = (e.DataTransfer.TryGetFiles() ?? [])
            .Select(item => item.TryGetLocalPath())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .ToList();
        if (paths.Count == 0)
            return;

        FocusTerminal();

        if (_connection?.IsWsl == true)
        {
            // No ZMODEM over ConPTY, and the Windows filesystem is mounted inside
            // the distro anyway: dropping pastes the /mnt/... path instead.
            WriteToShell(string.Join(" ",
                paths.Select(p => QuoteForRemoteShell(WslDistroService.ToWslPath(p)))));
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            WriteToShell(string.Join(" ", paths.Select(QuoteForRemoteShell)));
            return;
        }

        var files = paths.Where(File.Exists).ToList();
        if (files.Count < paths.Count)
            FeedLine("\r\n\u001b[33m[zmodem upload]\u001b[0m Folders cannot be uploaded and were skipped.");
        if (files.Count == 0)
            return;

        StartDropUpload(files);
    }

    private bool CanAcceptFileDrop(DragEventArgs e) =>
        IsConnected
        && !_suppressUserInput
        && _activeZmodemQueue is null
        && _pendingDropUploadFiles is null
        && e.DataTransfer.Contains(DataFormat.File);

    private void StartDropUpload(IReadOnlyList<string> files, string? remoteDirectory = null)
    {
        var generation = Interlocked.Increment(ref _dropUploadGeneration);
        _pendingDropUploadFiles = files;

        var rz = remoteDirectory is null
            ? "rz"
            : $"(mkdir -p {QuoteForRemoteShell(remoteDirectory)} && cd {QuoteForRemoteShell(remoteDirectory)} && rz)";

        // Ctrl+U discards anything half-typed at the prompt so `rz` runs clean.
        WriteToShell("\u0015" + rz + "\r");

        _ = ExpireDropUploadAsync(generation);
    }

    private async Task ExpireDropUploadAsync(int generation)
    {
        // If the ZMODEM handshake never arrives (rz missing, shell busy inside a
        // full-screen app, session dropped), release the queued files and explain.
        await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        if (generation != _dropUploadGeneration)
            return;
        if (Interlocked.Exchange(ref _pendingDropUploadFiles, null) is not null)
        {
            FeedLineOnUiThread("\u001b[33m[zmodem upload]\u001b[0m rz did not respond. Is lrzsz installed on the remote host?");
            _aiTransferCompletion?.TrySetResult("[upload failed: rz did not respond — is lrzsz installed on the remote host?]");
        }
    }

    private async Task ExpireDownloadRequestAsync(int generation)
    {
        // Same idea for an AI-requested `sz`: no handshake within 5s means sz is
        // missing or the shell was not at a prompt.
        await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        if (generation != _downloadRequestGeneration)
            return;
        if (Interlocked.Exchange(ref _pendingDownloadFolder, null) is not null)
        {
            FeedLineOnUiThread("\u001b[33m[zmodem download]\u001b[0m sz did not respond. Is lrzsz installed on the remote host?");
            _aiTransferCompletion?.TrySetResult("[download failed: sz did not respond — is lrzsz installed on the remote host?]");
        }
    }

    private static string QuoteForRemoteShell(string path) =>
        Regex.IsMatch(path, "^[A-Za-z0-9_@%+=:,./-]+$")
            ? path
            : "'" + path.Replace("'", "'\\''") + "'";

    private void ScheduleZmodemDetectionFlush()
    {
        if (_disposed || _activeZmodemQueue is not null)
            return;

        _zmodemDetectionFlushTimer ??= new Timer(_ => FlushZmodemDetectionBuffer());
        _zmodemDetectionFlushTimer.Change(80, Timeout.Infinite);
    }

    private void FlushZmodemDetectionBuffer()
    {
        byte[] data;
        lock (_zmodemDetectionGate)
        {
            if (_activeZmodemQueue is not null)
                return;
            data = _zmodemDetector.Flush();
        }

        FeedBytes(data);
    }

    private void StartZmodemTransfer(ZmodemDetection detection)
    {
        var queue = new ZmodemByteQueue();
        queue.Append(detection.ProtocolBytes);
        var trace = ZmodemTraceLog.CreateIfEnabled();
        trace?.Write($"detected direction={detection.Direction}");
        trace?.WriteBytes("RX trigger protocol", detection.ProtocolBytes);
        trace?.WriteBytes("RX trigger display", detection.DisplayBytes);

        var cancellation = new CancellationTokenSource();
        _activeZmodemQueue = queue;
        _activeZmodemTrace = trace;
        _activeZmodemCancellation = cancellation;
        _suppressUserInput = true;

        _ = Task.Run(() => RunZmodemTransferAsync(detection.Direction, queue, trace, cancellation));
    }

    private async Task RunZmodemTransferAsync(
        ZmodemTransferDirection direction,
        ZmodemByteQueue queue,
        ZmodemTraceLog? trace,
        CancellationTokenSource cancellation)
    {
        Action<string>? traceWriter = trace is null ? null : trace.Write;
        var session = new ZmodemSession(WriteZmodemBytesAsync, queue.ReadByteAsync, traceWriter);
        if (trace is not null)
            FeedLineOnUiThread($"\r\n\u001b[36m[zmodem trace]\u001b[0m {trace.FilePath}");
        try
        {
            if (direction == ZmodemTransferDirection.Download)
            {
                // A folder queued by the AI panel skips the picker; a manually typed `sz` asks.
                var folder = Interlocked.Exchange(ref _pendingDownloadFolder, null);
                if (folder is null)
                {
                    FeedLineOnUiThread("\r\n\u001b[36m[zmodem download]\u001b[0m Choose a local folder.");
                    folder = await PickZmodemDownloadFolderAsync().ConfigureAwait(false);
                }
                else
                {
                    // Move past the echoed `sz` command line.
                    FeedLineOnUiThread(string.Empty);
                }

                if (string.IsNullOrWhiteSpace(folder))
                {
                    await session.CancelAsync(CancellationToken.None).ConfigureAwait(false);
                    FeedLineOnUiThread("\u001b[33m[zmodem cancelled]\u001b[0m");
                    _aiTransferCompletion?.TrySetResult("[download cancelled]");
                    return;
                }

                FeedLineOnUiThread($"\u001b[36m[zmodem download]\u001b[0m Receiving to {folder}");
                var result = await session.ReceiveAsync(folder, cancellation.Token).ConfigureAwait(false);
                FeedZmodemComplete(direction, result.Files);
                return;
            }

            // Files queued by drag-drop skip the picker; a manually typed `rz` asks.
            var files = Interlocked.Exchange(ref _pendingDropUploadFiles, null);
            if (files is null)
            {
                FeedLineOnUiThread("\r\n\u001b[36m[zmodem upload]\u001b[0m Choose local file(s).");
                files = await PickZmodemUploadFilesAsync().ConfigureAwait(false);
            }
            else
            {
                // Move past the echoed `rz` command line.
                FeedLineOnUiThread(string.Empty);
            }

            if (files.Count == 0)
            {
                await session.CancelAsync(CancellationToken.None).ConfigureAwait(false);
                FeedLineOnUiThread("\u001b[33m[zmodem cancelled]\u001b[0m");
                _aiTransferCompletion?.TrySetResult("[upload cancelled]");
                return;
            }

            FeedLineOnUiThread($"\u001b[36m[zmodem upload]\u001b[0m Sending {files.Count} file(s).");
            var uploadResult = await session.SendAsync(files, cancellation.Token).ConfigureAwait(false);
            FeedZmodemComplete(direction, uploadResult.Files);
        }
        catch (ZmodemTransferCanceledException ex)
        {
            trace?.WriteException(ex);
            FeedLineOnUiThread($"\u001b[33m[zmodem cancelled] {ex.Message}\u001b[0m");
            _aiTransferCompletion?.TrySetResult($"[transfer cancelled: {ex.Message}]");
        }
        catch (OperationCanceledException)
        {
            trace?.Write("operation cancelled");
            FeedLineOnUiThread("\u001b[33m[zmodem cancelled]\u001b[0m");
            _aiTransferCompletion?.TrySetResult("[transfer cancelled]");
        }
        catch (Exception ex)
        {
            trace?.WriteException(ex);
            try { await session.CancelAsync(CancellationToken.None).ConfigureAwait(false); } catch { /* ignore */ }
            FeedLineOnUiThread($"\u001b[31m[zmodem failed] {ex.Message}\u001b[0m");
            if (trace is not null)
                FeedLineOnUiThread($"\u001b[31m[zmodem trace] {trace.FilePath}\u001b[0m");
            _aiTransferCompletion?.TrySetResult($"[transfer failed: {ex.Message}]");
        }
        finally
        {
            var leftover = queue.DrainAvailable();
            trace?.WriteBytes("RX leftover", leftover);
            if (ReferenceEquals(_activeZmodemQueue, queue))
                _activeZmodemQueue = null;
            if (ReferenceEquals(_activeZmodemTrace, trace))
                _activeZmodemTrace = null;
            if (ReferenceEquals(_activeZmodemCancellation, cancellation))
                _activeZmodemCancellation = null;

            queue.Complete();
            FeedBytes(leftover);
            cancellation.Dispose();
            trace?.Dispose();
            _suppressUserInput = _activePayloadMonitor is not null;
            // Safety net: every exit path above should have completed this already.
            _aiTransferCompletion?.TrySetResult("[transfer ended]");
            FocusTerminal();
        }
    }

    private Task WriteZmodemBytesAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled(cancellationToken);

        _activeZmodemTrace?.WriteBytes("TX raw", bytes);
        WriteToShell(bytes);
        return Task.CompletedTask;
    }

    private async Task<string?> PickZmodemDownloadFolderAsync()
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel is null)
                {
                    tcs.TrySetResult(null);
                    return;
                }

                var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = "Select ZMODEM download folder",
                    AllowMultiple = false,
                });
                tcs.TrySetResult(folders.Count > 0 ? folders[0].TryGetLocalPath() : null);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        return await tcs.Task.ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<string>> PickZmodemUploadFilesAsync()
    {
        var tcs = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel is null)
                {
                    tcs.TrySetResult([]);
                    return;
                }

                var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Select ZMODEM upload files",
                    AllowMultiple = true,
                });
                tcs.TrySetResult(files
                    .Select(file => file.TryGetLocalPath())
                    .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    .Cast<string>()
                    .ToArray());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        return await tcs.Task.ConfigureAwait(false);
    }

    private void FeedZmodemComplete(ZmodemTransferDirection direction, IReadOnlyList<string> files)
    {
        var label = direction == ZmodemTransferDirection.Download ? "download" : "upload";
        var summary = files.Count == 1
            ? Path.GetFileName(files[0])
            : $"{files.Count} files";
        FeedLineOnUiThread($"\u001b[32m[zmodem {label} complete]\u001b[0m {summary}");
        _aiTransferCompletion?.TrySetResult(
            $"[{label} complete] {files.Count} file(s): {string.Join(", ", files)}");
    }
}
