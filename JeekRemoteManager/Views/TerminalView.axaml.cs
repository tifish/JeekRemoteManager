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

/// <summary>
/// Hosts a native Avalonia terminal control (SvcSystems.UI.Terminal) driven by an
/// <see cref="ITerminalChannel"/> — an SSH.NET interactive shell or a local WSL
/// (ConPTY) process. Self-contained and reusable: a window can host one for
/// the Phase A spike, and the right-pane tab UI (Phase B) hosts one per tab.
/// SSH authentication is programmatic via <see cref="SshConnectionFactory"/> (the
/// stored password is sent automatically; a one-time or second-factor code is
/// typed in a dialog); live bytes, keyboard, title, and window size are
/// wired to the channel.
/// </summary>
public partial class TerminalView : UserControl
{
    private static readonly TimeSpan OutputFrameInterval = TimeSpan.FromMilliseconds(16);

    private sealed class EmptyValueObservable : IObservable<object?>
    {
        public static readonly EmptyValueObservable Instance = new();

        public IDisposable Subscribe(IObserver<object?> observer)
        {
            observer.OnCompleted();
            return EmptyDisposable.Instance;
        }
    }

    private sealed class EmptyDisposable : IDisposable
    {
        public static readonly EmptyDisposable Instance = new();

        public void Dispose()
        {
        }
    }

    /// <summary>Patience for one queued route switch. Route switches on a shared
    /// transport are serialized, so the real budget is this times the number of
    /// borrowers ahead — timing out costs a whole fresh login, which is far worse
    /// than waiting out a queue that is making progress.</summary>
    public const int BastionPoolWaitTimeoutSeconds = 15;

    /// <summary>Upper bound for that scaled wait, so a wedged borrower cannot park a
    /// new connection indefinitely.</summary>
    public const int BastionPoolWaitCapSeconds = 120;

    /// <summary>How long to wait for another connection's login to this same bastion.
    /// Generous on purpose: the person at the keyboard may be walking to another device
    /// for a code, and giving up early asks them for a second one — measured at three
    /// minutes, that is exactly what happened.</summary>
    public const int BastionPendingLoginWaitSeconds = 600;

    public const string BastionPoolWaitingMessage =
        "[bastion reuse] Waiting for another session to finish switching targets ...";
    public const string BastionPendingLoginMessage =
        "[bastion reuse] Another connection to this bastion is logging in; "
        + "waiting for it instead of asking for a second code ...";
    public const string BastionPoolFullMessage =
        "[bastion reuse] All authenticated connections are at their observed session limits; "
        + "opening a fresh SSH connection.";
    public const string BastionReuseFallbackMessage =
        "[bastion reuse failed; opening a fresh SSH connection]";
    public const string BastionReuseRetryMessage =
        "[bastion reuse] The login commands did not reach the target; "
        + "trying once more on a new channel of the same connection ...";
    public const string BastionReuseGaveUpMessage =
        "[bastion reuse] Still could not reach the target. The bastion session above is "
        + "live and yours to drive by hand; use Reconnect to start over.";

    public static string BastionPendingLoginTimeoutMessage =>
        $"[bastion reuse busy] The other login did not finish within "
        + $"{BastionPendingLoginWaitSeconds} seconds; opening a fresh SSH connection.";

    public static string BastionPoolWaitTimeoutMessage(int waitedSeconds) =>
        $"[bastion reuse busy] Waited {waitedSeconds} seconds; "
        + "opening a fresh SSH connection.";

    private const int ResizeOutputInitialWaitMs = 500;
    private const int ResizeOutputQuietPeriodMs = 150;
    private const int ResizeOutputHardLimitMs = 800;

    private readonly TerminalControlModel _model;

