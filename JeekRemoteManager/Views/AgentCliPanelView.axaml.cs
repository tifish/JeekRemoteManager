using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using JeekRemoteManager.Services;
using JeekRemoteManager.ViewModels;
using SvcSystems.UI.Terminal;
using XTerm.Buffer;

namespace JeekRemoteManager.Views;

public partial class AgentCliPanelView : UserControl
{
    private const double MinFontSize = 10;
    private const double MaxFontSize = 28;

    private static readonly TimeSpan ResizeSettleDebounce = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan OutputFrameInterval = TimeSpan.FromMilliseconds(16);

    private readonly TerminalControlModel _model = new(new TerminalOptions
    {
        Cols = 80,
        Rows = 24,
        // Full-screen agent TUIs repaint themselves; reflow would mix old cells into new frames.
        ReflowOnResize = false,
        // Codex (with --no-alt-screen) relies on host scrollback + scrollbar; Claude/Grok
        // mostly scroll inside their TUI but still benefit from history when on normal buffer.
        Scrollback = 5000,
    });

    private readonly DispatcherTimer _resizeSettleTimer = new()
    {
        Interval = ResizeSettleDebounce,
    };

    private readonly DispatcherTimer _outputFrameTimer = new()
    {
        Interval = OutputFrameInterval,
    };

    // Codex/Claude use SGR dim which this terminal does not paint; rewrite to soft gray.
    private readonly TerminalDimColorFilter _dimColorFilter = new();
    // ConPTY output can split multi-byte UTF-8 across reads; decode before Feed(byte[]).
    private readonly Utf8StreamDecoder _utf8Decoder = new();
    // Present one completed terminal frame instead of every ConPTY packet in a TUI redraw.
    private readonly TerminalSessionOutputBuffer _sessionOutputBuffer = new();

    private ConPtySession? _liveSession;
    private Action<byte[]>? _liveSessionDataHandler;
    private AgentCliPanelViewModel? _vm;
    private double _fontSize = 14;
    private int _lastSentCols;
    private int _lastSentRows;
    private int _lastFedCursorRow;
    private int _lastFedAbsoluteCursorRow;
    private int _sessionFeedGeneration;
    private long _receivedPacketCount;
    private long _feedBatchCount;
    private long _displayRefreshCount;

    public event EventHandler? CloseRequested;

    public AgentCliPanelView()
    {
        InitializeComponent();

        CliTerm.Model = _model;
        CliTerm.FontSize = _fontSize;
        TerminalTextInputMethodClient.Attach(CliTerm);
        UpdateFontSizeLabel();

        _resizeSettleTimer.Tick += OnResizeSettleTick;
        _outputFrameTimer.Tick += OnOutputFrameTick;

        _model.UserInput += (_, e) => _vm?.WriteToSession(e.Data.ToArray());
        _model.SizeChanged += OnModelSizeChanged;

        // Function keys are CLI-level shortcuts. Route F1-F24 from the whole panel
        // so they still reach the embedded process after header interaction.
        AddHandler(InputElement.KeyDownEvent, OnPanelPreviewKeyDown, RoutingStrategies.Tunnel);
        CliTerm.ContextRequested += OnCliContextRequested;
        CliTerm.AddHandler(InputElement.KeyDownEvent, OnCliPreviewKeyDown, RoutingStrategies.Tunnel);
        // Tunnel so we run before TerminalControl: on the normal buffer, prefer host
        // scrollback (Codex --no-alt-screen). Otherwise mouse-tracking mode steals the
        // wheel and the external scrollbar looks dead.
        CliTerm.AddHandler(
            InputElement.PointerWheelChangedEvent,
            OnCliPointerWheel,
            RoutingStrategies.Tunnel);

        TerminalHost.SizeChanged += (_, _) =>
            Dispatcher.UIThread.Post(SyncViewportToConPty, DispatcherPriority.Render);
        SizeChanged += (_, _) =>
            Dispatcher.UIThread.Post(SyncViewportToConPty, DispatcherPriority.Render);

        CliTerm.AddHandler(PointerPressedEvent, (_, _) => FocusCliTerminal(), RoutingStrategies.Tunnel);

        DataContextChanged += OnDataContextChanged;

        // TabControl unloads inactive tabs. Do not tear down ConPTY wiring on Unloaded —
        // the ViewModel session stays alive; re-attach on Loaded.
        Unloaded += (_, _) => _resizeSettleTimer.Stop();
        Loaded += (_, _) =>
        {
            TryReattachEmbeddedSession();
            Dispatcher.UIThread.Post(SyncViewportToConPty, DispatcherPriority.Loaded);
            FocusCliTerminal();
        };
    }

