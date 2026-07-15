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

    private INotifyCollectionChanged? _observed;
    private ScrollViewer? _messagesScroll;
    private bool _stickToBottom = true;
    private bool _isProgrammaticScroll;
    private bool _scrollUpdateScheduled;
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

    /// <summary>Number of currently realized chat bubbles. With virtualization this stays
    /// close to the viewport size even when the transcript contains hundreds of messages.</summary>
    public int RealizedMessageCount => MessagesList.GetVisualDescendants()
        .OfType<Border>()
        .Count(border => border.Classes.Contains("chat-bubble"));

    public int FirstRealizedMessageIndex => MessagesList.GetVisualDescendants()
        .OfType<VirtualizingStackPanel>()
        .FirstOrDefault()?.FirstRealizedIndex ?? -1;

    public int LastRealizedMessageIndex => MessagesList.GetVisualDescendants()
        .OfType<VirtualizingStackPanel>()
        .FirstOrDefault()?.LastRealizedIndex ?? -1;

    private ScrollViewer? MessagesScroll
    {
        get
        {
            AttachMessagesScroll();
            return _messagesScroll;
        }
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        AttachMessagesScroll();
        ScrollMessagesToEnd();
    }

    private void AttachMessagesScroll()
    {
        if (_messagesScroll is not null)
            return;

        var scroll = MessagesList.GetVisualDescendants()
            .OfType<ScrollViewer>()
            .FirstOrDefault(control => !control.Classes.Contains("CodeBlock"));
        if (scroll is null)
            return;

        _messagesScroll = scroll;
        _messagesScroll.ScrollChanged += OnMessagesScrollChanged;
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        if (_messagesScroll is not null)
            _messagesScroll.ScrollChanged -= OnMessagesScrollChanged;
        _messagesScroll = null;
    }

    /// <summary>Scrolls the transcript to the end and resumes following new messages.
    /// Public for Debug MCP and the floating button.</summary>
    public void ScrollToLatest()
    {
        _stickToBottom = true;
        ScrollMessagesToEnd();
        UpdateScrollToBottomButton();
    }

    private void OnScrollToBottomClick(object? sender, RoutedEventArgs e) => ScrollToLatest();

    private void OnMarkdownLayoutUpdated(object? sender, EventArgs e)
    {
        if (sender is Visual markdown)
            EnsureSelectableCodeBlocks(markdown);
    }

    /// <summary>Replaces Markdown.Avalonia.Tight's plain fenced-code text controls with
    /// selectable equivalents while leaving their scroll viewers and styles intact.
    /// Public so Debug MCP can verify the rendered control type.</summary>
    public int EnsureSelectableCodeBlocks() => EnsureSelectableCodeBlocks(MessagesList);

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

    /// <summary>Adds a temporary large transcript, lets the live ListBox lay it out, and
    /// removes it again. The returned counters let Debug MCP verify virtualization without
    /// changing the user's conversation.</summary>
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
        var first = FirstRealizedMessageIndex;
        var last = LastRealizedMessageIndex;
        var bottomButtonHidden = !IsScrollToBottomButtonVisible;

        MessagesScroll?.ScrollToHome();
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
               + $"realized={realized}; range={first}-{last}; "
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
            _stickToBottom = false;
            MessagesScroll?.ScrollToHome();
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

            await Task.Delay(150);
            var atBottom = IsNearBottom();
            var buttonHiddenAfter = !IsScrollToBottomButtonVisible;
            var scroll = MessagesScroll;
            var bottomGap = scroll is null
                ? double.NaN
                : Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height - scroll.Offset.Y);
            return $"updates={updateCount}; buttonVisibleBefore={buttonVisibleBefore}; "
                   + $"atBottom={atBottom}; buttonHiddenAfter={buttonHiddenAfter}; "
                   + $"stickToBottom={_stickToBottom}; bottomGap={bottomGap:F1}; "
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

            var scroll = MessagesScroll
                         ?? throw new InvalidOperationException("The transcript scroll viewer is unavailable.");
            var viewportBefore = scroll.Viewport.Height;
            var gapBefore = Math.Max(0, scroll.Extent.Height - viewportBefore - scroll.Offset.Y);

            InputBox.Text = string.Join(Environment.NewLine,
                Enumerable.Range(1, 12).Select(i => $"composer line {i}"));
            await Task.Delay(150);

            var viewportAfter = scroll.Viewport.Height;
            var gapAfter = Math.Max(0, scroll.Extent.Height - viewportAfter - scroll.Offset.Y);
            return $"viewportBefore={viewportBefore:F1}; viewportAfter={viewportAfter:F1}; "
                   + $"gapBefore={gapBefore:F1}; gapAfter={gapAfter:F1}; "
                   + $"atBottom={IsNearBottom()}; stickToBottom={_stickToBottom}";
        }
        finally
        {
            InputBox.Text = originalInput;
            foreach (var message in added)
                vm.Messages.Remove(message);
        }
    }

    private static int EnsureSelectableCodeBlocks(Visual root)
    {
        var replaced = 0;
        foreach (var scroller in root.GetVisualDescendants()
                     .OfType<ScrollViewer>()
                     .Where(control => control.Classes.Contains("CodeBlock")))
        {
            if (scroller.Content is SelectableTextBlock existingSelectable)
            {
                EnsureCodeBlockHeight(scroller, existingSelectable.Text);
                continue;
            }

            if (scroller.Content is not TextBlock code)
                continue;

            var selectable = new SelectableTextBlock
            {
                Name = "SelectableCodeBlock",
                Text = code.Text,
                TextWrapping = code.TextWrapping,
            };
            foreach (var @class in code.Classes)
                selectable.Classes.Add(@class);

            scroller.Content = selectable;
            EnsureCodeBlockHeight(scroller, selectable.Text);
            replaced++;
        }

        return replaced;
    }

    private static void EnsureCodeBlockHeight(ScrollViewer scroller, string? text)
    {
        // Markdown.Avalonia overlays its horizontal scrollbar. The stylesheet reserves
        // one line for it; grow the viewport for every actual code line so multiline
        // blocks are not clipped down to their first row after becoming selectable.
        var lineCount = string.IsNullOrEmpty(text) ? 1 : text.Count(ch => ch == '\n') + 1;
        scroller.MinHeight = Math.Max(32, lineCount * 16 + 16);
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
            _stickToBottom = false;
            ScheduleScrollUpdate();
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
            _stickToBottom = IsNearBottom();
            UpdateScrollToBottomButton();
        }, DispatcherPriority.Background);
    }

    private void OnMessagesPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (e.Delta.Y > 0)
            _stickToBottom = false;

        Dispatcher.UIThread.Post(() =>
        {
            if (IsNearBottom())
                _stickToBottom = true;
            UpdateScrollToBottomButton();
        }, DispatcherPriority.Background);
    }

    private static bool IsScrollbarInteraction(Visual source) =>
        source is ScrollBar || source.GetVisualAncestors().OfType<ScrollBar>().Any();

    private void OnMessagesSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // ListBox supplies virtualization, but the transcript itself is not a selectable
        // list: selection belongs to the text controls inside each bubble.
        if (MessagesList.SelectedIndex >= 0)
            MessagesList.SelectedIndex = -1;
    }

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
            _observed = vm.Messages;
            _observed.CollectionChanged += OnMessagesChanged;
            _stickToBottom = true;
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
            _stickToBottom = true;
            UpdateScrollToBottomButton();
            return;
        }

        if (e.Action is not (NotifyCollectionChangedAction.Add
            or NotifyCollectionChangedAction.Replace
            or NotifyCollectionChangedAction.Remove))
            return;

        ScheduleScrollUpdate();
    }

    private void OnMessagesScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (MessagesScroll is null)
            return;

        // Content growth and a shrinking viewport (for example, when the multiline
        // composer grows) must both keep the latest message above the composer.
        if (_stickToBottom && (e.ExtentDelta.Y > 0.5 || e.ViewportDelta.Y < -0.5))
        {
            // ScrollToEnd and ScrollIntoView can synchronously raise ScrollChanged. Queue
            // and coalesce the follow-up so streamed layout growth cannot recurse until
            // Windows runs out of stack guard pages.
            ScheduleScrollUpdate();
            return;
        }

        // Only real input handlers turn following off. Virtualized layout can report
        // offset changes in either direction while item heights are being re-estimated,
        // so ScrollChanged alone cannot distinguish those from user scrolling.
        if (!_isProgrammaticScroll && !_stickToBottom
            && Math.Abs(e.OffsetDelta.Y) > 0.5 && IsNearBottom())
            _stickToBottom = true;

        UpdateScrollToBottomButton();
    }

    private void ScrollMessagesToEnd()
    {
        _scrollToEndDepth++;
        ScrollToEndCallCount++;
        MaxScrollToEndDepth = Math.Max(MaxScrollToEndDepth, _scrollToEndDepth);
        try
        {
            // Mark the complete operation, including ScrollIntoView, as programmatic.
            // Virtualization may realize the last row and synchronously change the extent.
            _isProgrammaticScroll = true;
            if (DataContext is AgentChatViewModel vm && vm.Messages.Count > 0)
                MessagesList.ScrollIntoView(vm.Messages.Count - 1);

            if (MessagesScroll is null)
                return;

            MessagesScroll.ScrollToEnd();
            _stickToBottom = true;
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
        if (_scrollUpdateScheduled)
            return;

        _scrollUpdateScheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            _scrollUpdateScheduled = false;
            if (_stickToBottom)
                ScrollMessagesToEnd();
            else
                UpdateScrollToBottomButton();
        }, DispatcherPriority.Background);
    }

    private bool IsNearBottom()
    {
        if (MessagesScroll is null)
            return true;

        var extent = MessagesScroll.Extent.Height;
        var viewport = MessagesScroll.Viewport.Height;
        if (extent <= viewport + 1)
            return true;

        var maxOffset = extent - viewport;
        return MessagesScroll.Offset.Y >= maxOffset - NearBottomThreshold;
    }

    private void UpdateScrollToBottomButton()
    {
        var show = MessagesScroll is { IsVisible: true }
                   && MessagesScroll.Extent.Height > MessagesScroll.Viewport.Height + 1
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