    private Connection? _connection;
    private readonly object _clientReferenceGate = new();
    private SharedSshClient? _client;
    private bool _ownsClientReference;
    private SharedSshClient? _pendingSharedClient;
    private ITerminalChannel? _channel;
    private string? _sourcePath;
    /// <summary>
    /// 1-based tab session among open terminals on this connection (matches the tab header
    /// suffix). Session 2+ gets its own AI agent workspace named like the tab: <c>…/name (N)</c>.
    /// </summary>
    private int _sessionNumber = 1;
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
    // Captures the output produced since the last login command so a "#select" directive
    // can match the bastion menu on screen; only fed while a login sequence is running.
    private readonly LoginMenuOutputCapture _loginOutputCapture = new();
    private volatile bool _loginCaptureActive;
    private bool _isDuplicatedSession;
    private bool _forceNewTcpConnection;
    private string _loginSequenceState = "idle";
    private string _bastionSessionState = "none";
    private bool _connectInProgress;
    private bool _shellClosed;
    private bool _suppressUserInput;
    private int _lastFedCursorRow;
    private int _lastFedAbsoluteCursorRow;
    private DispatcherTimer? _windowSizeSyncTimer;
    private (uint Cols, uint Rows)? _lastSentWindowSize;
    private readonly TerminalResizeOutputBuffer _resizeOutputBuffer = new();
    // SSH packets often split multi-byte characters (Chinese) mid-character; decode statefully
    // before TerminalControlModel.Feed, which would otherwise insert U+FFFD tofu boxes. The
    // encoding is the connection's (UTF-8 unless it names a legacy code page such as GBK);
    // all three fields are replaced together by ApplyTerminalEncoding on each connect.
    private Encoding _terminalEncoding = TerminalEncoding.Utf8;
    private TerminalStreamDecoder _outputDecoder = new();
    private TerminalInputEncoder _inputEncoder = new(TerminalEncoding.Utf8);
    private readonly TerminalSessionOutputBuffer _sessionOutputBuffer = new();
    private readonly Timer _outputFrameFlushTimer;
    private long _receivedPacketCount;
    private long _feedBatchCount;
    private Timer? _resizeOutputFlushTimer;
    private long _resizeOutputDeadlineTicks;
    private string? _pendingKeyboardCopyText;
    private AgentCliPanelViewModel? _aiViewModel;
    private double _aiPanelWidth = 380;
    private FileBrowserViewModel? _fileBrowserViewModel;
    private double _fileBrowserHeight = 260;
    private ServerMonitorViewModel? _monitorViewModel;
    private DispatcherTimer? _monitorSuspendTimer;
    private bool _tabActive = true;
    private double _monitorPanelWidth = 260;
    private int _isAiCommandRunning;
    private long _aiCommandExecutionCount;
    private long _aiCommandCompletionCount;
    private long _terminalRecoveryCount;
    private Task<bool>? _aiAutoReconnectTask;
    private long _aiAutoReconnectAttemptCount;
    private long _aiAutoReconnectSuccessCount;
    private string _aiAutoReconnectState = "idle";
    private WeakReference<InputElement>? _lastFocusedElement;
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
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');

        // InteractiveShellPayloadMonitor returns the full raw stream, including the prepare
        // command echo and READY/BEGIN/EXIT markers. The AI panel only wants the script body
        // between BEGIN and EXIT; otherwise TruncateForDisplay often cuts the real output.
        const string beginPrefix = "__JRM_BEGIN_";
        const string exitPrefix = "__JRM_EXIT_";
        var beginIndex = text.LastIndexOf(beginPrefix, StringComparison.Ordinal);
        if (beginIndex >= 0)
        {
            var beginLineEnd = text.IndexOf('\n', beginIndex);
            if (beginLineEnd >= 0)
            {
                var bodyStart = beginLineEnd + 1;
                var exitIndex = text.IndexOf(exitPrefix, bodyStart, StringComparison.Ordinal);
                text = exitIndex >= bodyStart ? text[bodyStart..exitIndex] : text[bodyStart..];
            }
        }

