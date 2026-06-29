using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using JeekRemoteManager.Models;
using JeekRemoteManager.Services;
using Renci.SshNet;
using SvcSystems.UI.Terminal;

namespace JeekRemoteManager.Views;

/// <summary>
/// Hosts a native Avalonia terminal control (SvcSystems.UI.Terminal) driven by an
/// SSH.NET interactive shell. Self-contained and reusable: a window can host one for
/// the Phase A spike, and the right-pane tab UI (Phase B) hosts one per tab.
/// Authentication is programmatic via <see cref="SshConnectionFactory"/> (the user
/// never types a password); live bytes, keyboard, title, and window size are wired
/// to the SSH channel.
/// </summary>
public partial class TerminalView : UserControl
{
    private readonly TerminalControlModel _model = new(new TerminalOptions
    {
        Cols = 120,
        Rows = 30,
        // Disable resize reflow so full-screen TUIs (top, vim, mc) stay stable.
        ReflowOnResize = false,
    });

    private Connection? _connection;
    private SshClient? _client;
    private ShellStream? _shell;
    private string? _sourcePath;
    private TaskCompletionSource<bool>? _connected;
    private readonly SemaphoreSlim _scriptLock = new(1, 1);
    private readonly object _shellWriteGate = new();
    private InteractiveShellPayloadMonitor? _activePayloadMonitor;
    private bool _shellClosed;
    private bool _suppressUserInput;
    private volatile bool _disposed;

    private sealed record RemotePayloadResult(int ExitCode, string Output);

    /// <summary>Raised on the UI thread when the remote shell sets its title (OSC).</summary>
    public event EventHandler<string>? TitleChanged;

    public Connection? Connection => _connection;

    public string? SourcePath => _sourcePath;

