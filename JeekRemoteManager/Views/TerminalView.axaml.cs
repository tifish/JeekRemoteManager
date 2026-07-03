using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using JeekRemoteManager.Models;
using JeekRemoteManager.Services;
using JeekRemoteManager.ViewModels;
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
    private readonly object _zmodemDetectionGate = new();
    private readonly ZmodemTriggerDetector _zmodemDetector = new();
    private InteractiveShellPayloadMonitor? _activePayloadMonitor;
    private Timer? _zmodemDetectionFlushTimer;
    private ZmodemByteQueue? _activeZmodemQueue;
    private ZmodemTraceLog? _activeZmodemTrace;
    private CancellationTokenSource? _activeZmodemCancellation;
    private int _connectionGeneration;
    private bool _connectInProgress;
    private bool _shellClosed;
    private bool _suppressUserInput;
    private string? _pendingKeyboardCopyText;
    private AgentChatViewModel? _aiViewModel;
    private double _aiPanelWidth = 380;
    private volatile bool _disposed;

    // The terminal and AI panel live in grid columns 0 and 2; named ColumnDefinitions don't
    // generate fields, so reach them through the grid.
    private ColumnDefinition TerminalColumn => RootGrid.ColumnDefinitions[0];
    private ColumnDefinition AiColumn => RootGrid.ColumnDefinitions[2];

    private sealed record RemotePayloadResult(int ExitCode, string Output);

    // Strips ANSI escape sequences (CSI colors/cursor, OSC titles, other Fe escapes) and
    // stray control characters so captured shell output reads as plain text in the AI panel
    // and when fed back to the model. Keeps tab/newline.
    private static readonly Regex AnsiAndControlChars = new(
        "\u001b\\[[0-9;?]*[ -/]*[@-~]" +
        "|\u001b\\][\\s\\S]*?(?:\u0007|\u001b\\\\)" +
        "|\u001b[@-_]" +
        "|[\u0000-\u0008\u000b\u000c\u000e-\u001f\u007f]",
        RegexOptions.Compiled);

    private static string CleanShellOutput(string text)
    {
        text = AnsiAndControlChars.Replace(text, string.Empty);
        return text.Replace("\r\n", "\n").Replace('\r', '\n');
    }

    /// <summary>Raised on the UI thread when the remote shell sets its title (OSC).</summary>
    public event EventHandler<string>? TitleChanged;

    public Connection? Connection => _connection;

    public string? SourcePath => _sourcePath;

    public bool CanReuseSession => !_disposed && (_connectInProgress || IsConnected);

    private bool IsConnected =>
        _shell is not null
        && !_shellClosed
        && _client?.IsConnected == true;

    public TerminalView()
    {
        InitializeComponent();

        Term.Model = _model;
        Term.ContextRequested += OnTerminalContextRequested;
        Term.AddHandler(InputElement.KeyDownEvent, OnTerminalPreviewKeyDown, RoutingStrategies.Tunnel);
        Term.AddHandler(InputElement.KeyUpEvent, OnTerminalPreviewKeyUp, RoutingStrategies.Tunnel);

        // Persist the AI panel width whenever the user finishes dragging the splitter.
        AiSplitter.DragCompleted += (_, _) => PersistAiPanelWidth();

        _model.UserInput += (_, e) =>
        {
            if (!_suppressUserInput)
                HandleUserInput(e.Data);
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
        FocusTerminal();
        BeginConnectionAttempt();
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

            var color = result.ExitCode == 0 ? "32" : "31";
            await FeedCompletionLineAndRefreshPromptAsync($"\u001b[{color}m[script exit {result.ExitCode}]\u001b[0m");

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
                await FeedCompletionLineAndRefreshPromptAsync($"\u001b[31m[copy public key exit {result.ExitCode}]\u001b[0m");
                throw new InvalidOperationException($"Remote command exited with code {result.ExitCode}.");
            }

            await FeedCompletionLineAndRefreshPromptAsync("\u001b[32m[copy public key complete]\u001b[0m");
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

    /// <summary>Shows or hides the AI assistant side panel for this terminal, creating its
    /// per-connection chat session on first open.</summary>
    public void ToggleAiPanel()
    {
        if (_aiViewModel is null)
            _aiViewModel = CreateAgentChatViewModel();

        var show = !AiPanelHost.IsVisible;
        if (!show)
            PersistAiPanelWidth();

        AiPanelHost.IsVisible = show;
        ApplyAiPanelLayout();

        if (show)
            Dispatcher.UIThread.Post(() => AiPanel.FocusInput(), DispatcherPriority.Background);
        else
            FocusTerminal();
    }

    /// <summary>
    /// Lays out the terminal/AI columns for the current panel state. Three states: panel
    /// hidden (terminal full width), side panel (terminal + splitter + fixed-width panel),
    /// and agent mode (terminal fully hidden, panel takes the whole tab).
    /// </summary>
    private void ApplyAiPanelLayout()
    {
        var show = AiPanelHost.IsVisible;
        var agentMode = show && _aiViewModel?.AgentMode == true;

        // Hiding the terminal control (not just collapsing the column) keeps its pty size
        // stable, so remote command output isn't rewrapped to a zero-width window.
        Term.IsVisible = !agentMode;
        TerminalColumn.MinWidth = agentMode ? 0 : 200;
        TerminalColumn.Width = agentMode
            ? new GridLength(0, GridUnitType.Pixel)
            : new GridLength(1, GridUnitType.Star);

        AiSplitter.IsVisible = show && !agentMode;

        if (!show)
        {
            // Collapse the column so it leaves no gap.
            AiColumn.MinWidth = 0;
            AiColumn.Width = new GridLength(0, GridUnitType.Pixel);
        }
        else if (agentMode)
        {
            AiColumn.MinWidth = 0;
            AiColumn.Width = new GridLength(1, GridUnitType.Star);
        }
        else
        {
            // Open at the remembered width (shared across tabs, persisted across runs).
            _aiPanelWidth = Math.Clamp(
                (DataContext as MainWindowViewModel)?.AiPanelWidth ?? _aiPanelWidth, 240, 1200);
            AiColumn.MinWidth = 240;
            AiColumn.Width = new GridLength(_aiPanelWidth, GridUnitType.Pixel);
        }
    }

    private void PersistAiPanelWidth()
    {
        if (!AiColumn.Width.IsAbsolute || AiColumn.Width.Value <= 0)
            return;

        _aiPanelWidth = AiColumn.Width.Value;
        if (DataContext is MainWindowViewModel vm)
            vm.AiPanelWidth = _aiPanelWidth;
    }

    private AgentChatViewModel CreateAgentChatViewModel()
    {
        var claudePath = AgentCliLocator.FindClaude();
        var codexPath = AgentCliLocator.FindCodex();
        var workingDir = Path.Combine(Path.GetTempPath(), "JeekRemoteManager", "agent");
        var systemPrompt = BuildAssistantSystemPrompt();

        var providers = new List<AgentProvider>
        {
            AgentChatViewModel.CreateClaudeProvider(claudePath is null
                ? null
                : (model, effort) => new ClaudeChatSession(claudePath, workingDir, systemPrompt, model: model, effort: effort)),
            AgentChatViewModel.CreateCodexProvider(
                codexPath is null
                    ? null
                    : (model, effort) => new CodexChatSession(codexPath, workingDir, systemPrompt, model: model, effort: effort),
                codexPath is null
                    ? null
                    : () => CodexChatSession.ListModelsCachedAsync(codexPath)),
        };

        var vm = new AgentChatViewModel(
            providers,
            readSelection: () => Term.HasSelection ? GetTerminalSelectionText(Term.SelectedText) : null,
            runCaptured: RunCapturedAsync,
            initialOptions: (DataContext as MainWindowViewModel)?.AiPanelOptions,
            persistOptions: options =>
            {
                if (DataContext is MainWindowViewModel mainVm)
                    mainVm.AiPanelOptions = options;
            });

        // Relayout when the user flips agent mode: remember the side-panel width first (a
        // no-op when the column is already star-sized), then switch the columns over.
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AgentChatViewModel.AgentMode))
            {
                PersistAiPanelWidth();
                ApplyAiPanelLayout();
            }
        };

        AiPanel.DataContext = vm;
        return vm;
    }

    private string BuildAssistantSystemPrompt()
    {
        var name = _connection?.Name;
        var host = _connection?.Host;
        var target = !string.IsNullOrWhiteSpace(name)
            ? $"\"{name}\" ({host})"
            : host ?? "a remote server";

        return
            $"You are an assistant embedded inside JeekRemoteManager, operating an interactive SSH " +
            $"terminal connected to {target}. " +
            "To run a shell command on the server, output exactly ONE ```bash fenced code block " +
            "containing a single non-interactive command (or short script). It will be executed on the " +
            "server automatically, WITHOUT asking the user, and its output returned to you as the next " +
            "message. Run one command at a time and wait for its output before deciding the next step. " +
            "When the task is done, reply with a short summary and NO command block. " +
            "Commands run without confirmation, so avoid destructive actions unless explicitly asked, and " +
            "prefer non-interactive flags (e.g. `-y`). Assume a Linux server unless told otherwise.";
    }

    /// <summary>
    /// Runs a command on the server's interactive shell and returns its captured output
    /// (exit code + stdout/stderr), so the AI assistant can act on the result. Reuses the
    /// same payload runner as script execution, and echoes the command into the terminal so
    /// the user sees exactly what the assistant ran.
    /// </summary>
    internal async Task<string> RunCapturedAsync(string command, CancellationToken cancellationToken)
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
        try
        {
            if (_disposed || _shellClosed || _shell is null)
                return "[not connected]";

            Dispatcher.UIThread.Post(() =>
                FeedLine($"\r\n\u001b[35m[AI]\u001b[0m $ {command}"));

            var result = await ExecuteRemotePayloadAsync(command, cancellationToken);
            await FeedCompletionLineAndRefreshPromptAsync(
                $"\u001b[35m[AI exit {result.ExitCode}]\u001b[0m");

            var output = CleanShellOutput(result.Output ?? string.Empty).Trim();
            return $"[exit {result.ExitCode}]\n{output}";
        }
        catch (OperationCanceledException)
        {
            return "[command cancelled or timed out]";
        }
        catch (Exception ex)
        {
            return $"[command failed: {ex.Message}]";
        }
        finally
        {
            _scriptLock.Release();
        }
    }

    private void HandleUserInput(ReadOnlyMemory<byte> data)
    {
        if (CanReconnectFromInput(data))
        {
            Reconnect();
            return;
        }

        SendToShell(data);
    }

    private bool CanReconnectFromInput(ReadOnlyMemory<byte> data) =>
        _connection is not null
        && !_disposed
        && !_connectInProgress
        && !IsConnected
        && IsEnterInput(data);

    private static bool IsEnterInput(ReadOnlyMemory<byte> data)
    {
        if (data.IsEmpty)
            return false;

        var span = data.Span;
        for (var i = 0; i < span.Length; i++)
        {
            if (span[i] != (byte)'\r' && span[i] != (byte)'\n')
                return false;
        }

        return true;
    }

    private void Reconnect()
    {
        if (_connection is null || _disposed || _connectInProgress || IsConnected)
            return;

        _activePayloadMonitor?.Fail(new InvalidOperationException("SSH terminal is reconnecting."));
        _activePayloadMonitor = null;
        _suppressUserInput = false;
        Interlocked.Increment(ref _connectionGeneration);
        DisposeTransport();

        FeedLine("\r\n\u001b[36m[reconnect]\u001b[0m");
        BeginConnectionAttempt();
    }

    private void BeginConnectionAttempt()
    {
        var generation = Interlocked.Increment(ref _connectionGeneration);
        _connected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _shellClosed = false;
        _ = ConnectAsync(generation);
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

    private async Task ConnectAsync(int generation)
    {
        var connection = _connection!;
        var host = connection.Host.Trim();
        var port = connection.Port > 0 ? connection.Port : 22;
        FeedLine($"Connecting to {host}:{port} ...");

        SshClient client;
        _connectInProgress = true;
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
                onRejected: message => Dispatcher.UIThread.Post(() => FeedLine($"\r\n\u001b[31m[{message}]\u001b[0m\r\n")));
                sshClient.Connect();
                return sshClient;
            });
        }
        catch (Exception ex)
        {
            _connected?.TrySetException(new InvalidOperationException($"Connection failed: {ex.Message}", ex));
            _connectInProgress = false;
            FeedLine($"\u001b[31m[connect failed] {ex.Message}\u001b[0m");
            FeedReconnectHint();
            return;
        }

        if (_disposed || generation != _connectionGeneration)
        {
            _connected?.TrySetCanceled();
            _connectInProgress = false;
            try { client.Dispose(); } catch { /* ignore */ }
            return;
        }

        var cols = (uint)Math.Max(20, _model.Terminal.Cols);
        var rows = (uint)Math.Max(5, _model.Terminal.Rows);
        var terminalType = string.IsNullOrWhiteSpace(connection.TerminalType)
            ? Connection.DefaultTerminalType
            : connection.TerminalType.Trim();

        ShellStream shell;
        try
        {
            shell = client.CreateShellStream(terminalType, cols, rows, 0, 0, 4096);
        }
        catch (Exception ex)
        {
            _connected?.TrySetException(new InvalidOperationException($"Connection failed: {ex.Message}", ex));
            _connectInProgress = false;
            FeedLine($"\u001b[31m[connect failed] {ex.Message}\u001b[0m");
            FeedReconnectHint();
            try { client.Dispose(); } catch { /* ignore */ }
            return;
        }

        _client = client;
        _shell = shell;

        shell.DataReceived += (_, e) =>
        {
            if (generation == _connectionGeneration)
                OnShellData(e.Data);
        };
        shell.ErrorOccurred += (_, e) => Dispatcher.UIThread.Post(() =>
        {
            if (generation == _connectionGeneration && !_disposed)
                FeedLine($"\r\n\u001b[31m[error] {e.Exception.Message}\u001b[0m");
        });
        shell.Closed += (_, _) =>
        {
            if (generation != _connectionGeneration || _disposed)
                return;

            _shellClosed = true;
            _activePayloadMonitor?.Fail(new InvalidOperationException("SSH terminal closed during script execution."));
            Dispatcher.UIThread.Post(() =>
            {
                FeedLine("\r\n\u001b[33m[session closed]\u001b[0m");
                FeedReconnectHint();
            });
        };

        _connected?.TrySetResult(true);
        _connectInProgress = false;

        // Push the current (laid-out) size in case SizeChanged fired before connect.
        SyncWindowSize();
    }

    private void OnShellData(byte[] data)
    {
        if (_disposed || data.Length == 0)
            return;

        if (_activeZmodemQueue is { } zmodemQueue)
        {
            _activeZmodemTrace?.WriteBytes("RX raw", data);
            zmodemQueue.Append(data);
            return;
        }

        if (_activePayloadMonitor is not null)
        {
            var payloadDisplayData = _activePayloadMonitor.Append(data);
            FeedBytes(payloadDisplayData);
            return;
        }

        ZmodemDetection? detection;
        byte[] displayData;
        lock (_zmodemDetectionGate)
        {
            detection = _zmodemDetector.Append(data, out displayData);
        }

        FeedBytes(displayData);
        if (detection is not null)
        {
            FeedBytes(detection.DisplayBytes);
            StartZmodemTransfer(detection);
            return;
        }

        ScheduleZmodemDetectionFlush();
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

    private async void OnTerminalContextRequested(object? sender, TerminalContextRequestedEventArgs e)
    {
        if (e.HasSelection)
        {
            await CopyTerminalSelectionToClipboardAsync(e.SelectedText);
            _model.ClearSelection();
            return;
        }

        await Term.PasteFromClipboardAsync();
    }

    private async void OnTerminalPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (Term.HasSelection && IsTerminalCopyGestureKey(e.Key))
            _pendingKeyboardCopyText = GetTerminalSelectionText(Term.SelectedText);

        if (IsTerminalPasteGesture(e))
        {
            e.Handled = true;
            _pendingKeyboardCopyText = null;
            await Term.PasteFromClipboardAsync();
            return;
        }

        if (!IsTerminalCopyGesture(e))
        {
            return;
        }

        var text = Term.HasSelection
            ? GetTerminalSelectionText(Term.SelectedText)
            : _pendingKeyboardCopyText;
        if (string.IsNullOrEmpty(text))
            return;

        e.Handled = true;
        _pendingKeyboardCopyText = null;
        await SetTerminalClipboardTextAsync(text);
    }

    private void OnTerminalPreviewKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift)
            _pendingKeyboardCopyText = null;
    }

    private Task CopyTerminalSelectionToClipboardAsync(string selectedText)
    {
        var text = GetTerminalSelectionText(selectedText);
        return SetTerminalClipboardTextAsync(text);
    }

    private string GetTerminalSelectionText(string selectedText) =>
        TerminalClipboardText.BuildSelectedTextWithoutSoftWraps(_model.Terminal) ?? selectedText;

    private Task SetTerminalClipboardTextAsync(string text)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        return clipboard?.SetTextAsync(text) ?? Task.CompletedTask;
    }

    private static bool IsTerminalCopyGesture(KeyEventArgs e) =>
        e.Key == Key.C
        && e.KeyModifiers.HasFlag(KeyModifiers.Control)
        && e.KeyModifiers.HasFlag(KeyModifiers.Shift);

    private static bool IsTerminalPasteGesture(KeyEventArgs e) =>
        e.Key == Key.V
        && e.KeyModifiers.HasFlag(KeyModifiers.Control)
        && e.KeyModifiers.HasFlag(KeyModifiers.Shift);

    private static bool IsTerminalCopyGestureKey(Key key) =>
        key is Key.C or Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift;

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

    private void FeedBytes(byte[] data)
    {
        if (data.Length == 0)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed)
                _model.Feed(data, data.Length);
        });
    }

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
                FeedLineOnUiThread("\r\n\u001b[36m[zmodem download]\u001b[0m Choose a local folder.");
                var folder = await PickZmodemDownloadFolderAsync().ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(folder))
                {
                    await session.CancelAsync(CancellationToken.None).ConfigureAwait(false);
                    FeedLineOnUiThread("\u001b[33m[zmodem cancelled]\u001b[0m");
                    return;
                }

                FeedLineOnUiThread($"\u001b[36m[zmodem download]\u001b[0m Receiving to {folder}");
                var result = await session.ReceiveAsync(folder, cancellation.Token).ConfigureAwait(false);
                FeedZmodemComplete(direction, result.Files);
                return;
            }

            FeedLineOnUiThread("\r\n\u001b[36m[zmodem upload]\u001b[0m Choose local file(s).");
            var files = await PickZmodemUploadFilesAsync().ConfigureAwait(false);
            if (files.Count == 0)
            {
                await session.CancelAsync(CancellationToken.None).ConfigureAwait(false);
                FeedLineOnUiThread("\u001b[33m[zmodem cancelled]\u001b[0m");
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
        }
        catch (OperationCanceledException)
        {
            trace?.Write("operation cancelled");
            FeedLineOnUiThread("\u001b[33m[zmodem cancelled]\u001b[0m");
        }
        catch (Exception ex)
        {
            trace?.WriteException(ex);
            try { await session.CancelAsync(CancellationToken.None).ConfigureAwait(false); } catch { /* ignore */ }
            FeedLineOnUiThread($"\u001b[31m[zmodem failed] {ex.Message}\u001b[0m");
            if (trace is not null)
                FeedLineOnUiThread($"\u001b[31m[zmodem trace] {trace.FilePath}\u001b[0m");
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
    }

    private void FeedLineOnUiThread(string text) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed)
                FeedLine(text);
        });

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

    private async Task FeedCompletionLineAndRefreshPromptAsync(string text)
    {
        await Dispatcher.UIThread.InvokeAsync(() => FeedLine("\r\n" + text));
        TryRefreshShellPrompt();
    }

    private void TryRefreshShellPrompt()
    {
        try
        {
            WriteToShell(Encoding.UTF8.GetBytes("\n"));
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

    private void FeedReconnectHint() => FeedLine("\u001b[90m[press Enter to reconnect]\u001b[0m");

    private void DisposeTransport()
    {
        try { _shell?.Dispose(); } catch { /* ignore */ }
        try { _client?.Disconnect(); } catch { /* ignore */ }
        try { _client?.Dispose(); } catch { /* ignore */ }
        _shell = null;
        _client = null;
    }

    /// <summary>Tears down the SSH session. Safe to call multiple times.</summary>
    public void Close()
    {
        _disposed = true;
        Interlocked.Increment(ref _connectionGeneration);
        _connectInProgress = false;
        _connected?.TrySetCanceled();
        _activePayloadMonitor?.Fail(new ObjectDisposedException(nameof(TerminalView)));
        _activeZmodemCancellation?.Cancel();
        _activeZmodemQueue?.Complete(new ObjectDisposedException(nameof(TerminalView)));
        _activeZmodemTrace?.Dispose();
        _zmodemDetectionFlushTimer?.Dispose();
        DisposeTransport();

        var ai = _aiViewModel;
        _aiViewModel = null;
        if (ai is not null)
            _ = ai.DisposeAsync();
    }
}
