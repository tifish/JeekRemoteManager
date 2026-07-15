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
using Avalonia.VisualTree;
using JeekRemoteManager.Models;
using JeekRemoteManager.Services;
using JeekRemoteManager.ViewModels;
using Renci.SshNet;
using SvcSystems.UI.Terminal;

namespace JeekRemoteManager.Views;

/// <summary>
/// Hosts a native Avalonia terminal control (SvcSystems.UI.Terminal) driven by an
/// <see cref="ITerminalChannel"/> — an SSH.NET interactive shell or a local WSL
/// (ConPTY) process. Self-contained and reusable: a window can host one for
/// the Phase A spike, and the right-pane tab UI (Phase B) hosts one per tab.
/// SSH authentication is programmatic via <see cref="SshConnectionFactory"/> (the
/// user never types a password); live bytes, keyboard, title, and window size are
/// wired to the channel.
/// </summary>
public partial class TerminalView : UserControl
{
    private const int ResizeOutputInitialWaitMs = 500;
    private const int ResizeOutputQuietPeriodMs = 150;
    private const int ResizeOutputHardLimitMs = 800;

    private readonly TerminalControlModel _model = new(new TerminalOptions
    {
        Cols = 120,
        Rows = 30,
        // Disable resize reflow so full-screen TUIs (top, vim, mc) stay stable.
        ReflowOnResize = false,
    });

    private Connection? _connection;
    private SharedSshClient? _client;
    private SharedSshClient? _pendingSharedClient;
    private ITerminalChannel? _channel;
    private string? _sourcePath;
    private TaskCompletionSource<bool>? _connected;
    private readonly SemaphoreSlim _scriptLock = new(1, 1);
    private readonly object _shellWriteGate = new();
    private readonly object _zmodemDetectionGate = new();
    private readonly ZmodemTriggerDetector _zmodemDetector = new();
    private InteractiveShellPayloadMonitor? _activePayloadMonitor;
    private InteractiveShellPayloadMonitor? _manualRecoveryMonitor;
    private Timer? _zmodemDetectionFlushTimer;
    private ZmodemByteQueue? _activeZmodemQueue;
    private ZmodemTraceLog? _activeZmodemTrace;
    private CancellationTokenSource? _activeZmodemCancellation;
    private IReadOnlyList<string>? _pendingDropUploadFiles;
    private int _dropUploadGeneration;

    // AI-panel initiated transfers: a pre-chosen download folder skips the folder picker,
    // and the completion source carries the outcome back to the waiting AI request.
    private string? _pendingDownloadFolder;
    private int _downloadRequestGeneration;
    private TaskCompletionSource<string>? _aiTransferCompletion;
    private int _connectionGeneration;
    private long _lastShellDataTicks;
    // Set while a "#input" login-command directive is waiting for the user to
    // type something (e.g. a 2FA code) and press Enter; completed from HandleUserInput.
    private TaskCompletionSource? _loginManualInputTcs;
    private bool _isDuplicatedSession;
    private bool _connectInProgress;
    private bool _shellClosed;
    private bool _suppressUserInput;
    private int _lastFedCursorRow;
    private int _lastFedAbsoluteCursorRow;
    private DispatcherTimer? _windowSizeSyncTimer;
    private (uint Cols, uint Rows)? _lastSentWindowSize;
    private readonly TerminalResizeOutputBuffer _resizeOutputBuffer = new();
    private Timer? _resizeOutputFlushTimer;
    private long _resizeOutputDeadlineTicks;
    private string? _pendingKeyboardCopyText;
    private AgentChatViewModel? _aiViewModel;
    private double _aiPanelWidth = 380;
    private FileBrowserViewModel? _fileBrowserViewModel;
    private double _fileBrowserHeight = 260;
    private ServerMonitorViewModel? _monitorViewModel;
    private double _monitorPanelWidth = 260;
    private MainWindowViewModel? _customProvidersOwner;
    private Action? _customProvidersChangedHandler;
    private int _isAiCommandRunning;
    private long _aiCommandExecutionCount;
    private long _aiCommandCompletionCount;
    private long _terminalRecoveryCount;
    private Task<bool>? _aiAutoReconnectTask;
    private long _aiAutoReconnectAttemptCount;
    private long _aiAutoReconnectSuccessCount;
    private string _aiAutoReconnectState = "idle";
    private volatile bool _disposed;

    // The AI panel, terminal, and monitor panel live in grid columns 0, 2, and 4; named
    // ColumnDefinitions don't generate fields, so reach them through the grid.
    private ColumnDefinition AiColumn => RootGrid.ColumnDefinitions[0];
    private ColumnDefinition TerminalColumn => RootGrid.ColumnDefinitions[2];
    private ColumnDefinition MonitorColumn => RootGrid.ColumnDefinitions[4];

    // The file browser lives in row 2 of the terminal-area grid.
    private RowDefinition FileBrowserRow => RootGrid.RowDefinitions[2];

    private sealed record RemotePayloadResult(int ExitCode, string Output);

    private sealed class ShellRecoveryRequestedException(string message)
        : OperationCanceledException(message);

    private sealed class TerminalConnectionLostException()
        : InvalidOperationException("Server connection was lost during terminal command execution.");

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

    /// <summary>The lazily-created AI panel view model, exposed for Debug MCP verification.</summary>
    public AgentChatViewModel? AiViewModel => _aiViewModel;

    public string? SourcePath => _sourcePath;

    public bool CanReuseSession => !_disposed && (_connectInProgress || IsConnected);

    /// <summary>Live AI shell-runner state exposed for Debug MCP verification.</summary>
    public bool IsAiCommandRunning => Volatile.Read(ref _isAiCommandRunning) != 0;

    /// <summary>True while any captured terminal payload owns the interactive shell.</summary>
    public bool IsTerminalCommandRunning => Volatile.Read(ref _activePayloadMonitor) is not null;

    public bool IsCommandLockAvailable => _scriptLock.CurrentCount > 0;

    public bool IsUserInputSuppressed => _suppressUserInput;

    public bool IsTerminalConnected => IsConnected;

    public int ConnectionGeneration => Volatile.Read(ref _connectionGeneration);

    public long AiCommandExecutionCount => Interlocked.Read(ref _aiCommandExecutionCount);

    public long AiCommandCompletionCount => Interlocked.Read(ref _aiCommandCompletionCount);

    public long TerminalRecoveryCount => Interlocked.Read(ref _terminalRecoveryCount);

    /// <summary>Latest AI-triggered automatic reconnect state, exposed for Debug MCP.</summary>
    public string AiAutoReconnectState => _aiAutoReconnectState;

    public bool IsAiAutoReconnectRunning =>
        Volatile.Read(ref _aiAutoReconnectTask) is { IsCompleted: false };

    public long AiAutoReconnectAttemptCount =>
        Interlocked.Read(ref _aiAutoReconnectAttemptCount);

    public long AiAutoReconnectSuccessCount =>
        Interlocked.Read(ref _aiAutoReconnectSuccessCount);

    private bool IsConnected =>
        _channel is not null
        && !_shellClosed
        && (_connection?.IsWsl == true || _client?.IsConnected == true);