    /// <summary>Diagnostics for Debug MCP (session attach / feed / scroll state).</summary>
    public string DebugOutputStats =>
        $"session={(_liveSession is null ? "no" : "yes")} gen={_sessionFeedGeneration} " +
        $"vmEmbedded={_vm?.HasEmbeddedSession == true} handler={(_liveSessionDataHandler is null ? "no" : "yes")} " +
        $"canScroll={_model.CanScroll} mouseMode={_model.IsMouseModeActive} " +
        $"alt={_model.Terminal.IsAlternateBufferActive} " +
        $"yDisp={_model.ScrollOffset} yBase={_model.MaxScrollback} atBottom={_model.Terminal.Buffer.IsAtBottom} " +
        $"packets={Interlocked.Read(ref _receivedPacketCount)} batches={Interlocked.Read(ref _feedBatchCount)} " +
        $"refreshes={Interlocked.Read(ref _displayRefreshCount)} pending={_sessionOutputBuffer.PendingPacketCount} " +
        $"fnKeys={DebugForwardedFunctionKeyCount} lastFn={DebugLastForwardedFunctionKey} " +
        $"lastFnHex={DebugLastForwardedFunctionKeyHex}";

    /// <summary>Last panel-forwarded function key, exposed for Debug MCP.</summary>
    public string DebugLastForwardedFunctionKey { get; private set; } = "(none)";

    /// <summary>Last function-key sequence as hexadecimal bytes, exposed for Debug MCP.</summary>
    public string DebugLastForwardedFunctionKeyHex { get; private set; } = "(none)";

    /// <summary>Number of function keys forwarded by the panel-wide route, exposed for Debug MCP.</summary>
    public int DebugForwardedFunctionKeyCount { get; private set; }

