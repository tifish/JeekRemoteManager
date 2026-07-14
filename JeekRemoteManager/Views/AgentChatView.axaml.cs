using System;
using System.Collections.Specialized;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
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
    private INotifyCollectionChanged? _observed;

    /// <summary>Raised by the panel's own close button; the hosting TerminalView
    /// collapses the panel.</summary>
    public event EventHandler? CloseRequested;

    private void OnCloseClick(object? sender, RoutedEventArgs e) =>
        CloseRequested?.Invoke(this, EventArgs.Empty);

    public AgentChatView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;

        // Handle Enter on the tunnel (preview) route so it fires before the multi-line
        // TextBox consumes Enter to insert a newline.
        AddHandler(KeyDownEvent, OnShortcutKeyDown, RoutingStrategies.Tunnel);
        InputBox.AddHandler(KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);

        // Each bubble keeps its own text selection; starting a selection in one bubble
        // should drop the highlight left in the others.
        MessagesList.AddHandler(PointerPressedEvent, OnMessagesPointerPressed, RoutingStrategies.Tunnel);

        // Selectable text and Markdown controls have their own selection-only copy menu.
        // Intercept the request before those children so right-clicking anywhere in a
        // bubble consistently opens the whole-message menu instead.
        MessagesList.AddHandler(InputElement.ContextRequestedEvent, OnMessageContextRequested,
            RoutingStrategies.Tunnel);
    }

    private void OnMarkdownLayoutUpdated(object? sender, EventArgs e)
    {
        if (sender is Visual markdown)
            EnsureSelectableCodeBlocks(markdown);
    }

    /// <summary>Replaces Markdown.Avalonia.Tight's plain fenced-code text controls with
    /// selectable equivalents while leaving their scroll viewers and styles intact.
    /// Public so Debug MCP can verify the rendered control type.</summary>
    public int EnsureSelectableCodeBlocks() => EnsureSelectableCodeBlocks(MessagesList);

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
        }
        else
        {
            _observed = null;
        }
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
            Dispatcher.UIThread.Post(() => MessagesScroll?.ScrollToEnd(), DispatcherPriority.Background);
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
        if (DataContext is AgentChatViewModel vm && vm.SendCommand.CanExecute(null))
            vm.SendCommand.Execute(null);
    }

    /// <summary>Moves keyboard focus into the message input box, if it exists yet.</summary>
    public void FocusInput() => InputBox?.Focus();
}