        return text.Trim();
    }

    /// <summary>Raised on the UI thread when the remote shell sets its title (OSC).</summary>
    public event EventHandler<string>? TitleChanged;

    public Connection? Connection => _connection;

    /// <summary>The lazily-created AI CLI panel view model, exposed for Debug MCP verification.</summary>
    public AgentCliPanelViewModel? AiViewModel => _aiViewModel;

    /// <summary>Current connection-scoped agent MCP server, exposed for Debug MCP probes.</summary>
    /// <summary>Product MCP endpoint for the AI CLI on this tab (null until the panel starts it).</summary>
    public string? SourcePath => _sourcePath;

    /// <summary>
    /// 1-based parallel-tab index for this connection (header shows nothing for 1, "(2)" for 2…).
    /// Assigned when the tab is created; used to isolate AI agent workspaces.
    /// </summary>
    public int SessionNumber
    {
        get => _sessionNumber;
        set => _sessionNumber = value < 1 ? 1 : value;
    }

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
        : this(TerminalAppearance.DefaultScrollbackLines)
    {
    }

    /// <param name="scrollbackLines">History this tab keeps. The terminal buffer is sized once,
    /// when it is created, so the setting reaches new tabs only.</param>
    public TerminalView(int scrollbackLines)
    {
        _model = new TerminalControlModel(new TerminalOptions
        {
            Cols = 120,
            Rows = 30,
            // Disable resize reflow so full-screen TUIs (top, vim, mc) stay stable.
            ReflowOnResize = false,
            Scrollback = TerminalAppearance.NormalizeScrollback(scrollbackLines),
        });
        InitializeComponent();
        _outputFrameFlushTimer = new Timer(
            _ => Dispatcher.UIThread.Post(DrainTerminalOutputFrame));
        ApplyLocalizedText();
        Jeek.Avalonia.Localization.Localizer.LanguageChanged += OnLanguageChanged;

        // A TerminalView instance belongs to exactly one SSH tab. Remember its
        // most recently focused descendant so switching tabs can return to the
        // terminal, file browser, AI panel, etc. without persisting UI focus.
        AddHandler(
            InputElement.GotFocusEvent,
            OnDescendantGotFocus,
            RoutingStrategies.Bubble,
            handledEventsToo: true);

        Term.Model = _model;
        TerminalTextInputMethodClient.Attach(Term);
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

    private void OnLanguageChanged(object? sender, EventArgs e) => ApplyLocalizedText();

    private void ApplyLocalizedText()
    {
        ToolTip.SetTip(
            ScrollToBottomButton,
            Jeek.Avalonia.Localization.Localizer.Get("ScrollToLatestHint"));
        ToolTip.SetTip(
            CloseScriptPanelButton,
            Jeek.Avalonia.Localization.Localizer.Get("Close"));
        ClearScriptParametersButton.Content =
            Jeek.Avalonia.Localization.Localizer.Get("ClearParameters");
        FindBox.PlaceholderText = Jeek.Avalonia.Localization.Localizer.Get("FindPlaceholder");
        ToolTip.SetTip(FindPreviousButton, Jeek.Avalonia.Localization.Localizer.Get("FindPrevious"));
        ToolTip.SetTip(FindNextButton, Jeek.Avalonia.Localization.Localizer.Get("FindNext"));
        ToolTip.SetTip(FindCloseButton, Jeek.Avalonia.Localization.Localizer.Get("Close"));
    }

    private static void ReleaseLocalValueBindings(Control root)
    {
        var controls = new HashSet<Control>();
        var pending = new Stack<Control>();
        pending.Push(root);
        while (pending.TryPop(out var control))
        {
            if (!controls.Add(control))
                continue;

            foreach (var child in control.GetVisualChildren().OfType<Control>())
                pending.Push(child);
            foreach (var child in control.GetLogicalChildren().OfType<Control>())
                pending.Push(child);
            if (control is ItemsControl itemsControl)
            {
                foreach (var child in itemsControl.Items.OfType<Control>())
                    pending.Push(child);
            }
            if (control.ContextMenu is { } contextMenu)
                pending.Push(contextMenu);
            if (control is Button { Flyout: MenuFlyout menuFlyout })
            {
                foreach (var child in menuFlyout.Items.OfType<Control>())
                    pending.Push(child);
            }
        }

        // LocalizeExtension keeps its observers alive through the process-wide
        // language event. A closed tab will never render again, so discard all
        // local value frames (including bindings) from its detached visual tree.
        foreach (var item in controls.Reverse())
        {
            var properties = AvaloniaPropertyRegistry.Instance.GetRegistered(item)
                .Concat(AvaloniaPropertyRegistry.Instance.GetRegisteredAttached(item.GetType()))
                .Distinct()
                .ToArray();
            foreach (var property in properties)
            {
                if (!property.IsDirect
                    && item.GetDiagnostic(property).Priority == BindingPriority.LocalValue)
                {
                    // Avalonia's ClearValue leaves a local observable binding in
                    // its private binding table. Rebinding to a completing source
                    // disposes that subscription and then removes itself.
                    item.Bind(property, EmptyValueObservable.Instance, BindingPriority.LocalValue);
                }
            }
        }
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
        bool isDuplicatedSession = false,
        bool forceNewTcpConnection = false)
    {
        _connection = connection;
        _sourcePath = sourcePath;
        _pendingSharedClient = sharedClient;
        _isDuplicatedSession = isDuplicatedSession;
        _forceNewTcpConnection = forceNewTcpConnection;
        FocusTerminal();
        if (connection.AutoLogSession)
        {
            try
            {
                StartSessionLog();
            }
            catch (Exception ex)
            {
                FeedLine($"\u001b[33m[session log could not start: {ex.Message}]\u001b[0m");
            }
        }
        BeginConnectionAttempt();
    }

    /// <summary>
    /// Assigns the connection without starting one, so Debug MCP can exercise panels
    /// that are gated on the connection kind with no network activity at all: the
    /// monitor session then simply reports "waiting for a connection".
    /// </summary>
    internal void DebugAttachConnectionWithoutConnecting(Connection connection) =>
        _connection = connection;

    /// <summary>
    /// Marks a network-free probe tab as reusable so Debug MCP can exercise the
    /// connection-tree open modes without dialing a real server.
    /// </summary>
    internal void DebugAttachReusableConnectionProbe(Connection connection)
    {
        _connection = connection;
        _connectInProgress = true;
    }

    /// <summary>Connection-start policy exposed for the Debug MCP new-TCP probe.</summary>
    internal bool DebugRequiresNewTcpConnection => _forceNewTcpConnection;

    /// <summary>Login-command section selected for this tab, exposed for Debug MCP.</summary>
    internal bool DebugIsDuplicatedSession => _isDuplicatedSession;

    /// <summary>
    /// Hands out this view's live SSH connection for a duplicated tab, taking a
    /// reference on the duplicate's behalf. Returns null when there is nothing
    /// usable to share (not connected, or already torn down) — the duplicate then
    /// falls back to a fresh connection.
    /// </summary>
    public SharedSshClient? ShareClientForDuplicate()
    {
        var client = _client;
        if (_disposed
            || client is null
            || !client.IsConnected
            || !string.Equals(LoginSequenceState, "ready", StringComparison.Ordinal))
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

    /// <summary>
    /// Restores the last focused control in this terminal tab, falling back to
    /// the SSH terminal when the previous control no longer exists or is hidden.
    /// The weak reference is intentionally session-only and is never persisted.
    /// </summary>
    public void RestoreLastFocus() =>
        Dispatcher.UIThread.Post(() =>
        {
            if (_lastFocusedElement is { } lastFocused
                && lastFocused.TryGetTarget(out var target)
                && target is Avalonia.Visual visual
                && ReferenceEquals(visual.FindAncestorOfType<TerminalView>(includeSelf: true), this)
                && target.Focus())
            {
                return;
            }

            Term.Focus();
        }, DispatcherPriority.Background);

    private void OnDescendantGotFocus(object? sender, RoutedEventArgs e)
    {
        if (e.Source is InputElement focused
            && focused is Avalonia.Visual visual
            && ReferenceEquals(visual.FindAncestorOfType<TerminalView>(includeSelf: true), this))
        {
            _lastFocusedElement = new WeakReference<InputElement>(focused);
        }
    }

    internal string DebugLastFocusTarget =>
        _lastFocusedElement is { } lastFocused && lastFocused.TryGetTarget(out var target)
            ? DescribeFocusTarget(target)
            : "(none)";

    internal string DebugCurrentFocusTarget
    {
        get
        {
            var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
            return focused is Avalonia.Visual visual
                   && ReferenceEquals(visual.FindAncestorOfType<TerminalView>(includeSelf: true), this)
                ? DescribeFocusTarget(focused)
                : "(outside)";
        }
    }

    /// <summary>Creates a second, non-terminal focus target for the Debug MCP
    /// tab-switch probe without opening any network-backed panel.</summary>
    internal void DebugFocusSecondaryTarget()
    {
        ScrollToBottomButton.IsVisible = true;
        ScrollToBottomButton.Focus();
    }

    /// <summary>Makes the embedded AI terminal focusable in the Debug MCP fixture.
    /// This catches Loaded handlers that override the per-tab restored focus.</summary>
    internal void DebugPrepareLoadedFocusCompetitor()
    {
        AiPanelHost.IsVisible = true;
        AiColumn.MinWidth = 220;
        AiColumn.Width = new GridLength(220, GridUnitType.Pixel);
        AiPanel.DebugShowCliTerminalForTabFocusProbe();
    }

    private static string DescribeFocusTarget(IInputElement? target) => target switch
    {
        Control { Name.Length: > 0 } control => $"{control.GetType().Name}#{control.Name}",
        null => "(none)",
        _ => target.GetType().Name,
    };

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

    /// <summary>Application-owned authenticated bastion transport pool.</summary>
    public BastionSessionPool? BastionSessionPool { get; set; }

    /// <summary>
    /// Looks a saved connection up by tree path — how a jump host is found at dial time,
    /// so an edit to it applies on the next connect. Set by the window.
    /// </summary>
    public Func<string, Connection?>? ResolveConnection { get; set; }

    /// <summary>Held while this tab authenticates a new transport for its bastion, so
    /// other connections to the same bastion wait for it instead of logging in too.</summary>
    private BastionSessionPool.FreshLoginReservation? _freshLoginReservation;

    /// <summary>Automatic retries of a reuse that failed to reach the target, so a
    /// broken workflow cannot turn into an endless reconnect loop.</summary>
    private int _bastionReuseRetries;

    public bool IsMonitorPanelOpen => MonitorPanelHost.IsVisible;

    public bool IsAiPanelOpen => AiPanelHost.IsVisible;

    /// <summary>AI ConPTY pump diagnostics for Debug MCP (null when panel never opened).</summary>
    public string? DebugAiOutputStats =>
        _aiViewModel is null ? null : AiPanel.DebugOutputStats;

    /// <summary>Plain text in the AI CLI viewport exposed for Debug MCP verification.</summary>
    public string? DebugAiVisibleText =>
        _aiViewModel is null ? null : AiPanel.DebugVisibleText;

    /// <summary>Raises a function key from the AI header through the real panel key route.</summary>
    public bool DebugPressAiFunctionKeyFromHeader(int functionKeyNumber) =>
        _aiViewModel is not null && AiPanel.DebugPressFunctionKeyFromHeader(functionKeyNumber);

    /// <summary>Raises the real Ctrl+C route on the AI CLI terminal for Debug MCP.</summary>
    public string? DebugPressAiCtrlC(bool selectVisibleText) =>
        _aiViewModel is null ? null : AiPanel.DebugPressCtrlCOnCli(selectVisibleText);

    /// <summary>Direct AI panel access for self-contained Debug MCP checks.</summary>
    internal AgentCliPanelView DebugAiPanel => AiPanel;

    /// <summary>Rendered SSH terminal font size exposed for Debug MCP verification.</summary>
    internal double DebugTerminalFontSize => Term.FontSize;

    /// <summary>Rendered AI header height exposed for Debug MCP layout verification.</summary>
    public double? DebugAiHeaderHeight =>
        _aiViewModel is null ? null : AiPanel.DebugHeaderHeight;

    /// <summary>True while a login-command <c>#input</c> directive is waiting for Enter.</summary>
    public bool IsLoginManualInputPending => Volatile.Read(ref _loginManualInputTcs) is not null;

    /// <summary>Login-command lifecycle exposed for Debug MCP verification.</summary>
    public string LoginSequenceState => Volatile.Read(ref _loginSequenceState);

    /// <summary>Whether this terminal connected fresh or reused/switches a pooled bastion transport.</summary>
    public string BastionSessionState => Volatile.Read(ref _bastionSessionState);

    /// <summary>Rendered terminal visibility exposed for Debug MCP verification.</summary>
    public bool IsTerminalAreaVisible => TerminalArea.IsVisible;

    /// <summary>Whether the SSH terminal is currently hidden by the AI panel preference.</summary>
    public bool IsSshTerminalHidden => ShouldHideSshTerminal(
        AiPanelHost.IsVisible,
        _aiViewModel?.HideSshTerminal == true,
        IsLoginManualInputPending);

    /// <summary>Computes whether the SSH terminal should be hidden without changing the preference.</summary>
    public static bool ShouldHideSshTerminal(
        bool aiPanelVisible,
        bool hideSshTerminalRequested,
        bool loginManualInputPending) =>
        aiPanelVisible && hideSshTerminalRequested && !loginManualInputPending;

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

    /// <summary>Last SSH terminal function key forwarded through the shared encoder.</summary>
    public string DebugLastTerminalFunctionKey { get; private set; } = "(none)";

    /// <summary>Last forwarded SSH function-key sequence as hexadecimal bytes.</summary>
    public string DebugLastTerminalFunctionKeyHex { get; private set; } = "(none)";

    /// <summary>Number of SSH terminal function keys forwarded by this view.</summary>
    public int DebugForwardedTerminalFunctionKeyCount { get; private set; }

    /// <summary>Raises a function key on the SSH terminal through its real key route.</summary>
    public bool DebugPressTerminalFunctionKey(int functionKeyNumber)
    {
        if (functionKeyNumber is < 1 or > 24)
            return false;

        var e = new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = (Key)((int)Key.F1 + functionKeyNumber - 1),
        };
        Term.RaiseEvent(e);
        return e.Handled;
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
    /// Debug helper: feed raw UTF-8 terminal bytes through the same stateful decoder used for
    /// live SSH/ConPTY output (reproduces multi-packet Chinese rendering).
    /// Prefer <see cref="DebugFeedUtf8Base64"/> from Debug MCP (byte[] needs base64 there).
    /// </summary>
    public void DebugFeedUtf8Bytes(byte[] data)
    {
        if (data is null || data.Length == 0)
            return;
        FeedBytesDirect(data);
    }

    /// <summary>Debug helper: feed base64-encoded UTF-8 bytes (MCP-friendly).</summary>
    public void DebugFeedUtf8Base64(string base64)
    {
        if (string.IsNullOrEmpty(base64))
            return;
        FeedBytesDirect(Convert.FromBase64String(base64));
    }

    /// <summary>
    /// Debug helper: queue output through the same buffer OnShellData uses, then write a
    /// script completion line, so a probe can assert the line lands after the output it
    /// summarizes instead of overtaking the pending output frame.
    /// </summary>
    internal Task DebugFeedCompletionLineAfterOutputAsync(string output, string completionLine)
    {
        FeedBytesDirect(_terminalEncoding.GetBytes(output));
        return FeedCompletionLineAndRefreshPromptAsync(completionLine);
    }

    /// <summary>Debug helper: reset the streaming output decoder (as on a new connection).</summary>
    public void DebugResetUtf8Decoder() => _outputDecoder.Reset();

    /// <summary>Encoding the current session decodes and encodes with, for Debug MCP.</summary>
    internal string DebugTerminalEncodingName => _terminalEncoding.WebName;

    /// <summary>Debug MCP: bytes the shell would receive for this typed UTF-8 input.</summary>
    internal byte[] DebugEncodeInput(string text) =>
        new TerminalInputEncoder(_terminalEncoding).Encode(Encoding.UTF8.GetBytes(text));

    /// <summary>Debug MCP: feeds raw session bytes through the real output pipeline.</summary>
    internal void DebugFeedRawOutput(byte[] data) => FeedBytesDirect(data);

    /// <summary>Debug MCP: switches the session encoding as a connect would.</summary>
    internal void DebugApplyTerminalEncoding(string name) => ApplyTerminalEncoding(TerminalEncoding.Resolve(name));

    /// <summary>
    /// Switches every text boundary of the session to one encoding: the display decoder,
    /// the input transcoder, and the login-menu capture. Called as a connection starts, so
    /// nothing decoded under the previous encoding is carried over.
    /// </summary>
    private void ApplyTerminalEncoding(Encoding encoding)
    {
        _terminalEncoding = encoding;
        _outputDecoder = new TerminalStreamDecoder(encoding);
        _inputEncoder = new TerminalInputEncoder(encoding);
        _loginOutputCapture.SetEncoding(encoding);
    }

    public void DebugResetTerminalOutputStats()
    {
        _outputFrameFlushTimer.Change(Timeout.Infinite, Timeout.Infinite);
        _sessionOutputBuffer.Clear();
        Interlocked.Exchange(ref _receivedPacketCount, 0);
        Interlocked.Exchange(ref _feedBatchCount, 0);
    }

    public (long ReceivedPackets, long FeedBatches, int PendingPackets) DebugTerminalOutputStats =>
        (
            Interlocked.Read(ref _receivedPacketCount),
            Interlocked.Read(ref _feedBatchCount),
            _sessionOutputBuffer.PendingPacketCount
        );

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

    /// <summary>Sets the terminal font size in points (main shell and AI CLI panel).</summary>
    /// <summary>Sets the terminal font family (main shell and AI CLI panel).</summary>
    public void SetFontFamily(FontFamily family)
    {
        Term.FontFamily = family;
        AiPanel.SetFontFamily(family);
        Dispatcher.UIThread.Post(SyncWindowSize, DispatcherPriority.Background);
    }

    /// <summary>Repaints both terminals after the color scheme resources changed.</summary>
    public void RefreshTerminalColors()
    {
        TerminalAppearance.RefreshRendering(Term);
        AiPanel.RefreshTerminalColors();
    }

    /// <summary>Scrollback this tab was created with, for Debug MCP.</summary>
    internal int DebugScrollbackLines => _model.Terminal.Options.Scrollback;

    /// <summary>The main terminal control, for Debug MCP rendering checks.</summary>
    internal TerminalControl DebugTerminalControl => Term;

    /// <summary>Font family of the main terminal, for Debug MCP.</summary>
    internal string DebugTerminalFontFamily => Term.FontFamily.Name;

    public void SetFontSize(double size)
    {
        Term.FontSize = size;
        AiPanel.SetFontSize(size);
        // A font size change resizes the character cell, so the column/row geometry
        // changes too. Push the new size to the remote after the control re-measures
        // rather than relying on it raising SizeChanged for a font-only change.
        // SyncWindowSize no-ops while _channel is null (e.g. the pre-Start call).
        Dispatcher.UIThread.Post(SyncWindowSize, DispatcherPriority.Background);
    }

    private void OnShellData(byte[] data)
    {
        if (_disposed || data.Length == 0)
            return;

        Interlocked.Exchange(ref _lastShellDataTicks, Environment.TickCount64);

        if (_loginCaptureActive)
            _loginOutputCapture.Append(data);

        if (_activeZmodemQueue is { } zmodemQueue)
        {
            _activeZmodemTrace?.WriteBytes("RX raw", data);
            zmodemQueue.Append(data);
            return;
        }

        if (_activePayloadMonitor is { } payloadMonitor)
        {
            var payloadDisplayData = payloadMonitor.Append(data);
            FeedBytes(payloadDisplayData);
            // Only now may the script runner learn that the command finished: it writes
            // its completion line straight to the terminal, ahead of this queue.
            payloadMonitor.ReleasePendingExit();
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
        bool holdingPartialTrigger;
        lock (_zmodemDetectionGate)
        {
            detection = _zmodemDetector.Append(data, out displayData);
            holdingPartialTrigger = _zmodemDetector.HasPendingBytes;
        }

        FeedBytes(displayData);
        if (detection is not null)
        {
            FeedBytes(detection.DisplayBytes);
            StartZmodemTransfer(detection);
            return;
        }

        // Only arm the flush timer when bytes are actually being withheld. Ordinary
        // output now passes straight through, so this is the rare case.
        if (holdingPartialTrigger)
            ScheduleZmodemDetectionFlush();
    }

    private void SendToShell(ReadOnlyMemory<byte> data)
    {
        var channel = _channel;
        if (channel is null || _disposed || data.IsEmpty)
            return;
        try
        {
            byte[] encoded;
            lock (_shellWriteGate)
                encoded = _inputEncoder.Encode(data.Span);
            WriteToShell(encoded);
        }
        catch
        {
            // Best-effort; a closed/broken stream is reported via Closed/ErrorOccurred.
        }
    }

    private void WriteToShell(string text) => WriteToShell(_terminalEncoding.GetBytes(text));

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
        if (!e.Handled && IsFindGesture(e))
        {
            e.Handled = true;
            OpenFindBar();
            return;
        }

        if (!e.Handled
            && TerminalFunctionKeySequence.TryEncode(
                e.Key,
                e.KeyModifiers,
                out var functionKeyNumber,
                out var sequence))
        {
            e.Handled = true;
            DebugForwardedTerminalFunctionKeyCount++;
            DebugLastTerminalFunctionKey = $"F{functionKeyNumber}+{e.KeyModifiers}";
            DebugLastTerminalFunctionKeyHex = Convert.ToHexString(Encoding.ASCII.GetBytes(sequence));
            _model.Send(sequence);
            return;
        }

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

    /// <summary>The real script encoding boundary, also inspected by the Debug MCP.</summary>
    internal InteractiveShellPayload BuildInteractivePayload(string payload) =>
        InteractiveShellPayloadRunner.Build(payload, encoding: _terminalEncoding);

    private async Task<RemotePayloadResult> ExecuteRemotePayloadAsync(
        string payload,
        CancellationToken cancellationToken)
    {
        if (_channel is null || _disposed || _shellClosed)
            throw new InvalidOperationException("Terminal is not connected.");

        var interactivePayload = BuildInteractivePayload(payload);
        // This monitor's output is rendered, so the exit result must not be released
        // until OnShellData has queued that packet's bytes -- see DeferExitCompletion.
        var monitor = new InteractiveShellPayloadMonitor(interactivePayload, _terminalEncoding)
        {
            DeferExitCompletion = true,
        };
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

        Interlocked.Increment(ref _receivedPacketCount);
        var generation = Volatile.Read(ref _connectionGeneration);
        if (!_sessionOutputBuffer.Append(data, generation))
            return;

        _outputFrameFlushTimer.Change(OutputFrameInterval, Timeout.InfiniteTimeSpan);
    }

    private void DrainTerminalOutputFrame()
    {
        var generation = Volatile.Read(ref _connectionGeneration);
        var payload = _sessionOutputBuffer.Drain(generation);
        if (_disposed || payload.Length == 0 || generation != _connectionGeneration)
            return;

        // Decode and feed once per UI frame so split UTF-8 remains ordered while a
        // burst of SSH packets produces one terminal refresh instead of one per read.
        var text = _outputDecoder.Decode(payload);
        if (text.Length > 0)
        {
            Interlocked.Increment(ref _feedBatchCount);
            _model.Feed(text);
            _sessionLog?.Write(text);
        }

        // Say so rather than leaving a silent hole in the scrollback. Bytes are dropped
        // only when the remote outran the UI badly enough to threaten the process.
        if (_sessionOutputBuffer.TakeDroppedByteCount() is > 0 and var dropped)
        {
            _outputDecoder.Reset();
            FeedLine(
                $"\r\n[33m[output truncated: dropped {dropped / 1024} KiB the terminal "
                + "could not keep up with][0m");
        }
        RecordCursorRow();
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
                // Quit a stuck pager first (less shows "(END)" and waits for 'q'), then Ctrl+C.
                lock (_shellWriteGate)
                {
                    channel.Write(Encoding.UTF8.GetBytes("q"));
                }

                await Task.Delay(80).ConfigureAwait(false);

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
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            // FeedLine writes to the terminal directly, while the script's own last lines
            // are still sitting in the output-frame buffer behind a 16 ms timer. Flush
            // those first or the completion line renders above the output it summarizes.
            FlushResizeOutputBuffer();
            DrainTerminalOutputFrame();
            FeedLine("\r\n" + text);
        });
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
        SharedSshClient? client;
        bool ownsClientReference;
        lock (_clientReferenceGate)
        {
            client = _client;
            ownsClientReference = _ownsClientReference;
            _client = null;
            _ownsClientReference = false;
        }
        if (ownsClientReference)
            client?.Release();
        _channel = null;
    }

    /// <summary>Tears down the SSH session. Safe to call multiple times.</summary>
    public void Close()
    {
        _disposed = true;
        StopSessionLog();
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
        _outputFrameFlushTimer.Dispose();
        _sessionOutputBuffer.Clear();
        _windowSizeSyncTimer?.Stop();
        Interlocked.Exchange(ref _pendingSharedClient, null)?.Release();
        // A tab closed mid-login must not keep other connections to the same bastion
        // parked behind a login that will never finish.
        ReleaseFreshLoginReservation();
        DisposeTransport();

        _fileBrowserViewModel?.Dispose();
        _fileBrowserViewModel = null;

        _monitorViewModel?.Dispose();
        _monitorViewModel = null;

        var ai = _aiViewModel;
        _aiViewModel = null;
        AiPanel.DataContext = null;
        if (ai is not null)
            _ = ai.DisposeAsync();

        Jeek.Avalonia.Localization.Localizer.LanguageChanged -= OnLanguageChanged;
        ReleaseLocalValueBindings(AiPanel);
        ReleaseLocalValueBindings(FileBrowser);
        ReleaseLocalValueBindings(MonitorPanel);
        ReleaseLocalValueBindings(this);
        BastionSessionPool?.ReleaseUnusedSessions();
    }
}
