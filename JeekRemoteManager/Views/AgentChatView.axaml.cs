using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ColorTextBlock.Avalonia;
using JeekRemoteManager.ViewModels;

namespace JeekRemoteManager.Views;

public partial class AgentChatView : UserControl
{
    private const double NearBottomThreshold = 48;
    private static readonly TimeSpan ScrollFollowInterval = TimeSpan.FromMilliseconds(1);

    private INotifyCollectionChanged? _observed;
    private readonly TranscriptScrollController _scrollController = new();
    private bool _isProgrammaticScroll;
    private IDisposable? _scrollUpdateTimer;
    private int _scrollToEndDepth;

    /// <summary>Debug MCP diagnostic for detecting synchronous scroll re-entry.</summary>
    public int MaxScrollToEndDepth { get; private set; }

    /// <summary>Debug MCP diagnostic for verifying that coalescing bounds scroll work.</summary>
    public int ScrollToEndCallCount { get; private set; }

    /// <summary>Raised by the panel's own close button; the hosting TerminalView
    /// collapses the panel.</summary>
    public event EventHandler? CloseRequested;

    private void OnCloseClick(object? sender, RoutedEventArgs e) =>
        CloseRequested?.Invoke(this, EventArgs.Empty);

    public AgentChatView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        // Handle Enter on the tunnel (preview) route so it fires before the multi-line
        // TextBox consumes Enter to insert a newline.
        AddHandler(KeyDownEvent, OnShortcutKeyDown, RoutingStrategies.Tunnel);
        InputBox.AddHandler(KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);

        // Each bubble keeps its own text selection; starting a selection in one bubble
        // should drop the highlight left in the others.
        MessagesList.AddHandler(PointerPressedEvent, OnMessagesPointerPressed, RoutingStrategies.Tunnel);
        MessagesList.AddHandler(PointerReleasedEvent, OnMessagesPointerReleased, RoutingStrategies.Tunnel);
        MessagesList.AddHandler(PointerWheelChangedEvent, OnMessagesPointerWheelChanged,
            RoutingStrategies.Tunnel);