    public TerminalView()
    {
        InitializeComponent();

        Term.Model = _model;
        Term.ContextRequested += OnTerminalContextRequested;
        Term.AddHandler(InputElement.KeyDownEvent, OnTerminalPreviewKeyDown, RoutingStrategies.Tunnel);
        Term.AddHandler(InputElement.KeyUpEvent, OnTerminalPreviewKeyUp, RoutingStrategies.Tunnel);

        DragDrop.SetAllowDrop(Term, true);
        Term.AddHandler(DragDrop.DragOverEvent, OnTerminalDragOver);
        Term.AddHandler(DragDrop.DropEvent, OnTerminalDrop);

        // TerminalControl owns UpdateUI; chain so the "scroll to latest" button tracks
        // viewport changes from feed, wheel, and scrollbar without a timer.
        var previousUpdateUi = _model.UpdateUI;
        _model.UpdateUI = () =>
        {
            previousUpdateUi?.Invoke();
            UpdateScrollToBottomButton();
        };

        // Persist the AI panel width whenever the user finishes dragging the splitter.
        AiSplitter.DragCompleted += (_, _) => PersistAiPanelWidth();
        FileSplitter.DragCompleted += (_, _) => PersistFileBrowserHeight();
        MonitorSplitter.DragCompleted += (_, _) => PersistMonitorPanelWidth();
        // The panels are only visible while open, so toggling from their close buttons hides them.
        MonitorPanel.CloseRequested += (_, _) => ToggleMonitorPanel();
        AiPanel.CloseRequested += (_, _) => ToggleAiPanel();
        FileBrowser.CloseRequested += (_, _) => ToggleFileBrowserPanel();

        _model.UserInput += (_, e) =>
        {
            if (!_suppressUserInput)
                HandleUserInput(e.Data);
        };
        _model.SizeChanged += (_, e) =>
        {
            RepairBufferAfterResize(e.Rows);
            RecordCursorRow();
            SyncWindowSize();
        };
        _model.Terminal.TitleChanged += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            var title = _model.Terminal.Title;
            if (!string.IsNullOrWhiteSpace(title))
                TitleChanged?.Invoke(this, title);
        });
    }

    /// <summary>True when the terminal viewport is not following the latest output
    /// and the floating jump-to-bottom control is shown. Public for Debug MCP.</summary>
    public bool IsScrollToBottomButtonVisible => ScrollToBottomButton.IsVisible;

    /// <summary>Scrolls the terminal to the live bottom and resumes following new output.
    /// Public for Debug MCP and the floating button.</summary>
    public void ScrollToLatest()
    {
        _model.EnsureCaretIsVisible();
        UpdateScrollToBottomButton();
    }

    private void OnScrollToBottomClick(object? sender, RoutedEventArgs e) => ScrollToLatest();

    private void UpdateScrollToBottomButton()
    {
        // Alternate-buffer TUIs have no useful scrollback; only offer the jump when
        // the normal buffer has history and the viewport is not already at the bottom.
        var show = !_disposed
                   && _model.CanScroll
                   && !_model.Terminal.Buffer.IsAtBottom;
        if (ScrollToBottomButton.IsVisible != show)
            ScrollToBottomButton.IsVisible = show;
    }

    /// <summary>
    /// Connects the given connection and starts streaming. Call once. When
    /// <paramref name="sharedClient"/> is provided (a duplicated tab), the view opens a
    /// new shell channel on that already-authenticated connection instead of dialing a
    /// new one; the reference must already be counted for this view (see
    /// <see cref="ShareClientForDuplicate"/>).
    /// </summary>
    public void Start(
        Connection connection,
        string? sourcePath = null,
        SharedSshClient? sharedClient = null,
        bool isDuplicatedSession = false)
    {
        _connection = connection;
        _sourcePath = sourcePath;
        _pendingSharedClient = sharedClient;
        _isDuplicatedSession = isDuplicatedSession;
        FocusTerminal();
        BeginConnectionAttempt();
    }

    /// <summary>
    /// Hands out this view's live SSH connection for a duplicated tab, taking a
    /// reference on the duplicate's behalf. Returns null when there is nothing
    /// usable to share (not connected, or already torn down) — the duplicate then
    /// falls back to a fresh connection.
    /// </summary>
    public SharedSshClient? ShareClientForDuplicate()
    {
        var client = _client;
        if (_disposed || client is null || !client.IsConnected)
            return null;
        return client.TryAddRef() ? client : null;
    }

    public async Task WaitUntilConnectedAsync(CancellationToken cancellationToken = default)
    {
        var connected = _connected ?? throw new InvalidOperationException("Terminal has not started.");
        await connected.Task.WaitAsync(cancellationToken);
    }

    // Read on the UI thread before starting a script, so each terminal rejects a
    // second concurrent script while other terminals keep running their own.
    public bool IsScriptRunning { get; private set; }

    public async Task<RemoteScriptExecutionResult> RunScriptAsync(
        RemoteScriptSuite suite,
        RemoteScriptFile scriptFile,
        ConnectionScriptBinding binding,
        CancellationToken cancellationToken = default)
    {
        var errors = RemoteScriptLauncher.ValidateBinding(suite, binding);
        if (errors.Count > 0)
            throw new InvalidOperationException(string.Join(Environment.NewLine, errors));

        IsScriptRunning = true;
        try
        {
            return await RunScriptCoreAsync(suite, scriptFile, binding, cancellationToken);
        }
        finally
        {
            IsScriptRunning = false;
        }
    }

    private async Task<RemoteScriptExecutionResult> RunScriptCoreAsync(
        RemoteScriptSuite suite,
        RemoteScriptFile scriptFile,
        ConnectionScriptBinding binding,
        CancellationToken cancellationToken)
    {
        await WaitUntilConnectedAsync(cancellationToken);
        await _scriptLock.WaitAsync(cancellationToken);

        try
        {
            if (_disposed || _shellClosed || _channel is null)
                throw new InvalidOperationException("Terminal is not connected.");

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
            if (_disposed || _shellClosed || _channel is null)
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

    /// <summary>Raised when one of the side panels (monitor, AI, file browser) is
    /// shown or hidden, so the main window can refresh its toggle-button states.</summary>
    public event EventHandler? PanelStateChanged;

    public bool IsMonitorPanelOpen => MonitorPanelHost.IsVisible;

    public bool IsAiPanelOpen => AiPanelHost.IsVisible;

    /// <summary>True while a login-command <c>#input</c> directive is waiting for Enter.</summary>
    public bool IsLoginManualInputPending => Volatile.Read(ref _loginManualInputTcs) is not null;

    /// <summary>Rendered terminal visibility exposed for Debug MCP verification.</summary>
    public bool IsTerminalAreaVisible => TerminalArea.IsVisible;

    /// <summary>Whether the AI panel currently owns the full tab.</summary>
    public bool IsAgentModeLayoutActive => ShouldUseAgentModeLayout(
        AiPanelHost.IsVisible,
        _aiViewModel?.AgentMode == true,
        IsLoginManualInputPending);

    /// <summary>Computes the effective Agent layout without changing the persisted preference.</summary>
    public static bool ShouldUseAgentModeLayout(
        bool aiPanelVisible,
        bool agentModeRequested,
        bool loginManualInputPending) =>
        aiPanelVisible && agentModeRequested && !loginManualInputPending;

    /// <summary>
    /// Submits one login-command answer through the real terminal input path. This is
    /// intentionally limited to an active <c>#input</c> wait for safe Debug MCP checks.
    /// </summary>
    public bool DebugSubmitLoginInput(string input)
    {
        if (!IsConnected || !IsLoginManualInputPending)
            return false;

        HandleUserInput(Encoding.UTF8.GetBytes((input ?? string.Empty) + "\r"));
        return true;
    }

    /// <summary>Rendered side-panel columns exposed for Debug MCP verification.</summary>
    public int AiPanelColumn => Grid.GetColumn(AiPanelHost);

    public int MonitorPanelColumn => Grid.GetColumn(MonitorPanelHost);

    public bool IsFileBrowserPanelOpen => FileBrowserHost.IsVisible;

    // Exposed through the generic Debug MCP object-path tools so resize behavior
    // can be checked against a running terminal without changing production UI.
    public bool IsResizeOutputCoalescing => _resizeOutputBuffer.IsActive;

    public int PendingResizeOutputByteCount => _resizeOutputBuffer.PendingByteCount;

    /// <summary>Current terminal cursor (relative Y, YBase, absolute YBase+Y, cols, rows).</summary>
    public (int Y, int YBase, int AbsoluteY, int Cols, int Rows) DebugTerminalCursorState
    {
        get
        {
            var buffer = _model.Terminal.Buffer;
            return (buffer.Y, buffer.YBase, buffer.YBase + buffer.Y, _model.Terminal.Cols, _model.Terminal.Rows);
        }
    }

    /// <summary>Plain text currently visible in the terminal viewport for Debug MCP checks.</summary>
    public string DebugVisibleTerminalText
    {
        get
        {
            var terminal = _model.Terminal;
            var buffer = terminal.Buffer;
            var firstRow = Math.Max(0, buffer.YDisp);
            var lastRow = Math.Min(buffer.Length, firstRow + terminal.Rows);
            var text = new StringBuilder();

            for (var row = firstRow; row < lastRow; row++)
            {
                if (row > firstRow)
                    text.Append('\n');
                if (buffer.GetLine(row) is { } line)
                    text.Append(line.TranslateToString(true));
            }

            return text.ToString().TrimEnd();
        }
    }

    /// <summary>
    /// Debug helper: resize the terminal buffer as a window size change would and
    /// apply the same cursor repair used on real SizeChanged events.
    /// </summary>
    public void DebugResizeBuffer(int cols, int rows)
    {
        cols = Math.Max(1, cols);
        rows = Math.Max(1, rows);
        RecordCursorRow();
        _model.Terminal.Resize(cols, rows);
        RepairBufferAfterResize(rows);
        RecordCursorRow();
        _model.UpdateDisplay();
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
        PanelStateChanged?.Invoke(this, EventArgs.Empty);

        if (show && IsLoginManualInputPending)
            FocusTerminal();
        else if (show)
            Dispatcher.UIThread.Post(() => AiPanel.FocusInput(), DispatcherPriority.Background);
        else
            FocusTerminal();
    }

    /// <summary>Shows or hides the SFTP file browser panel below the terminal, dialing
    /// its own SFTP connection on first open (lazy: tabs that never open it never pay).</summary>
    public void ToggleFileBrowserPanel()
    {
        if (_connection is null)
            return;

        _fileBrowserViewModel ??= CreateFileBrowserViewModel();

        var show = !FileBrowserHost.IsVisible;
        if (!show)
            PersistFileBrowserHeight();

        FileBrowserHost.IsVisible = show;
        FileSplitter.IsVisible = show;
        ApplyFileBrowserPlacement();
        PanelStateChanged?.Invoke(this, EventArgs.Empty);

        if (show)
        {
            // Open at the remembered height (shared across tabs, persisted across runs).
            _fileBrowserHeight = Math.Clamp(
                (DataContext as MainWindowViewModel)?.FileBrowserPanelHeight ?? _fileBrowserHeight, 120, 1600);
            FileBrowserRow.MinHeight = 120;
            FileBrowserRow.Height = new GridLength(_fileBrowserHeight, GridUnitType.Pixel);
            // Focus the list so typing locates files instead of reaching the shell.
            Dispatcher.UIThread.Post(() => FileBrowser.FocusList(), DispatcherPriority.Background);
            _ = LoadFileBrowserAndRefocusAsync(_fileBrowserViewModel);
        }
        else
        {
            // Collapse the row so it leaves no gap.
            FileBrowserRow.MinHeight = 0;
            FileBrowserRow.Height = new GridLength(0, GridUnitType.Pixel);
            if (IsAgentModeLayoutActive)
                Dispatcher.UIThread.Post(() => AiPanel.FocusInput(), DispatcherPriority.Background);
            else
                FocusTerminal();
        }
    }

    /// <summary>Waits for the panel's first directory listing, then focuses the list
    /// again so the first row is selected — unless the user has deliberately clicked
    /// back into the terminal in the meantime.</summary>
    private async Task LoadFileBrowserAndRefocusAsync(FileBrowserViewModel vm)
    {
        try
        {
            await vm.EnsureLoadedAsync();
        }
        catch
        {
            return;
        }

        if (_disposed || !FileBrowserHost.IsVisible)
            return;

        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        if (focused is Avalonia.Visual visual && Term.IsVisualAncestorOf(visual))
            return;

        FileBrowser.FocusList();
    }

    private FileBrowserViewModel CreateFileBrowserViewModel()
    {
        var connection = _connection!;
        string label;
        Func<IFileSystemSession> createSession;
        if (connection.IsWsl)
        {
            // An empty distro means the default one; resolve the actual name here
            // because the UNC share is addressed by name.
            var distro = connection.WslDistro.Trim();
            if (distro.Length == 0)
                distro = WslDistroService.ListDistros().FirstOrDefault(d => d.IsDefault)?.Name ?? "";
            label = distro.Length == 0 ? "WSL" : distro;
            var user = connection.Username.Trim();
            var resolvedDistro = distro;
            createSession = () => new WslFileSession(resolvedDistro, user);
        }
        else
        {
            var host = connection.Host.Trim();
            label = string.IsNullOrWhiteSpace(connection.Username)
                ? host
                : $"{connection.Username.Trim()}@{host}";
            // Each SFTP session dials its own connection; build fresh auth methods
            // per dial (they hold per-attempt state).
            createSession = () => new SftpSession(() => SshConnectionFactory.Build(connection));
        }

        var vm = new FileBrowserViewModel(
            createSession,
            path =>
            {
                // Ctrl+U discards anything half-typed at the prompt so `cd` runs clean.
                WriteToShell("\u0015cd " + QuoteForRemoteShell(path) + "\r");
                FocusTerminal();
            },
            label,
            () => (DataContext as MainWindowViewModel)?.FileBrowserEditorPath);
        FileBrowser.DataContext = vm;
        return vm;
    }

    private void PersistFileBrowserHeight()
    {
        if (!FileBrowserRow.Height.IsAbsolute || FileBrowserRow.Height.Value <= 0)
            return;

        _fileBrowserHeight = FileBrowserRow.Height.Value;
        if (DataContext is MainWindowViewModel vm)
            vm.FileBrowserPanelHeight = _fileBrowserHeight;
    }

    /// <summary>Shows or hides the server monitor panel right of the terminal. Sampling
    /// runs only while the panel is visible, over a hidden duplicated shell on this
    /// terminal's SSH connection — SSH-type connections only.</summary>
    public void ToggleMonitorPanel()
    {
        if (_connection is not { IsSsh: true })
            return;

        _monitorViewModel ??= CreateServerMonitorViewModel();

        var show = !MonitorPanelHost.IsVisible;
        if (!show)
            PersistMonitorPanelWidth();

        MonitorPanelHost.IsVisible = show;
        MonitorSplitter.IsVisible = show;
        PanelStateChanged?.Invoke(this, EventArgs.Empty);

        if (show)
        {
            // Open at the remembered width (shared across tabs, persisted across runs).
            _monitorPanelWidth = Math.Clamp(
                (DataContext as MainWindowViewModel)?.MonitorPanelWidth ?? _monitorPanelWidth, 180, 600);
            MonitorColumn.MinWidth = 180;
            MonitorColumn.Width = new GridLength(_monitorPanelWidth, GridUnitType.Pixel);
            _monitorViewModel.Start();
        }
        else
        {
            // Collapse the column so it leaves no gap.
            MonitorColumn.MinWidth = 0;
            MonitorColumn.Width = new GridLength(0, GridUnitType.Pixel);
            _monitorViewModel.Stop();
            FocusTerminal();
        }
    }

    private ServerMonitorViewModel CreateServerMonitorViewModel()
    {
        var connection = _connection!;
        var host = connection.Host.Trim();
        var user = connection.Username.Trim();
        var label = (user.Length == 0 ? host : $"{user}@{host}")
            + (connection.Port == 22 ? "" : $":{connection.Port}");

        var vm = new ServerMonitorViewModel(
            // Takes a counted reference on the live shared client so the transport
            // survives until the monitor lets go, even if the tab reconnects meanwhile.
            () => _client is { IsConnected: true } client && client.TryAddRef() ? client : null,
            connection.TerminalType,
            connection.LoginCommands,
            label,
            host);
        MonitorPanel.DataContext = vm;
        return vm;
    }

    private void PersistMonitorPanelWidth()
    {
        if (!MonitorColumn.Width.IsAbsolute || MonitorColumn.Width.Value <= 0)
            return;

        _monitorPanelWidth = MonitorColumn.Width.Value;
        if (DataContext is MainWindowViewModel vm)
            vm.MonitorPanelWidth = _monitorPanelWidth;
    }

    /// <summary>
    /// Lays out the terminal/AI columns for the current panel state. Three states: panel
    /// hidden (terminal full width), side panel (terminal + splitter + fixed-width panel),
    /// and agent mode (terminal fully hidden, panel takes the whole tab above an optional
    /// file browser).
    /// </summary>
    private void ApplyAiPanelLayout()
    {
        var show = AiPanelHost.IsVisible;
        var agentMode = IsAgentModeLayoutActive;

        // Hiding the terminal area (not just collapsing the column) keeps the pty size
        // stable, so remote command output isn't rewrapped to a zero-width window.
        TerminalArea.IsVisible = !agentMode;
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

        ApplyFileBrowserPlacement();
    }

    /// <summary>Places the shared file-browser row below the terminal normally, or below
    /// the AI conversation while agent mode owns the tab.</summary>
    private void ApplyFileBrowserPlacement()
    {
        var agentMode = IsAgentModeLayoutActive;
        var column = agentMode ? 0 : 2;
        Grid.SetColumn(FileSplitter, column);
        Grid.SetColumn(FileBrowserHost, column);

        // Outside agent mode the AI panel continues alongside both terminal rows. In
        // agent mode it yields the remembered bottom row only while the browser is open.
        Grid.SetRowSpan(AiPanelHost, agentMode && FileBrowserHost.IsVisible ? 1 : 3);
    }

    private void PersistAiPanelWidth()
    {
        if (!AiColumn.Width.IsAbsolute || AiColumn.Width.Value <= 0)
            return;

        _aiPanelWidth = AiColumn.Width.Value;
        if (DataContext is MainWindowViewModel vm)
            vm.AiPanelWidth = _aiPanelWidth;
    }

    private List<AgentProvider> BuildAgentProviders(string systemPrompt)
    {
        var claudePath = AgentCliLocator.FindClaude();
        var codexPath = AgentCliLocator.FindCodex();
        var grokPath = AgentCliLocator.FindGrok();
        var workingDir = Path.Combine(DebugInstanceContext.RuntimeTempRoot, "agent");

        var providers = new List<AgentProvider>
        {
            AgentChatViewModel.CreateClaudeProvider(claudePath is null
                ? null
                : (model, effort) => new ClaudeChatSession(claudePath, workingDir, systemPrompt, model: model, effort: effort),
                claudePath is null
                    ? null
                    : (model, effort, sessionId) => new ClaudeChatSession(
                        claudePath, workingDir, systemPrompt, sessionId, model, effort)),
            AgentChatViewModel.CreateCodexProvider(
                codexPath is null
                    ? null
                    : (model, effort) => new CodexChatSession(codexPath, workingDir, systemPrompt, model: model, effort: effort),
                codexPath is null
                    ? null
                    : () => CodexChatSession.ListModelsCachedAsync(codexPath),
                AgentModelCatalogCache.Load("Codex"),
                models => AgentModelCatalogCache.Save("Codex", models),
                codexPath is null
                    ? null
                    : (model, effort, sessionId) => new CodexChatSession(
                        codexPath, workingDir, systemPrompt, model, effort, sessionId)),
            AgentChatViewModel.CreateGrokProvider(
                grokPath is null
                    ? null
                    : (model, effort) => new GrokChatSession(grokPath, workingDir, systemPrompt, model: model, effort: effort),
                grokPath is null
                    ? null
                    : () => GrokChatSession.ListModelsCachedAsync(grokPath),
                AgentModelCatalogCache.Load("Grok"),
                models => AgentModelCatalogCache.Save("Grok", models),
                grokPath is null
                    ? null
                    : (model, effort, sessionId) => new GrokChatSession(
                        grokPath, workingDir, systemPrompt, model, effort, sessionId)),
        };

        if (DataContext is MainWindowViewModel mainVm)
        {
            foreach (var config in mainVm.CustomAiProviders)
            {
                var cfg = config;
                var apiKey = DecryptApiKey(cfg.ApiKey);
                var configured = !string.IsNullOrEmpty(apiKey) && cfg.Models.Count > 0;
                providers.Add(AgentChatViewModel.CreateCustomProvider(cfg, !configured
                    ? null
                    : (model, effort) => cfg.ApiType == CustomAiApiType.Anthropic
                        ? new AnthropicChatSession(cfg.BaseUrl, apiKey!, model ?? cfg.Models[0], systemPrompt)
                        : new OpenAiChatSession(cfg.BaseUrl, apiKey!, model ?? cfg.Models[0], systemPrompt, effort)));
            }
        }

        return providers;
    }

    /// <summary>Returns the clear-text API key: jrm1 blobs are decrypted with the master
    /// password (null when that fails), legacy plaintext values pass through unchanged.</summary>
    private static string? DecryptApiKey(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
            return null;
        if (!MasterKeyService.IsPasswordBlob(stored))
            return stored;
        return PasswordProtector.TryDecrypt(stored, out var clear) ? clear : null;
    }

    private AgentChatViewModel CreateAgentChatViewModel()
    {
        var systemPrompt = BuildAssistantSystemPrompt();
        var providers = BuildAgentProviders(systemPrompt);

        var vm = new AgentChatViewModel(
            providers,
            takeSelection: () =>
            {
                if (!Term.HasSelection)
                    return null;
                var text = GetTerminalSelectionText(Term.SelectedText);
                // Consume the selection: clearing it also hides the panel's hint.
                _model.ClearSelection();
                return text;
            },
            runCaptured: RunCapturedAsync,
            transferFiles: TransferFilesAsync,
            initialOptions: (DataContext as MainWindowViewModel)?.AiPanelOptions,
            persistOptions: options =>
            {
                if (DataContext is MainWindowViewModel mainVm)
                    mainVm.AiPanelOptions = options;
            },
            runTerminalAction: RunAiTerminalActionAsync,
            conversationScopeId: BuildAiConversationScopeId(),
            connectionLabel: BuildAiConversationLabel(),
            legacyConversationScopeIds: [BuildLegacyAiConversationScopeId()]);

        vm.ConversationHistoryInteraction = () =>
            TopLevel.GetTopLevel(this) is Window owner
                ? AiConversationHistoryDialog.ShowAsync(owner, vm)
                : Task.CompletedTask;

        // The gear button: edit the custom providers, persist them, and let the changed
        // event below rebuild the picker in every open terminal tab (including this one).
        vm.ManageProvidersInteraction = async () =>
        {
            if (DataContext is not MainWindowViewModel mainVm
                || TopLevel.GetTopLevel(this) is not Window owner)
            {
                return;
            }

            var edited = await CustomAiProvidersDialog.ShowAsync(owner, mainVm.CustomAiProviders);
            if (edited is not null)
                mainVm.SetCustomAiProviders(edited);
        };

        if (DataContext is MainWindowViewModel providersOwner)
        {
            _customProvidersChangedHandler = () =>
            {
                if (!_disposed)
                    vm.ReplaceProviders(BuildAgentProviders(systemPrompt));
            };
            providersOwner.CustomAiProvidersChanged += _customProvidersChangedHandler;
            _customProvidersOwner = providersOwner;
        }

        // Keep the panel's "selection will be attached" hint in sync with the terminal.
        vm.HasTerminalSelection = Term.HasSelection;
        _model.Terminal.Selection.SelectionChanged += () => Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed)
                vm.HasTerminalSelection = Term.HasSelection;
        });

        // Relayout when the user flips agent mode: remember the side-panel width first (a
        // no-op when the column is already star-sized), then switch the columns over.
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AgentChatViewModel.AgentMode))
            {
                PersistAiPanelWidth();
                ApplyAiPanelLayout();
                if (IsLoginManualInputPending)
                    FocusTerminal();
            }
        };

        AiPanel.DataContext = vm;
        return vm;
    }

    private string BuildAiConversationScopeId()
    {
        if (_connection is not null && Guid.TryParse(_connection.ConnectionId, out _))
            return $"connection:{_connection.ConnectionId.Trim()}";

        return BuildLegacyAiConversationScopeId();
    }

    private string BuildLegacyAiConversationScopeId()
    {
        if (!string.IsNullOrWhiteSpace(_sourcePath))
        {
            try { return Path.GetFullPath(_sourcePath); }
            catch { return _sourcePath; }
        }

        if (_connection is null)
            return "unknown";
        return _connection.IsWsl
            ? $"wsl:{_connection.TargetLabel}"
            : $"ssh:{_connection.Username}@{_connection.Host}:{_connection.Port}";
    }

    private string BuildAiConversationLabel()
    {
        var name = _connection?.Name?.Trim();
        return !string.IsNullOrWhiteSpace(name)
            ? name
            : _connection?.TargetLabel ?? "Unknown connection";
    }

    private string BuildAssistantSystemPrompt()
    {
        var name = _connection?.Name;
        var host = _connection?.IsWsl == true
            ? $"local WSL distribution {_connection.TargetLabel}"
            : _connection?.Host;
        var target = !string.IsNullOrWhiteSpace(name)
            ? $"\"{name}\" ({host})"
            : host ?? "a remote server";

        var terminalKind = _connection?.IsWsl == true ? "WSL" : "SSH";

        // WSL shares the Windows filesystem, so plain cp beats any transfer protocol there;
        // SSH connections get the canonical file.upload/file.download tools.
        var transferInstructions = _connection?.IsWsl == true
            ? "To move files between Windows and the distro, just cp them: Windows drives are " +
              "mounted inside the distro under /mnt (C:\\Users is /mnt/c/Users). "
            : "The `file.upload` and `file.download` tools accept one `SOURCE -> DESTINATION` " +
              "line per file. For file.upload, SOURCE is a local Windows file path and DESTINATION " +
              "is a remote directory (optional; defaults to the shell's current directory, created " +
              "if missing). For file.download, SOURCE is a remote file path and DESTINATION is a " +
              "local Windows directory (optional; defaults to the user's Downloads folder). " +
              "All lines in one block share the first destination. The transfer runs through the " +
              "terminal via ZMODEM (requires lrzsz on the server) and its result is returned to " +
              "you like command output. ";

        return
            $"You are an assistant embedded inside JeekRemoteManager, operating an interactive {terminalKind} " +
            $"terminal connected to {target}. " +
            "JeekRemoteManager exposes one canonical app-tool protocol. To call a tool, output exactly " +
            "ONE fenced block tagged `jrm-tool`. The first line inside the block is the tool name; all " +
            "remaining lines are its payload. Available tools: `terminal.run` runs a non-interactive " +
            "remote command or short script; `terminal.run-danger` does the same but asks the user to " +
            "confirm destructive or hard-to-reverse work; `terminal.interrupt` stops the current terminal " +
            "command and restores shell input; `terminal.reconnect` rebuilds the SSH channel. " +
            "Use exactly one tool per response and wait for its returned result before deciding the next " +
            "step. When the task is done, reply with a short summary and no tool block. Text that is not " +
            "a tool call must be plain prose or a plain fenced block without a language tag. " +
            "Your own built-in tools (shell, file access, etc.), if you have any, run on the user's " +
            "LOCAL Windows machine where JeekRemoteManager runs — NOT on the server. Use them for " +
            "local-side work (e.g. reading or writing local files, or transferring files between the " +
            "local machine and the server); use `terminal.run` for everything that must run on the " +
            "server. Never mix the two up. " +
            transferInstructions +
            "For recovery, use `terminal.interrupt` first. Use `terminal.reconnect` only when interrupt " +
            "did not restore a usable shell or the SSH channel is unhealthy. Recovery tools run without " +
            "user confirmation and their result is returned to you. " +
            "`terminal.run` executes without confirmation, so avoid destructive actions unless explicitly asked, and " +
            "prefer non-interactive flags (e.g. `-y`). " +
            "SAFETY: use `terminal.run-danger` instead of `terminal.run` for deleting or overwriting files " +
            "or directories, dropping databases or tables, formatting or repartitioning disks, " +
            "force-pushing, or removing volumes or containers with data. The user is then asked to " +
            "confirm before it runs. Assume a Linux server unless told otherwise.";
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
        var commandStarted = false;
        try
        {
            if (_disposed || _shellClosed || _channel is null)
                return "[not connected]";

            Interlocked.Exchange(ref _isAiCommandRunning, 1);
            Interlocked.Increment(ref _aiCommandExecutionCount);
            commandStarted = true;

            Dispatcher.UIThread.Post(() =>
                FeedLine($"\r\n\u001b[35m[AI]\u001b[0m $ {AiCommandTerminalText.NormalizeForTerminalEcho(command)}"));

            var result = await ExecuteRemotePayloadAsync(command, cancellationToken);
            await FeedCompletionLineAndRefreshPromptAsync(
                $"\u001b[35m[AI exit {result.ExitCode}]\u001b[0m");

            var output = CleanShellOutput(result.Output ?? string.Empty).Trim();
            Interlocked.Increment(ref _aiCommandCompletionCount);
            return $"[exit {result.ExitCode}]\n{output}";
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
            if (commandStarted)
                Interlocked.Exchange(ref _isAiCommandRunning, 0);
            _scriptLock.Release();
        }
    }

    /// <summary>Runs a captured terminal command for live Debug MCP verification.</summary>
    public Task<string> RunTerminalCommandForDebugAsync(string command) =>
        RunCapturedAsync(command, CancellationToken.None);

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

    private void HandleUserInput(ReadOnlyMemory<byte> data)
    {
        if (CanReconnectFromInput(data))
        {
            Reconnect();
            return;
        }

        SendToShell(data);

        // A pending "#input" login directive resumes once the user presses Enter
        // (or pastes text ending with a newline).
        if (_loginManualInputTcs is { } manualInput && ContainsEnter(data))
            manualInput.TrySetResult();
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

    private static bool ContainsEnter(ReadOnlyMemory<byte> data)
    {
        var span = data.Span;
        for (var i = 0; i < span.Length; i++)
        {
            if (span[i] == (byte)'\r' || span[i] == (byte)'\n')
                return true;
        }

        return false;
    }

    private void Reconnect()
    {
        if (_connection is null || _disposed || _connectInProgress || IsConnected)
            return;

        _activePayloadMonitor?.Fail(new InvalidOperationException("SSH terminal is reconnecting."));
        _activePayloadMonitor = null;
        _suppressUserInput = false;
        Interlocked.Increment(ref _connectionGeneration);
        // When only the shell channel died (e.g. the user typed `exit`) but the
        // transport is still up, reopen a channel on it instead of redialing;
        // ConnectAsync falls back to a fresh connection if that fails.
        _pendingSharedClient = _client is { IsConnected: true } live && live.TryAddRef() ? live : null;
        DisposeTransport();

        FeedLine("\r\n\u001b[36m[reconnect]\u001b[0m");
        BeginConnectionAttempt();
    }

    /// <summary>Manually aborts the active terminal payload and restores an interactive
    /// prompt. Public so the terminal-level recovery can be verified through Debug MCP.</summary>
    public void ForceInterruptTerminalCommand()
    {
        Interlocked.Increment(ref _terminalRecoveryCount);
        var monitor = Volatile.Read(ref _activePayloadMonitor);
        if (monitor is not null)
        {
            Volatile.Write(ref _manualRecoveryMonitor, monitor);
            monitor.Fail(new ShellRecoveryRequestedException(
                "Terminal command was forcefully interrupted by the user."));
        }

        // Restore local keyboard routing immediately; do not wait for the background
        // command task to unwind before the user can type in the terminal again.
        _suppressUserInput = false;

        // Never make releasing the command lock depend on a possibly wedged shell write.
        // The monitor failure above unwinds RunCapturedAsync first; recovery bytes are
        // best-effort on a worker and target only the channel that was current at the click.
        RecoverShellInputInBackground(_channel);
        FocusTerminal();
    }

    /// <summary>Manually replaces the current terminal channel with a fresh connection.
    /// Unlike Enter-to-reconnect, this is available even while the old channel appears live.</summary>
    public void ReconnectTerminal()
    {
        if (_connection is null || _disposed || _connectInProgress)
            return;

        var monitor = Volatile.Read(ref _activePayloadMonitor);
        if (monitor is not null)
        {
            Volatile.Write(ref _manualRecoveryMonitor, monitor);
            monitor.Fail(new ShellRecoveryRequestedException("Terminal is reconnecting."));
        }
        _activePayloadMonitor = null;
        _suppressUserInput = false;
        Interlocked.Increment(ref _connectionGeneration);
        DisposeTransport();

        FeedLine("\r\n\u001b[36m[reconnect]\u001b[0m");
        BeginConnectionAttempt();
    }

    private void BeginConnectionAttempt()
    {
        var pendingLoginInput = Interlocked.Exchange(ref _loginManualInputTcs, null);
        var hadPendingLoginInput = pendingLoginInput is not null;
        pendingLoginInput?.TrySetCanceled();
        var generation = Interlocked.Increment(ref _connectionGeneration);
        if (hadPendingLoginInput)
            RefreshLoginManualInputLayout(generation);
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
        // SyncWindowSize no-ops while _channel is null (e.g. the pre-Start call).
        Dispatcher.UIThread.Post(SyncWindowSize, DispatcherPriority.Background);
    }

    private async Task ConnectAsync(int generation)
    {
        var connection = _connection!;
        if (connection.IsWsl)
        {
            await ConnectWslAsync(connection, generation);
            return;
        }

        var host = connection.Host.Trim();
        var port = connection.Port > 0 ? connection.Port : 22;
        _connectInProgress = true;

        // A duplicated tab starts from the source tab's authenticated connection: SSH
        // multiplexes independent session channels over one transport, so a new shell
        // channel needs no TCP/auth round-trip. Falls back to a fresh connection when
        // the shared transport is dead or refuses more channels (e.g. sshd MaxSessions).
        var shared = Interlocked.Exchange(ref _pendingSharedClient, null);
        if (shared is not null)
        {
            FeedLine($"Opening a new session on the existing connection to {host}:{port} ...");
            if (await TryOpenShellAsync(shared, generation, reportFailure: false))
                return;

            shared.Release();
            if (_disposed || generation != _connectionGeneration)
                return;
        }

        FeedLine($"Connecting to {host}:{port} ...");

        SharedSshClient client;
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
                return new SharedSshClient(sshClient);
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
            client.Release();
            return;
        }

        if (!await TryOpenShellAsync(client, generation, reportFailure: true))
            client.Release();
    }

    /// <summary>
    /// Starts the connection's WSL distribution under a ConPTY and wires it to the
    /// terminal. Local counterpart of the SSH dial: same failure reporting, same
    /// reconnect hint. The first start of a stopped WSL VM can take several seconds.
    /// </summary>
    private async Task ConnectWslAsync(Connection connection, int generation)
    {
        _connectInProgress = true;
        var distro = connection.WslDistro.Trim();
        FeedLine($"Starting WSL ({(distro.Length == 0 ? "default distribution" : distro)}) ...");

        var cols = (uint)Math.Max(20, _model.Terminal.Cols);
        var rows = (uint)Math.Max(5, _model.Terminal.Rows);

        ConPtySession session;
        try
        {
            if (!WslDistroService.IsWslInstalled)
                throw new InvalidOperationException("WSL is not installed on this machine.");

            var args = WslDistroService.BuildLaunchArguments(connection);
            session = await Task.Run(() =>
                ConPtySession.Start(WslDistroService.WslExePath, args, (int)cols, (int)rows));
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
            session.Dispose();
            return;
        }

        AttachChannel(new WslTerminalChannel(session), generation, (cols, rows));
    }

    /// <summary>
    /// Opens the shell channel on <paramref name="client"/> and wires it to the
    /// terminal. Returns false without taking ownership when the channel cannot be
    /// opened or the attempt is stale — the caller still owns releasing the client.
    /// With <paramref name="reportFailure"/> false (the shared-connection fast path),
    /// a failure is only noted in the terminal so the caller can fall back to a
    /// fresh connection.
    /// </summary>
    private async Task<bool> TryOpenShellAsync(SharedSshClient client, int generation, bool reportFailure)
    {
        var connection = _connection!;
        var cols = (uint)Math.Max(20, _model.Terminal.Cols);
        var rows = (uint)Math.Max(5, _model.Terminal.Rows);
        var terminalType = string.IsNullOrWhiteSpace(connection.TerminalType)
            ? Connection.DefaultTerminalType
            : connection.TerminalType.Trim();

        ShellStream shell;
        try
        {
            // The channel open is a network round-trip; keep it off the UI thread so
            // a stalled (half-dead) shared transport cannot freeze the window.
            shell = await Task.Run(() => client.Client.CreateShellStream(terminalType, cols, rows, 0, 0, 4096));
        }
        catch (Exception ex)
        {
            if (_disposed || generation != _connectionGeneration)
            {
                _connected?.TrySetCanceled();
                _connectInProgress = false;
                return false;
            }

            if (!reportFailure)
            {
                FeedLine($"\u001b[33m[shared connection unavailable] {ex.Message}\u001b[0m");
                return false;
            }

            _connected?.TrySetException(new InvalidOperationException($"Connection failed: {ex.Message}", ex));
            _connectInProgress = false;
            FeedLine($"\u001b[31m[connect failed] {ex.Message}\u001b[0m");
            FeedReconnectHint();
            return false;
        }

        if (_disposed || generation != _connectionGeneration)
        {
            _connected?.TrySetCanceled();
            _connectInProgress = false;
            try { shell.Dispose(); } catch { /* ignore */ }
            return false;
        }

        _client = client;
        AttachChannel(new SshTerminalChannel(shell), generation, (cols, rows));
        return true;
    }

    /// <summary>
    /// Wires an opened channel (SSH shell or WSL ConPTY) to the terminal:
    /// data/error/close events, connection completion, window-size sync, and the
    /// connection's login commands.
    /// </summary>
    private void AttachChannel(ITerminalChannel channel, int generation, (uint Cols, uint Rows) initialSize)
    {
        _channel = channel;
        _lastSentWindowSize = initialSize;
        Interlocked.Exchange(ref _lastShellDataTicks, 0);

        channel.DataReceived += data =>
        {
            if (generation == _connectionGeneration)
                OnShellData(data);
        };
        channel.ErrorMessage += message => Dispatcher.UIThread.Post(() =>
        {
            if (generation == _connectionGeneration && !_disposed)
                FeedLine($"\r\n\u001b[31m[error] {message}\u001b[0m");
        });
        channel.Closed += () =>
        {
            if (generation != _connectionGeneration || _disposed)
                return;

            _shellClosed = true;
            var shouldAutoReconnect = _aiViewModel?.IsBusy == true;
            TaskCompletionSource<bool>? reconnectCompletion = null;
            if (shouldAutoReconnect)
            {
                reconnectCompletion = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                Volatile.Write(ref _aiAutoReconnectTask, reconnectCompletion.Task);
            }

            _activePayloadMonitor?.Fail(new TerminalConnectionLostException());
            Dispatcher.UIThread.Post(() =>
            {
                FeedLine("\r\n\u001b[33m[session closed]\u001b[0m");
                if (reconnectCompletion is null)
                {
                    FeedReconnectHint();
                    return;
                }

                _ = CompleteAiAutoReconnectAsync(reconnectCompletion);
            });
        };

        _connected?.TrySetResult(true);
        _connectInProgress = false;

        // Push the current (laid-out) size in case SizeChanged fired before connect.
        SyncWindowSize();
        StartLoginCommands(_connection!, generation);
        Dispatcher.UIThread.Post(OpenConfiguredPanelsAfterLogin, DispatcherPriority.Background);
    }

    private async Task CompleteAiAutoReconnectAsync(TaskCompletionSource<bool> completion)
    {
        string? error = null;
        try
        {
            if (_disposed || _connection is null)
                throw new InvalidOperationException("Terminal is closed.");

            _aiAutoReconnectState = "reconnecting";
            _aiViewModel?.NotifyTerminalDisconnected();

            var retryDelays = new[]
            {
                TimeSpan.Zero,
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(3),
            };
            foreach (var retryDelay in retryDelays)
            {
                if (retryDelay > TimeSpan.Zero)
                    await Task.Delay(retryDelay);
                if (_disposed)
                    throw new InvalidOperationException("Terminal is closed.");

                Interlocked.Increment(ref _aiAutoReconnectAttemptCount);
                var generation = ConnectionGeneration;
                ReconnectTerminal();
                if (ConnectionGeneration == generation || _connected is null)
                {
                    error = "Reconnect could not be started.";
                    continue;
                }

                try
                {
                    await _connected.Task;
                    if (!IsConnected)
                        throw new InvalidOperationException("The new terminal channel is not connected.");

                    _aiAutoReconnectState = "connected";
                    Interlocked.Increment(ref _aiAutoReconnectSuccessCount);
                    _aiViewModel?.NotifyTerminalReconnected();
                    completion.TrySetResult(true);
                    return;
                }
                catch (Exception ex)
                {
                    error = ex.GetBaseException().Message;
                }
            }

            throw new InvalidOperationException(error ?? "Unknown connection error.");
        }
        catch (Exception ex)
        {
            error = ex.GetBaseException().Message;
        }

        _aiAutoReconnectState = "failed";
        if (!_disposed)
            _aiViewModel?.NotifyTerminalReconnectFailed(error ?? "Unknown connection error.");
        completion.TrySetResult(false);
    }

    /// <summary>Runs the production AI disconnect notification and reconnect sequence on
    /// the current terminal. Intended for live Debug MCP verification.</summary>
    public async Task<string> RunAiAutoReconnectForDebugAsync()
    {
        if (_aiViewModel is null)
            return "[AI panel has not been opened]";
        if (_disposed || _connection is null)
            return "[terminal is closed]";

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _aiAutoReconnectTask, completion.Task);
        _ = CompleteAiAutoReconnectAsync(completion);
        var reconnected = await completion.Task;
        return $"state={AiAutoReconnectState}; connected={IsTerminalConnected}; success={reconnected}; "
               + $"attempts={AiAutoReconnectAttemptCount}; successes={AiAutoReconnectSuccessCount}";
    }

    /// <summary>
    /// Opens the panels selected on this SSH connection after authentication. This is
    /// public so the running behavior and resulting panel state can be exercised via
    /// the generic Debug MCP invoke/get_value tools.
    /// </summary>
    public void OpenConfiguredPanelsAfterLogin()
    {
        if (_disposed || _connection is not { IsSsh: true } connection || !IsConnected)
            return;

        if (connection.AutoOpenMonitorPanel && !IsMonitorPanelOpen)
            ToggleMonitorPanel();
        if (connection.AutoOpenAiPanel && !IsAiPanelOpen)
            ToggleAiPanel();
        if (connection.AutoOpenFileBrowserPanel && !IsFileBrowserPanelOpen)
            ToggleFileBrowserPanel();
    }

    /// <summary>
    /// Types the connection's login commands into the shell, one line at a time.
    /// Each line is sent only after the remote has produced output and then gone
    /// quiet, so bastion menus, sudo prompts, etc. are on screen before their
    /// answer is typed. A line consisting of "#input" pauses the sequence until
    /// the user types something manually (e.g. a 2FA code) and presses Enter.
    /// In a duplicated tab, a "#duplicate" line skips all commands before it.
    /// Runs on every shell open, including reconnects.
    /// </summary>
    private void StartLoginCommands(Connection connection, int generation)
    {
        var lines = LoginCommandSequence.Select(connection.LoginCommands, _isDuplicatedSession);
        if (lines.Length == 0)
            return;

        _ = Task.Run(async () =>
        {
            // Output older than this tick doesn't count as the response to the
            // previous command; the PTY echoes every typed line, so waiting for
            // newer data never stalls on a command with no output of its own.
            long mustBeAfterTicks = 0;
            const int quietMs = 500;
            const int timeoutMs = 30000;

            foreach (var line in lines)
            {
                if (LoginCommandSequence.IsManualInputDirective(line))
                {
                    var manualInput = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    Interlocked.Exchange(ref _loginManualInputTcs, manualInput)?.TrySetCanceled();
                    RefreshLoginManualInputLayout(generation);
                    try
                    {
                        // No timeout: fetching a 2FA code can take as long as it takes.
                        while (!manualInput.Task.IsCompleted)
                        {
                            if (_disposed || _shellClosed || generation != _connectionGeneration)
                                return;
                            await Task.Delay(50);
                        }
                    }
                    finally
                    {
                        Interlocked.CompareExchange(ref _loginManualInputTcs, null, manualInput);
                        RefreshLoginManualInputLayout(generation);
                    }

                    // The next command must wait for output produced after the
                    // user's Enter, not for what was already on screen.
                    mustBeAfterTicks = Environment.TickCount64;
                    continue;
                }

                var startedAt = Environment.TickCount64;
                while (true)
                {
                    if (_disposed || _shellClosed || generation != _connectionGeneration)
                        return;

                    var lastData = Interlocked.Read(ref _lastShellDataTicks);
                    var now = Environment.TickCount64;
                    // ">=" not ">": on a low-latency link the echo can land in the
                    // same TickCount64 tick as the send that provoked it.
                    if (lastData != 0 && lastData >= mustBeAfterTicks && now - lastData >= quietMs)
                        break;
                    // Don't stall forever on a server that prints nothing.
                    if (now - startedAt >= timeoutMs)
                        break;
                    await Task.Delay(50);
                }

                // Stamp before the write so an echo arriving in the same tick counts.
                mustBeAfterTicks = Environment.TickCount64;
                try
                {
                    WriteToShell(line + "\r");
                }
                catch
                {
                    // A closed/broken stream is reported via Closed/ErrorOccurred.
                    return;
                }
            }
        });
    }

    private void RefreshLoginManualInputLayout(int generation) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed || generation != _connectionGeneration)
                return;

            ApplyAiPanelLayout();
            if (IsLoginManualInputPending)
                FocusTerminal();
            else if (IsAgentModeLayoutActive)
                Dispatcher.UIThread.Post(() => AiPanel.FocusInput(), DispatcherPriority.Background);
        }, DispatcherPriority.Background);

    private void OnShellData(byte[] data)
    {
        if (_disposed || data.Length == 0)
            return;

        Interlocked.Exchange(ref _lastShellDataTicks, Environment.TickCount64);

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

        // ConPTY re-synthesizes VT output, so ZMODEM frames can never arrive
        // intact — skip detection entirely on channels that aren't 8-bit clean.
        if (_channel?.SupportsBinaryTransfers != true)
        {
            FeedBytes(data);
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
        var channel = _channel;
        if (channel is null || _disposed || data.IsEmpty)
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
        if (_channel is null || _disposed || _shellClosed)
            throw new InvalidOperationException("Terminal is not connected.");

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
        catch (ShellRecoveryRequestedException)
        {
            // Manual recovery already interrupted/restored or replaced the shell.
            throw;
        }
        catch (OperationCanceledException)
        {
            // Cancelled (e.g. the AI panel's Stop button): the remote command is still
            // running in the shell — Ctrl+C it before restoring echo.
            if (!ReferenceEquals(Volatile.Read(ref _manualRecoveryMonitor), monitor))
            {
                TryInterruptRemoteCommand();
                TryRestoreShellEcho();
            }
            throw;
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
            Interlocked.CompareExchange(ref _manualRecoveryMonitor, null, monitor);
            _suppressUserInput = false;
        }
    }

    private void WriteToShell(byte[] data)
    {
        var channel = _channel;
        if (channel is null || _disposed || _shellClosed || data.Length == 0)
            return;

        lock (_shellWriteGate)
        {
            channel.Write(data);
        }
    }

    private void FeedBytes(byte[] data)
    {
        if (data.Length == 0)
            return;

        if (_resizeOutputBuffer.TryAppend(data))
        {
            // Resize-triggered prompt redraws normally arrive immediately, but may
            // be split across several SSH packets. Wait for a short quiet period so
            // the carriage return and replacement prompt are rendered atomically.
            // The absolute deadline keeps continuous command output from being held.
            var remaining = Volatile.Read(ref _resizeOutputDeadlineTicks) - Environment.TickCount64;
            if (remaining <= 0)
                FlushResizeOutputBuffer();
            else
                _resizeOutputFlushTimer?.Change(
                    (int)Math.Min(ResizeOutputQuietPeriodMs, remaining),
                    Timeout.Infinite);
            return;
        }

        FeedBytesDirect(data);
    }

    private void FeedBytesDirect(byte[] data)
    {
        if (data.Length == 0)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed)
                return;
            _model.Feed(data, data.Length);
            RecordCursorRow();
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

    private void TryInterruptRemoteCommand()
    {
        try
        {
            WriteToShell("\u0003");
        }
        catch
        {
            // Best-effort only; the shell may already be closed.
        }
    }

    private void RecoverShellInputInBackground(ITerminalChannel? channel)
    {
        if (channel is null || _disposed)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                lock (_shellWriteGate)
                {
                    channel.Write([0x03]);
                }

                // ISIG normally flushes input queued after Ctrl+C. Sending the restore
                // command in the same SSH/ConPTY packet can therefore discard it too.
                await Task.Delay(200).ConfigureAwait(false);

                var restoreCommand = InteractiveShellPayloadRunner.RestoreEchoCommand.TrimEnd('\n')
                    + "; printf '\\033[?25h'\r";
                lock (_shellWriteGate)
                {
                    channel.Write(Encoding.UTF8.GetBytes(restoreCommand));
                }
            }
            catch
            {
                // Best-effort only; reconnect may already have disposed this channel.
            }
        });
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

    /// <summary>
    /// Works around XTerm.NET TerminalBuffer.Resize cursor bugs on both shrink and
    /// grow (maximize): the library moves the viewport without keeping the cursor on
    /// the same absolute buffer line, so the shell's prompt redraw overwrites history.
    /// </summary>
    private void RepairBufferAfterResize(int newRows)
    {
        var terminal = _model.Terminal;
        // Full-screen apps (alt buffer or a custom scroll region) repaint themselves
        // after a resize, and ScrollUp would splice inside the scroll region.
        if (terminal.IsAlternateBufferActive || terminal.Buffer.ScrollTop != 0)
            return;

        if (TerminalBufferResizeRepair.TryRepair(
                terminal.Buffer,
                _lastFedAbsoluteCursorRow,
                _lastFedCursorRow,
                newRows))
        {
            _model.UpdateDisplay();
        }
    }

    /// <summary>Remembers the cursor row so <see cref="RepairBufferAfterResize"/> knows
    /// where the cursor was before the library's resize moved it.</summary>
    private void RecordCursorRow()
    {
        var buffer = _model.Terminal.Buffer;
        _lastFedCursorRow = buffer.Y;
        _lastFedAbsoluteCursorRow = buffer.YBase + buffer.Y;
    }

    /// <summary>
    /// Schedules the remote pty resize after the local size settles. An interactive
    /// drag produces dozens of size events per second; sending each one makes the
    /// remote shell emit prompt redraws computed for widths that are stale by the
    /// time they arrive over the network, which strews wrongly-wrapped prompt copies
    /// across the screen. Debouncing sends a single window-change per gesture.
    /// </summary>
    private void SyncWindowSize()
    {
        if (_disposed)
            return;

        if (_windowSizeSyncTimer is null)
        {
            _windowSizeSyncTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _windowSizeSyncTimer.Tick += (_, _) =>
            {
                _windowSizeSyncTimer!.Stop();
                SendWindowSizeNow();
            };
        }

        _windowSizeSyncTimer.Stop();
        _windowSizeSyncTimer.Start();
    }

    private void SendWindowSizeNow()
    {
        var channel = _channel;
        if (channel is null || _disposed || _shellClosed)
            return;
        try
        {
            var cols = (uint)Math.Max(20, _model.Terminal.Cols);
            var rows = (uint)Math.Max(5, _model.Terminal.Rows);
            if (_lastSentWindowSize == (cols, rows))
                return;
            _lastSentWindowSize = (cols, rows);
            BeginResizeOutputCoalescing();
            channel.Resize(cols, rows);
        }
        catch
        {
            // Ignore resize failures on a closing stream.
        }
    }

    private void BeginResizeOutputCoalescing()
    {
        _resizeOutputBuffer.Start();
        _resizeOutputFlushTimer ??= new Timer(_ => FlushResizeOutputBuffer());
        // Allow for a remote round trip, while placing a firm upper bound on how
        // long unrelated live output can be delayed by a resize.
        Volatile.Write(
            ref _resizeOutputDeadlineTicks,
            Environment.TickCount64 + ResizeOutputHardLimitMs);
        _resizeOutputFlushTimer.Change(ResizeOutputInitialWaitMs, Timeout.Infinite);
    }

    private void FlushResizeOutputBuffer()
    {
        Volatile.Write(ref _resizeOutputDeadlineTicks, 0);
        var data = _resizeOutputBuffer.StopAndDrain();
        FeedBytesDirect(data);
    }

    private void FeedLine(string text)
    {
        _model.Feed(text + "\r\n");
        RecordCursorRow();
    }

    private void FeedReconnectHint() => FeedLine("\u001b[90m[press Enter to reconnect]\u001b[0m");

    private void DisposeTransport()
    {
        try { _channel?.Dispose(); } catch { /* ignore */ }
        // Drop our reference only: another tab duplicated from this one may still
        // be using the connection; the last holder tears it down.
        _client?.Release();
        _channel = null;
        _client = null;
    }

    /// <summary>Tears down the SSH session. Safe to call multiple times.</summary>
    public void Close()
    {
        _disposed = true;
        Interlocked.Increment(ref _connectionGeneration);
        Interlocked.Exchange(ref _loginManualInputTcs, null)?.TrySetCanceled();
        _connectInProgress = false;
        _connected?.TrySetCanceled();
        _activePayloadMonitor?.Fail(new ObjectDisposedException(nameof(TerminalView)));
        _activeZmodemCancellation?.Cancel();
        _activeZmodemQueue?.Complete(new ObjectDisposedException(nameof(TerminalView)));
        _activeZmodemTrace?.Dispose();
        _zmodemDetectionFlushTimer?.Dispose();
        _resizeOutputFlushTimer?.Dispose();
        _resizeOutputBuffer.StopAndDrain();
        _windowSizeSyncTimer?.Stop();
        Interlocked.Exchange(ref _pendingSharedClient, null)?.Release();
        DisposeTransport();

        if (_customProvidersOwner is not null && _customProvidersChangedHandler is not null)
            _customProvidersOwner.CustomAiProvidersChanged -= _customProvidersChangedHandler;
        _customProvidersOwner = null;
        _customProvidersChangedHandler = null;

        _fileBrowserViewModel?.Dispose();
        _fileBrowserViewModel = null;

        _monitorViewModel?.Dispose();
        _monitorViewModel = null;

        var ai = _aiViewModel;
        _aiViewModel = null;
        if (ai is not null)
            _ = ai.DisposeAsync();
    }
}