    public TerminalView()
    {
        InitializeComponent();

        Term.Model = _model;

        _model.UserInput += (_, e) =>
        {
            if (!_suppressUserInput)
                SendToShell(e.Data);
        };
        _model.SizeChanged += (_, _) => SyncWindowSize();
        _model.Terminal.TitleChanged += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            var title = _model.Terminal.Title;
            if (!string.IsNullOrWhiteSpace(title))
                TitleChanged?.Invoke(this, title);
        });
    }

    /// <summary>Connects the given connection and starts streaming. Call once.</summary>
    public void Start(Connection connection, string? sourcePath = null)
    {
        _connection = connection;
        _sourcePath = sourcePath;
        _connected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _shellClosed = false;
        FocusTerminal();
        _ = ConnectAsync();
    }

    public async Task WaitUntilConnectedAsync(CancellationToken cancellationToken = default)
    {
        var connected = _connected ?? throw new InvalidOperationException("Terminal has not started.");
        await connected.Task.WaitAsync(cancellationToken);
    }

    public async Task<RemoteScriptExecutionResult> RunScriptAsync(
        RemoteScriptSuite suite,
        RemoteScriptFile scriptFile,
        ConnectionScriptBinding binding,
        CancellationToken cancellationToken = default)
    {
        var errors = RemoteScriptLauncher.ValidateBinding(suite, binding);
        if (errors.Count > 0)
            throw new InvalidOperationException(string.Join(Environment.NewLine, errors));

        await WaitUntilConnectedAsync(cancellationToken);
        await _scriptLock.WaitAsync(cancellationToken);

        try
        {
            if (_disposed || _shellClosed || _shell is null)
                throw new InvalidOperationException("SSH terminal is not connected.");

            var startedAt = DateTimeOffset.Now;
            var payload = RemoteScriptLauncher.BuildPayload(suite, scriptFile, binding);

            Dispatcher.UIThread.Post(() =>
            {
                FeedLine($"\r\n\u001b[36m[run script] {suite.Name}/{scriptFile.DisplayName}\u001b[0m");
                FeedLine("\u001b[33m[please wait] Sending the script through the interactive terminal. This can take a few seconds.\u001b[0m");
            });

            var result = await ExecuteRemotePayloadAsync(payload, cancellationToken);
            var finishedAt = DateTimeOffset.Now;

            Dispatcher.UIThread.Post(() =>
            {
                var color = result.ExitCode == 0 ? "32" : "31";
                FeedLine($"\u001b[{color}m[script exit {result.ExitCode}]\u001b[0m");
            });

            return new RemoteScriptExecutionResult(result.ExitCode, startedAt, finishedAt);
        }
        finally
        {
            _scriptLock.Release();
        }
    }

    public async Task<PublicKeyInstallResult> InstallPublicKeyAsync(
        string publicKeyText,
        CancellationToken cancellationToken = default)
    {
        await WaitUntilConnectedAsync(cancellationToken);
        await _scriptLock.WaitAsync(cancellationToken);

        try
        {
            if (_disposed || _shellClosed || _shell is null)
                throw new InvalidOperationException("SSH terminal is not connected.");

            var payload = PublicKeyInstaller.BuildTerminalPayload(publicKeyText);

            Dispatcher.UIThread.Post(() =>
            {
                FeedLine("\r\n\u001b[36m[copy public key]\u001b[0m");
                FeedLine("\u001b[33m[please wait] Sending commands through the interactive terminal. This can take a few seconds.\u001b[0m");
            });

            var result = await ExecuteRemotePayloadAsync(payload, cancellationToken);
            if (result.ExitCode != 0)
            {
                Dispatcher.UIThread.Post(() =>
                    FeedLine($"\u001b[31m[copy public key exit {result.ExitCode}]\u001b[0m"));
                throw new InvalidOperationException($"Remote command exited with code {result.ExitCode}.");
            }

            Dispatcher.UIThread.Post(() =>
                FeedLine("\u001b[32m[copy public key complete]\u001b[0m"));
            return new PublicKeyInstallResult(
                result.Output.Contains(PublicKeyInstaller.TerminalAlreadyPresentLine, StringComparison.Ordinal),
                result.Output);
        }
        finally
        {
            _scriptLock.Release();
        }
    }

    /// <summary>
    /// Moves keyboard focus into the inner terminal control. Posted on the UI
    /// thread so it runs after the view is (re)attached on a tab switch — focusing
    /// the UserControl itself would not forward focus to the terminal.
    /// </summary>
    public void FocusTerminal() =>
        Dispatcher.UIThread.Post(() => Term.Focus(), DispatcherPriority.Background);

    public void ShowScriptPanel()
    {
        ScriptPanelOverlay.IsVisible = true;
    }

    public void HideScriptPanel()
    {
        ScriptPanelOverlay.IsVisible = false;
    }

    /// <summary>Sets the terminal font size in points.</summary>
    public void SetFontSize(double size)
    {
        Term.FontSize = size;
        // A font size change resizes the character cell, so the column/row geometry
        // changes too. Push the new size to the remote after the control re-measures
        // rather than relying on it raising SizeChanged for a font-only change.
        // SyncWindowSize no-ops while _shell is null (e.g. the pre-Start call).
        Dispatcher.UIThread.Post(SyncWindowSize, DispatcherPriority.Background);
    }

    private async Task ConnectAsync()
    {
        var connection = _connection!;
        var host = connection.Host.Trim();
        var port = connection.Port > 0 ? connection.Port : 22;
        FeedLine($"Connecting to {host}:{port} ...");

        SshClient client;
        try
        {
            // Build (which may query ssh-agent / Pageant over IPC) and Connect both
            // run on a background thread — those calls can block, and on the UI thread
            // would freeze the whole window.
            client = await Task.Run(() =>
            {
                var sshClient = new SshClient(SshConnectionFactory.Build(connection));
                SshHostKey.Attach(sshClient, host, port,
                onUnknown: (keyType, fingerprint) => HostKeyDialog.PromptTrust(host, port, keyType, fingerprint),
                onRejected: message => Dispatcher.UIThread.Post(() => FeedLine($"\r\n[31m[{message}][0m\r\n")));
                sshClient.Connect();
                return sshClient;
            });
        }
        catch (Exception ex)
        {
            _connected?.TrySetException(new InvalidOperationException($"Connection failed: {ex.Message}", ex));
            FeedLine($"[31m[connect failed] {ex.Message}[0m\r\n");
            return;
        }

        if (_disposed)
        {
            _connected?.TrySetCanceled();
            try { client.Dispose(); } catch { /* ignore */ }
            return;
        }

        _client = client;

        var cols = (uint)Math.Max(20, _model.Terminal.Cols);
        var rows = (uint)Math.Max(5, _model.Terminal.Rows);
        var terminalType = string.IsNullOrWhiteSpace(connection.TerminalType)
            ? Connection.DefaultTerminalType
            : connection.TerminalType.Trim();
        var shell = client.CreateShellStream(terminalType, cols, rows, 0, 0, 4096);
        _shell = shell;

        shell.DataReceived += (_, e) => OnShellData(e.Data);
        shell.ErrorOccurred += (_, e) => Dispatcher.UIThread.Post(() =>
            FeedLine($"\r\n[31m[error] {e.Exception.Message}[0m"));
        shell.Closed += (_, _) =>
        {
            _shellClosed = true;
            _activePayloadMonitor?.Fail(new InvalidOperationException("SSH terminal closed during script execution."));
            Dispatcher.UIThread.Post(() =>
                FeedLine("\r\n[33m[session closed][0m\r\n"));
        };

        _connected?.TrySetResult(true);

        // Push the current (laid-out) size in case SizeChanged fired before connect.
        SyncWindowSize();
    }

    private void OnShellData(byte[] data)
    {
        if (_disposed || data.Length == 0)
            return;
        var displayData = _activePayloadMonitor?.Append(data) ?? data;
        if (displayData.Length == 0)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed)
                _model.Feed(displayData, displayData.Length);
        });
    }

    private void SendToShell(ReadOnlyMemory<byte> data)
    {
        var shell = _shell;
        if (shell is null || _disposed || data.IsEmpty)
            return;
        try
        {
            WriteToShell(data.ToArray());
        }
        catch
        {
            // Best-effort; a closed/broken stream is reported via Closed/ErrorOccurred.
        }
    }

    private void WriteToShell(string text) => WriteToShell(Encoding.UTF8.GetBytes(text));

    private async Task<RemotePayloadResult> ExecuteRemotePayloadAsync(
        string payload,
        CancellationToken cancellationToken)
    {
        var shell = _shell;
        if (shell is null || _disposed || _shellClosed)
            throw new InvalidOperationException("SSH terminal is not connected.");

        var interactivePayload = InteractiveShellPayloadRunner.Build(payload);
        var monitor = new InteractiveShellPayloadMonitor(interactivePayload);
        _activePayloadMonitor = monitor;
        _suppressUserInput = true;

        try
        {
            var result = await InteractiveShellPayloadRunner.RunAsync(
                interactivePayload,
                monitor,
                WriteToShell,
                cancellationToken).ConfigureAwait(false);
            return new RemotePayloadResult(result.ExitCode, result.Output);
        }
        catch
        {
            TryRestoreShellEcho();
            throw;
        }
        finally
        {
            if (ReferenceEquals(_activePayloadMonitor, monitor))
                _activePayloadMonitor = null;
            _suppressUserInput = false;
        }
    }

    private void WriteToShell(byte[] data)
    {
        var shell = _shell;
        if (shell is null || _disposed || _shellClosed || data.Length == 0)
            return;

        lock (_shellWriteGate)
        {
            shell.Write(data, 0, data.Length);
            shell.Flush();
        }
    }

    private void TryRestoreShellEcho()
    {
        try
        {
            WriteToShell(InteractiveShellPayloadRunner.RestoreEchoCommand);
        }
        catch
        {
            // Best-effort only; the shell may already be closed.
        }
    }

    private void SyncWindowSize()
    {
        var shell = _shell;
        if (shell is null || _disposed)
            return;
        try
        {
            var cols = (uint)Math.Max(20, _model.Terminal.Cols);
            var rows = (uint)Math.Max(5, _model.Terminal.Rows);
            shell.ChangeWindowSize(cols, rows, 0, 0);
        }
        catch
        {
            // Ignore resize failures on a closing stream.
        }
    }

    private void FeedLine(string text) => _model.Feed(text + "\r\n");

    /// <summary>Tears down the SSH session. Safe to call multiple times.</summary>
    public void Close()
    {
        _disposed = true;
        _connected?.TrySetCanceled();
        _activePayloadMonitor?.Fail(new ObjectDisposedException(nameof(TerminalView)));
        try { _shell?.Dispose(); } catch { /* ignore */ }
        try { _client?.Disconnect(); } catch { /* ignore */ }
        try { _client?.Dispose(); } catch { /* ignore */ }
        _shell = null;
        _client = null;
    }
}