        // Selectable text and Markdown controls have their own selection-only copy menu.
        // Intercept the request before those children so right-clicking anywhere in a
        // bubble consistently opens the whole-message menu instead.
        MessagesList.AddHandler(InputElement.ContextRequestedEvent, OnMessageContextRequested,
            RoutingStrategies.Tunnel);
    }

    /// <summary>True when the transcript is not following the latest messages and the
    /// floating jump-to-bottom control is shown. Public for Debug MCP.</summary>
    public bool IsScrollToBottomButtonVisible => ScrollToBottomButton.IsVisible;

    /// <summary>Number of chat bubbles in the bounded, non-virtualized transcript window.</summary>
    public int RealizedMessageCount => MessagesList.GetVisualDescendants()
        .OfType<Border>()
        .Count(border => border.Classes.Contains("chat-bubble"));

    public int FirstRealizedMessageIndex => DataContext is AgentChatViewModel vm
        && vm.TranscriptMessages.Count > 0
            ? vm.Messages.Count - vm.TranscriptMessages.Count
            : -1;

    public int LastRealizedMessageIndex => DataContext is AgentChatViewModel vm
        && vm.TranscriptMessages.Count > 0
            ? vm.Messages.Count - 1
            : -1;

    public bool IsFollowingLatest => _scrollController.IsFollowingLatest;

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        _scrollController.FollowLatest();
        ScheduleScrollUpdate();
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        _scrollUpdateTimer?.Dispose();
        _scrollUpdateTimer = null;
    }

    /// <summary>Scrolls the transcript to the end and resumes following new messages.
    /// Public for Debug MCP and the floating button.</summary>
    public void ScrollToLatest()
    {
        _scrollController.FollowLatest();
        ScrollMessagesToEnd();
        ScheduleScrollUpdate();
        UpdateScrollToBottomButton();
    }

    private void OnScrollToBottomClick(object? sender, RoutedEventArgs e) => ScrollToLatest();

    /// <summary>Adds an assistant message to the live transcript for Debug MCP
    /// rendering checks without starting an external agent session.</summary>
    public ChatMessageViewModel AddDebugAssistantMessage(string markdown)
    {
        if (DataContext is not AgentChatViewModel vm)
            throw new InvalidOperationException("The AI panel is not initialized.");

        var message = new ChatMessageViewModel(ChatRole.Assistant, markdown);
        vm.Messages.Add(message);
        return message;
    }

    /// <summary>Adds a temporary large transcript, lets the bounded transcript window lay
    /// it out, and removes it again. The returned counters let Debug MCP verify that the
    /// non-virtualized visual tree stays bounded without changing the user's conversation.</summary>
    public async Task<string> RunDebugTranscriptStressAsync(int messageCount, int charactersPerMessage)
    {
        if (messageCount is < 1 or > 2_000)
            throw new ArgumentOutOfRangeException(nameof(messageCount));
        if (charactersPerMessage is < 1 or > 20_000)
            throw new ArgumentOutOfRangeException(nameof(charactersPerMessage));
        if (DataContext is not AgentChatViewModel vm)
            throw new InvalidOperationException("The AI panel is not initialized.");

        var added = new List<ChatMessageViewModel>(messageCount);
        var payload = new string('x', charactersPerMessage);
        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < messageCount; i++)
        {
            var message = new ChatMessageViewModel(ChatRole.User, $"stress-{i}: {payload}");
            added.Add(message);
            vm.Messages.Add(message);
        }

        await Task.Delay(100);
        ScrollToLatest();
        await Task.Delay(150);

        var realized = RealizedMessageCount;
        var projected = vm.TranscriptMessageCount;
        var hidden = vm.HiddenEarlierMessageCount;
        var first = FirstRealizedMessageIndex;
        var last = LastRealizedMessageIndex;
        var bottomButtonHidden = !IsScrollToBottomButtonVisible;

        _scrollController.BeginManualNavigation();
        TranscriptScroll.ScrollToHome();
        await Task.Delay(50);
        var awayButtonVisible = IsScrollToBottomButtonVisible;
        ScrollToLatest();
        await Task.Delay(50);
        var returnedToLast = LastRealizedMessageIndex == vm.Messages.Count - 1;
        var returnedButtonHidden = !IsScrollToBottomButtonVisible;
        stopwatch.Stop();

        foreach (var message in added)
            vm.Messages.Remove(message);

        return $"messages={messageCount}; chars={messageCount * charactersPerMessage}; "
               + $"projected={projected}; hidden={hidden}; realized={realized}; range={first}-{last}; "
               + $"bottomButtonHidden={bottomButtonHidden}; awayButtonVisible={awayButtonVisible}; "
               + $"returnedToLast={returnedToLast}; returnedButtonHidden={returnedButtonHidden}; "
               + $"elapsedMs={stopwatch.ElapsedMilliseconds}";
    }

    /// <summary>Exercises the production jump-to-latest path while the last message keeps
    /// growing, matching a user clicking the floating arrow during an AI stream.</summary>
    public async Task<string> RunDebugScrollFollowStressAsync(int updateCount)
    {
        if (updateCount is < 1 or > 1_000)
            throw new ArgumentOutOfRangeException(nameof(updateCount));
        if (DataContext is not AgentChatViewModel vm)
            throw new InvalidOperationException("The AI panel is not initialized.");

        var added = new List<ChatMessageViewModel>();
        try
        {
            var historyPayload = new string('h', 240);
            for (var i = 0; i < 40; i++)
            {
                var history = new ChatMessageViewModel(ChatRole.User, $"history-{i}: {historyPayload}");
                added.Add(history);
                vm.Messages.Add(history);
            }

            var streaming = new ChatMessageViewModel(ChatRole.Assistant, "stream-start")
            {
                IsStreaming = true,
            };
            added.Add(streaming);
            vm.Messages.Add(streaming);

            await Task.Delay(150);
            // Make the synthetic setup deterministic even if a previously queued layout
            // follow-up is still waiting in this invisible test window.
            _scrollController.BeginManualNavigation();
            TranscriptScroll.ScrollToHome();
            await Task.Delay(75);
            UpdateScrollToBottomButton();
            var buttonVisibleBefore = IsScrollToBottomButtonVisible;

            MaxScrollToEndDepth = 0;
            ScrollToEndCallCount = 0;
            ScrollToLatest();

            var chunk = new string('x', 96);
            for (var i = 0; i < updateCount; i++)
            {
                streaming.Text += $"\nstream-{i}: {chunk}";
                await Task.Delay(2);
            }

            await Task.Delay(150 + updateCount * 2);
            var atBottom = IsNearBottom();
            var buttonHiddenAfter = !IsScrollToBottomButtonVisible;
            var scroll = TranscriptScroll;
            var bottomGap = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height - scroll.Offset.Y);
            return $"updates={updateCount}; buttonVisibleBefore={buttonVisibleBefore}; "
                   + $"atBottom={atBottom}; buttonHiddenAfter={buttonHiddenAfter}; "
                   + $"stickToBottom={_scrollController.IsFollowingLatest}; bottomGap={bottomGap:F1}; "
                   + $"scrollCalls={ScrollToEndCallCount}; maxScrollDepth={MaxScrollToEndDepth}";
        }
        finally
        {
            foreach (var message in added)
                vm.Messages.Remove(message);
        }
    }

    /// <summary>Exercises jump-to-latest and selection stability with completed Markdown
    /// messages whose variable-height code blocks reproduce the real Codex transcript.</summary>
    public async Task<string> RunDebugMarkdownScrollSelectionCheckAsync(int messageCount)
    {
        if (messageCount is < 2 or > 200)
            throw new ArgumentOutOfRangeException(nameof(messageCount));
        if (DataContext is not AgentChatViewModel vm)
            throw new InvalidOperationException("The AI panel is not initialized.");

        var added = new List<ChatMessageViewModel>(messageCount);
        try
        {
            for (var i = 0; i < messageCount; i++)
            {
                var markdown = $"Commentary {i} before the command.\n\n"
                               + "```jrm-tool\nterminal.run\n"
                               + $"printf 'message-{i}\\n'\nprintf 'line-2\\n'\n```";
                var message = new ChatMessageViewModel(ChatRole.Assistant, markdown);
                added.Add(message);
                vm.Messages.Add(message);
            }

            await Task.Delay(250);
            MaxScrollToEndDepth = 0;
            ScrollToEndCallCount = 0;
            ScrollToLatest();
            await Task.Delay(250);

            var scroll = TranscriptScroll;
            var bottomGap = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height - scroll.Offset.Y);
            var reachedLast = LastRealizedMessageIndex == vm.Messages.Count - 1;

            var selectedBlock = MessagesList.GetVisualDescendants()
                .OfType<SelectableTextBlock>()
                .LastOrDefault(text => text.Name == "SelectableCodeBlock");
            var selectionAvailable = selectedBlock is not null;
            var selectedText = "";
            if (selectedBlock is not null)
            {
                selectedBlock.SelectAll();
                selectedText = selectedBlock.SelectedText ?? "";
            }

            for (var i = 0; i < 12; i++)
            {
                ScrollToLatest();
                await Task.Delay(5);
            }
            await Task.Delay(150);

            var selectionStable = selectedBlock is not null
                                  && selectedBlock.IsAttachedToVisualTree()
                                  && selectedText.Length > 0
                                  && selectedBlock.SelectedText == selectedText;
            var finalGap = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height - scroll.Offset.Y);
            return $"messages={messageCount}; atBottom={IsNearBottom()}; reachedLast={reachedLast}; "
                   + $"bottomGap={bottomGap:F1}; finalGap={finalGap:F1}; "
                   + $"selectionAvailable={selectionAvailable}; selectionStable={selectionStable}; "
                   + $"scrollCalls={ScrollToEndCallCount}; maxScrollDepth={MaxScrollToEndDepth}";
        }
        finally
        {
            foreach (var message in added)
                vm.Messages.Remove(message);
        }
    }

    /// <summary>Grows the composer while the transcript is following the latest message.
    /// The returned viewport and bottom-gap values let Debug MCP verify that the last
    /// message remains fully visible as the composer takes more vertical space.</summary>
    public async Task<string> RunDebugComposerResizeCheckAsync()
    {
        if (DataContext is not AgentChatViewModel vm)
            throw new InvalidOperationException("The AI panel is not initialized.");

        var originalInput = InputBox.Text;
        var added = new List<ChatMessageViewModel>();
        try
        {
            InputBox.Text = "";
            var payload = new string('c', 240);
            for (var i = 0; i < 30; i++)
            {
                var message = new ChatMessageViewModel(ChatRole.User, $"composer-resize-{i}: {payload}");
                added.Add(message);
                vm.Messages.Add(message);
            }

            await Task.Delay(150);
            ScrollToLatest();
            await Task.Delay(100);

            var scroll = TranscriptScroll;
            var viewportBefore = scroll.Viewport.Height;
            var gapBefore = Math.Max(0, scroll.Extent.Height - viewportBefore - scroll.Offset.Y);
            var scrollCallsBefore = ScrollToEndCallCount;

            InputBox.Text = string.Join(Environment.NewLine,
                Enumerable.Range(1, 12).Select(i => $"composer line {i}"));
            await Task.Delay(150);

            var viewportAfter = scroll.Viewport.Height;
            var gapAfter = Math.Max(0, scroll.Extent.Height - viewportAfter - scroll.Offset.Y);
            var streaming = new ChatMessageViewModel(ChatRole.Assistant, "composer-stream-start")
            {
                IsStreaming = true,
            };
            added.Add(streaming);
            vm.Messages.Add(streaming);
            var streamChunk = new string('s', 96);
            for (var i = 0; i < 120; i++)
            {
                streaming.Text += $"\ncomposer-stream-{i}: {streamChunk}";
                InputBox.Text = i % 2 == 0
                    ? "composer short"
                    : string.Join(Environment.NewLine,
                        Enumerable.Range(1, 12).Select(line => $"composer line {line}"));
                await Task.Delay(2);
            }
            await Task.Delay(150);

            var composerScrollCalls = ScrollToEndCallCount - scrollCallsBefore;
            return $"viewportBefore={viewportBefore:F1}; viewportAfter={viewportAfter:F1}; "
                   + $"gapBefore={gapBefore:F1}; gapAfter={gapAfter:F1}; "
                   + $"atBottom={IsNearBottom()}; stickToBottom={_scrollController.IsFollowingLatest}; "
                   + $"scrollCalls={composerScrollCalls}; maxScrollDepth={MaxScrollToEndDepth}";
        }
        finally
        {
            InputBox.Text = originalInput;
            foreach (var message in added)
                vm.Messages.Remove(message);
        }
    }

    private void OnMessageContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (e.Source is Visual source && OpenMessageContextMenu(source))
            e.Handled = true;
    }

    /// <summary>Opens the context menu for the chat bubble containing <paramref name="source"/>.
    /// Public so Debug MCP can verify the same routing used by right-click.</summary>
    public bool OpenMessageContextMenu(Visual source)
    {
        var bubble = source is Border sourceBorder && sourceBorder.Classes.Contains("chat-bubble")
            ? sourceBorder
            : source.GetVisualAncestors()
                .OfType<Border>()
                .FirstOrDefault(border => border.Classes.Contains("chat-bubble"));

        if (bubble?.ContextMenu is not { } menu)
            return false;

        menu.Open(bubble);
        return true;
    }

    private void OnMessagesPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not Visual source)
            return;

        if (IsScrollbarInteraction(source))
        {
            _scrollController.BeginManualNavigation();
            UpdateScrollToBottomButton();
        }

        foreach (var text in MessagesList.GetVisualDescendants().OfType<SelectableTextBlock>())
        {
            if (text != source && !text.IsVisualAncestorOf(source))
                text.ClearSelection();
        }

        foreach (var text in MessagesList.GetVisualDescendants().OfType<CTextBlock>())
        {
            if (text != source && !text.IsVisualAncestorOf(source))
                text.ClearSelection();
        }
    }

    private void OnMessagesPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.Source is not Visual source || !IsScrollbarInteraction(source))
            return;

        Dispatcher.UIThread.Post(() =>
        {
            _scrollController.CompleteManualNavigation(IsNearBottom());
            UpdateScrollToBottomButton();
        }, DispatcherPriority.Background);
    }

    private void OnMessagesPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        // Any wheel gesture is manual navigation. Suspend automatic following until the
        // gesture actually reaches the bottom; otherwise a growing Markdown transcript
        // can fight a downward scroll and recycle the selected text controls underneath it.
        _scrollController.BeginManualNavigation();

        Dispatcher.UIThread.Post(() =>
        {
            _scrollController.CompleteManualNavigation(IsNearBottom());
            UpdateScrollToBottomButton();
        }, DispatcherPriority.Background);
    }

    private static bool IsScrollbarInteraction(Visual source) =>
        source is ScrollBar || source.GetVisualAncestors().OfType<ScrollBar>().Any();

    private async void OnCopyMessageClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { CommandParameter: ChatMessageViewModel message })
            await CopyMessageAsync(message);
    }

    /// <summary>Copies one complete chat bubble. Public so Debug MCP can exercise
    /// the same clipboard path used by the bubble context menu.</summary>
    public async Task CopyMessageAsync(ChatMessageViewModel message)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
            await clipboard.SetTextAsync(message.Text);
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (_observed is not null)
            _observed.CollectionChanged -= OnMessagesChanged;

        if (DataContext is AgentChatViewModel vm)
        {
            _observed = vm.TranscriptMessages;
            _observed.CollectionChanged += OnMessagesChanged;
            _scrollController.FollowLatest();
            ScheduleScrollUpdate();
            UpdateScrollToBottomButton();
        }
        else
        {
            _observed = null;
            ScrollToBottomButton.IsVisible = false;
        }
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            if (DataContext is AgentChatViewModel vm && vm.TranscriptMessages.Count == 0)
                _scrollController.FollowLatest();
            UpdateScrollToBottomButton();
            return;
        }

        if (e.Action is not (NotifyCollectionChangedAction.Add
            or NotifyCollectionChangedAction.Replace
            or NotifyCollectionChangedAction.Remove))
            return;

        if (_scrollController.IsFollowingLatest)
            ScheduleScrollUpdate();
        else
            UpdateScrollToBottomButton();
    }

    private void OnMessagesScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (!_isProgrammaticScroll
            && _scrollController.ShouldFollowLayoutChange(e.ExtentDelta.Y, e.ViewportDelta.Y))
        {
            ScheduleScrollUpdate();
        }

        if (!_isProgrammaticScroll && !_scrollController.IsFollowingLatest
            && Math.Abs(e.OffsetDelta.Y) > 0.5 && IsNearBottom())
            _scrollController.CompleteManualNavigation(isAtBottom: true);

        UpdateScrollToBottomButton();
    }

    private void ScrollMessagesToEnd()
    {
        _scrollToEndDepth++;
        ScrollToEndCallCount++;
        MaxScrollToEndDepth = Math.Max(MaxScrollToEndDepth, _scrollToEndDepth);
        try
        {
            _isProgrammaticScroll = true;
            TranscriptScroll.ScrollToEnd();
            UpdateScrollToBottomButton();
        }
        finally
        {
            // Defer clearing so the ScrollChanged raised by ScrollToEnd still sees the flag.
            Dispatcher.UIThread.Post(() => _isProgrammaticScroll = false, DispatcherPriority.Background);
            _scrollToEndDepth--;
        }
    }

    private void ScheduleScrollUpdate()
    {
        if (_scrollUpdateTimer is not null)
            return;

        // One coalesced pass after layout is sufficient because the transcript window is
        // non-virtualized and completed Markdown controls are immutable.
        _scrollUpdateTimer = DispatcherTimer.RunOnce(() =>
        {
            _scrollUpdateTimer?.Dispose();
            _scrollUpdateTimer = null;
            if (_scrollController.IsFollowingLatest && !IsNearBottom())
                ScrollMessagesToEnd();
            UpdateScrollToBottomButton();
        }, ScrollFollowInterval, DispatcherPriority.Background);
    }

    private bool IsNearBottom()
    {
        var extent = TranscriptScroll.Extent.Height;
        var viewport = TranscriptScroll.Viewport.Height;
        if (extent <= viewport + 1)
            return true;

        var maxOffset = extent - viewport;
        return TranscriptScroll.Offset.Y >= maxOffset - NearBottomThreshold;
    }

    private void UpdateScrollToBottomButton()
    {
        var show = TranscriptScroll.IsVisible
                   && TranscriptScroll.Extent.Height > TranscriptScroll.Viewport.Height + 1
                   && !IsNearBottom();
        if (ScrollToBottomButton.IsVisible != show)
            ScrollToBottomButton.IsVisible = show;
    }

    private void OnShortcutKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.N || e.KeyModifiers != KeyModifiers.Control)
            return;

        e.Handled = true;
        if (DataContext is AgentChatViewModel vm && vm.NewConversationCommand.CanExecute(null))
            vm.NewConversationCommand.Execute(null);
    }

    // Enter sends; Shift+Enter inserts a newline.
    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            return;

        e.Handled = true;
        if (DataContext is not AgentChatViewModel vm)
            return;

        if (vm.IsBusy && vm.SteerCommand.CanExecute(null))
            vm.SteerCommand.Execute(null);
        else if (vm.SendCommand.CanExecute(null))
            vm.SendCommand.Execute(null);
    }

    /// <summary>Moves keyboard focus into the message input box, if it exists yet.</summary>
    public void FocusInput() => InputBox?.Focus();
}
