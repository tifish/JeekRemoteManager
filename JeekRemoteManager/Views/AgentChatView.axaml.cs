using System.Collections.Specialized;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ColorTextBlock.Avalonia;
using JeekRemoteManager.ViewModels;

namespace JeekRemoteManager.Views;

public partial class AgentChatView : UserControl
{
    private INotifyCollectionChanged? _observed;

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
