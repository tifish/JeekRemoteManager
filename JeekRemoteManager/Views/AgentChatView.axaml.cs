using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
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
        InputBox.AddHandler(KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);
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