    /// <summary>Plain text currently visible in the embedded CLI viewport for Debug MCP.</summary>
    public string DebugVisibleText
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
    /// Raises a function key from a header control so Debug MCP exercises the
    /// same panel-wide route used when focus is outside the terminal surface.
    /// </summary>
    public bool DebugPressFunctionKeyFromHeader(int functionKeyNumber)
    {
        if (functionKeyNumber is < 1 or > 24)
            return false;

        var e = new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = (Key)((int)Key.F1 + functionKeyNumber - 1),
        };
        AiOptionsButton.RaiseEvent(e);
        return e.Handled;
    }

    /// <summary>Rendered AI header height exposed for Debug MCP layout verification.</summary>
    public double DebugHeaderHeight => AiHeader.Bounds.Height;

    private void OnCloseClick(object? sender, RoutedEventArgs e) =>
        CloseRequested?.Invoke(this, EventArgs.Empty);

    private void OnFontDecreaseClick(object? sender, RoutedEventArgs e) =>
        SetFontSize(_fontSize - 1);

    private void OnFontIncreaseClick(object? sender, RoutedEventArgs e) =>
        SetFontSize(_fontSize + 1);

    public void SetFontSize(double size)
    {
        _fontSize = Math.Clamp(size, MinFontSize, MaxFontSize);
        CliTerm.FontSize = _fontSize;
        UpdateFontSizeLabel();
        Dispatcher.UIThread.Post(SyncViewportToConPty, DispatcherPriority.Render);
    }

    public double TerminalFontSize => _fontSize;

    private void UpdateFontSizeLabel()
    {
        if (FontSizeLabel is not null)
            FontSizeLabel.Text = ((int)Math.Round(_fontSize)).ToString();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null)
        {
            _vm.SessionStarted -= OnSessionStarted;
            _vm.SessionStopped -= OnSessionStopped;
            _vm.GetViewportSize = null;
        }

        _vm = DataContext as AgentCliPanelViewModel;
        if (_vm is null)
            return;

        _vm.GetViewportSize = ReadViewportSize;
        _vm.SessionStarted += OnSessionStarted;
        _vm.SessionStopped += OnSessionStopped;
    }

    private (int Cols, int Rows) ReadViewportSize()
    {
        var cols = Math.Max(20, _model.Terminal.Cols);
        var rows = Math.Max(5, _model.Terminal.Rows);
        if ((cols <= 20 || rows <= 5) && TerminalHost.Bounds is { Width: > 1, Height: > 1 } bounds)
        {
            var cellW = Math.Max(_fontSize * 0.6, 6);
            var cellH = Math.Max(_fontSize * 1.4, 10);
            cols = Math.Max(20, (int)(bounds.Width / cellW));
            rows = Math.Max(5, (int)(bounds.Height / cellH));
        }

        return (cols, rows);
    }

    private void OnModelSizeChanged(object? sender, TerminalSizeChangedEventArgs e)
    {
        RepairBufferAfterResize(e.Rows);
        ApplyConPtyResizeNow(e.Cols, e.Rows);
        ScheduleResizeSettle();
    }

    private void SyncViewportToConPty()
    {
        if (_vm is null || !_vm.HasEmbeddedSession)
            return;

        var (cols, rows) = ReadViewportSize();
        RepairBufferAfterResize(rows);
        ApplyConPtyResizeNow(cols, rows);
        ScheduleResizeSettle();
    }

    private void ApplyConPtyResizeNow(int cols, int rows)
    {
        if (_vm is null || !_vm.HasEmbeddedSession)
            return;

        cols = Math.Max(20, cols);
        rows = Math.Max(5, rows);
        if (cols == _lastSentCols && rows == _lastSentRows)
            return;

        _lastSentCols = cols;
        _lastSentRows = rows;
        _vm.ResizeSession(cols, rows);
        SanitizeWideCharactersAtEdges();
        try { CliTerm.InvalidateVisual(); } catch { /* ignore */ }
    }

    private void ScheduleResizeSettle()
    {
        if (_vm is null || !_vm.HasEmbeddedSession)
            return;

        _resizeSettleTimer.Stop();
        _resizeSettleTimer.Start();
    }

    private void OnResizeSettleTick(object? sender, EventArgs e)
    {
        _resizeSettleTimer.Stop();
        if (_vm is null || !_vm.HasEmbeddedSession)
            return;

        var (cols, rows) = ReadViewportSize();
        ApplyConPtyResizeNow(cols, rows);
        SanitizeWideCharactersAtEdges();
        RefreshLocalDisplay();
    }

    private void RefreshLocalDisplay()
    {
        try
        {
            _model.UpdateDisplay();
            CliTerm.InvalidateVisual();
            TerminalHost.InvalidateVisual();
        }
        catch
        {
            // Control may be tearing down.
        }
    }

    private void SanitizeWideCharactersAtEdges()
    {
        try
        {
            var terminal = _model.Terminal;
            var buffer = terminal.Buffer;
            var cols = terminal.Cols;
            var rows = terminal.Rows;
            if (cols <= 0 || rows <= 0)
                return;

            var space = BufferCell.Space;
            for (var row = 0; row < rows; row++)
            {
                var line = buffer.GetLine(buffer.YDisp + row);
                if (line is null || line.Length < 1)
                    continue;

                var limit = Math.Min(cols, line.Length);
                if (line[0].Width == 0)
                    line[0] = space;
                if (limit >= 1 && line[limit - 1].Width >= 2)
                    line[limit - 1] = space;
                if (limit >= 2 && line[limit - 1].Width == 0 && line[limit - 2].Width < 2)
                    line[limit - 1] = space;
            }
        }
        catch
        {
            // Buffer may be mid-mutation.
        }
    }

    private void RepairBufferAfterResize(int newRows)
    {
        var terminal = _model.Terminal;
        if (terminal.IsAlternateBufferActive || terminal.Buffer.ScrollTop != 0)
        {
            SanitizeWideCharactersAtEdges();
            return;
        }

        if (TerminalBufferResizeRepair.TryRepair(
                terminal.Buffer,
                _lastFedAbsoluteCursorRow,
                _lastFedCursorRow,
                newRows))
        {
            try { _model.UpdateDisplay(); } catch { /* ignore */ }
        }

        SanitizeWideCharactersAtEdges();
    }

    private void RecordCursorRow()
    {
        var buffer = _model.Terminal.Buffer;
        _lastFedCursorRow = buffer.Y;
        _lastFedAbsoluteCursorRow = buffer.YBase + buffer.Y;
    }

    private void OnSessionStarted(ConPtySession session) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (_vm is null || !_vm.IsCurrentSession(session))
                return;
            AttachLiveSession(session, clearDisplay: true);
        });

    private void OnSessionStopped(bool replaced) =>
        Dispatcher.UIThread.Post(() =>
        {
            _resizeSettleTimer.Stop();
            UnhookSession();
            try
            {
                _model.Feed("\u001b[?1049l\u001b[0m");
                if (replaced)
                    _model.Feed("\u001b[2J\u001b[H");
                else
                    _model.Feed("\r\n\u001b[33m[session ended]\u001b[0m\r\n");
                RecordCursorRow();
                RefreshLocalDisplay();
            }
            catch
            {
                // control may be tearing down
            }
        });

    private void TryReattachEmbeddedSession()
    {
        if (_vm?.EmbeddedSession is not { } session)
            return;

        if (ReferenceEquals(_liveSession, session) && _liveSessionDataHandler is not null)
            return;

        AttachLiveSession(session, clearDisplay: false);
    }

    private void AttachLiveSession(ConPtySession session, bool clearDisplay)
    {
        UnhookSession();

        if (clearDisplay)
        {
            _dimColorFilter.Reset();
            _utf8Decoder.Reset();
            _sessionOutputBuffer.Clear();
            Interlocked.Exchange(ref _receivedPacketCount, 0);
            Interlocked.Exchange(ref _feedBatchCount, 0);
            Interlocked.Exchange(ref _displayRefreshCount, 0);
            try
            {
                _model.Feed("\u001bc\u001b[?1049l\u001b[2J\u001b[H\u001b[0m");
                RecordCursorRow();
                RefreshLocalDisplay();
            }
            catch
            {
                // control may be tearing down
            }
        }

        _liveSession = session;
        _sessionFeedGeneration++;
        var feedGeneration = _sessionFeedGeneration;
        void Handler(byte[] data) => OnSessionData(data, session, feedGeneration);
        _liveSessionDataHandler = Handler;
        session.DataReceived += Handler;

        _lastSentCols = 0;
        _lastSentRows = 0;
        var (cols, rows) = ReadViewportSize();
        ApplyConPtyResizeNow(cols, rows);
        ScheduleResizeSettle();
        if (clearDisplay)
            FocusCliTerminal();
    }

    private void OnSessionData(byte[] data, ConPtySession session, int feedGeneration)
    {
        if (data.Length == 0)
            return;

        Interlocked.Increment(ref _receivedPacketCount);
        if (!_sessionOutputBuffer.Append(data, feedGeneration))
            return;

        // Start a one-frame window on the UI thread. Cursor moves and text replacement
        // sequences emitted during that window are then presented atomically.
        Dispatcher.UIThread.Post(
            () =>
            {
                if (!_outputFrameTimer.IsEnabled)
                    _outputFrameTimer.Start();
            },
            DispatcherPriority.Background);
    }

    private void OnOutputFrameTick(object? sender, EventArgs e)
    {
        _outputFrameTimer.Stop();
        if (_liveSession is not { } session)
        {
            _sessionOutputBuffer.Clear();
            return;
        }

        DrainSessionOutput(session, _sessionFeedGeneration);
    }

    private void DrainSessionOutput(ConPtySession session, int feedGeneration)
    {
        var data = _sessionOutputBuffer.Drain(feedGeneration);
        if (data.Length == 0
            || feedGeneration != _sessionFeedGeneration
            || !ReferenceEquals(_liveSession, session))
            return;

        try
        {
            var filtered = _dimColorFilter.Process(data);
            if (filtered.Length == 0)
                return;

            Interlocked.Increment(ref _feedBatchCount);
            FeedPreservingUserScroll(filtered);
        }
        catch
        {
            // control may be tearing down
        }
    }

    /// <summary>
    /// Feed VT bytes without yanking the viewport back to the bottom when the user has
    /// scrolled up (TerminalControlModel.Feed always EnsureCaretIsVisible when it was at
    /// bottom at the start of the call — continuous Codex output then fights the scrollbar).
    /// </summary>
    private void FeedPreservingUserScroll(byte[] data)
    {
        var terminal = _model.Terminal;
        var followBottom = terminal.Buffer.IsAtBottom;
        var pinnedYDisp = terminal.Buffer.YDisp;

        var text = _utf8Decoder.Decode(data);
        if (text.Length == 0)
            return;

        // Feed the parser directly. TerminalControlModel.Feed performs its own display
        // update before this method restores scroll position, exposing intermediate Codex
        // cursor locations and then causing another refresh below.
        var utf8 = Encoding.UTF8.GetBytes(text);
        terminal.Feed(utf8, utf8.Length);

        if (followBottom)
            _model.EnsureCaretIsVisible();
        else
            _model.ScrollToYDisp(pinnedYDisp);

        _model.UpdateDisplay();
        Interlocked.Increment(ref _displayRefreshCount);
        RecordCursorRow();
    }

    /// <summary>
    /// Normal-buffer agents (Codex --no-alt-screen) scroll via host history. Do not let
    /// mouse-tracking mode convert the wheel into CSI mouse events the app ignores.
    /// </summary>
    private void OnCliPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (e.Delta.Y == 0)
            return;

        if (_model.Terminal.IsAlternateBufferActive)
            return; // Claude/Grok TUI: leave TerminalControl mouse-mode handling.

        _model.HandlePointerWheel(e.Delta);
        e.Handled = true;
    }

    private void UnhookSession()
    {
        if (_liveSession is not null && _liveSessionDataHandler is not null)
        {
            try { _liveSession.DataReceived -= _liveSessionDataHandler; }
            catch { /* ignore */ }
        }

        _liveSessionDataHandler = null;
        _liveSession = null;
        _sessionFeedGeneration++;
        _outputFrameTimer.Stop();
        _sessionOutputBuffer.Clear();
    }

    public void FocusCliTerminal() =>
        Dispatcher.UIThread.Post(() =>
        {
            if (CliTerm.IsVisible)
                CliTerm.Focus();
        }, DispatcherPriority.Input);

    public void NotifyHostLayoutChanged() =>
        Dispatcher.UIThread.Post(SyncViewportToConPty, DispatcherPriority.Loaded);

    private async void OnCliContextRequested(object? sender, TerminalContextRequestedEventArgs e)
    {
        if (e.HasSelection)
        {
            await CopyCliSelectionToClipboardAsync(e.SelectedText);
            _model.ClearSelection();
            return;
        }

        await PasteCliFromClipboardAsync();
    }

    private async void OnCliPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (IsCliPasteGesture(e))
        {
            e.Handled = true;
            await PasteCliFromClipboardAsync();
            return;
        }

        if (!IsCliCopyGesture(e))
            return;

        var text = GetCliSelectionText(CliTerm.SelectedText);
        if (string.IsNullOrEmpty(text))
            return;

        e.Handled = true;
        await SetCliClipboardTextAsync(text);
    }

    private void OnPanelPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled
            || _vm?.HasEmbeddedSession != true
            || !CliTerm.IsVisible)
        {
            return;
        }

        if (!TerminalFunctionKeySequence.TryEncode(
                e.Key,
                e.KeyModifiers,
                out var functionKeyNumber,
                out var sequence))
        {
            return;
        }

        e.Handled = true;
        DebugForwardedFunctionKeyCount++;
        DebugLastForwardedFunctionKey = $"F{functionKeyNumber}+{e.KeyModifiers}";
        DebugLastForwardedFunctionKeyHex = Convert.ToHexString(Encoding.ASCII.GetBytes(sequence));
        _model.Send(sequence);
        FocusCliTerminal();
    }

    private async Task PasteCliFromClipboardAsync()
    {
        if (_vm is null || !_vm.HasEmbeddedSession || !CliTerm.IsVisible)
            return;

        if (!CliTerm.IsFocused)
            CliTerm.Focus();

        await CliTerm.PasteFromClipboardAsync();
    }

    private Task CopyCliSelectionToClipboardAsync(string selectedText)
    {
        var text = GetCliSelectionText(selectedText);
        return string.IsNullOrEmpty(text)
            ? Task.CompletedTask
            : SetCliClipboardTextAsync(text);
    }

    private string? GetCliSelectionText(string? selectedText) =>
        TerminalClipboardText.BuildSelectedTextWithoutSoftWraps(_model.Terminal) ?? selectedText;

    private Task SetCliClipboardTextAsync(string text)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        return clipboard?.SetTextAsync(text) ?? Task.CompletedTask;
    }

    private static bool IsCliPasteGesture(KeyEventArgs e) =>
        e.Key == Key.V
        && e.KeyModifiers.HasFlag(KeyModifiers.Control)
        && !e.KeyModifiers.HasFlag(KeyModifiers.Alt);

    private static bool IsCliCopyGesture(KeyEventArgs e) =>
        e.Key == Key.C
        && e.KeyModifiers.HasFlag(KeyModifiers.Control)
        && e.KeyModifiers.HasFlag(KeyModifiers.Shift);
}
