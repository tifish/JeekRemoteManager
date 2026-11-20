using Avalonia.Interactivity;
using JeekRemoteManager.Services;
using JeekRemoteManager.ViewModels;

namespace JeekRemoteManager.Views;

public partial class MainWindow
{
    private BackgroundPortForwardWindow? _backgroundPortForwardWindow;

    private void OnBackgroundPortForwardManagerClick(object? sender, RoutedEventArgs e)
    {
        if (_backgroundPortForwardWindow is { } existing)
        {
            existing.WindowState = Avalonia.Controls.WindowState.Normal;
            existing.Activate();
            return;
        }
        if (DataContext is not MainWindowViewModel vm)
            return;

        IEnumerable<BackgroundPortForwardWindow.ConnectionChoice> Choices(IEnumerable<TreeNodeViewModel> nodes)
        {
            foreach (var node in nodes)
            {
                if (node.IsRecent)
                    continue;
                if (node.IsFolder)
                {
                    foreach (var choice in Choices(node.Children))
                        yield return choice;
                }
                else if (node.Connection is { IsSsh: true } connection && !string.IsNullOrWhiteSpace(connection.Host))
                    yield return new(connection.ConnectionId, TreePath(node.FullPath), connection.Host.Trim());
            }
        }

        string TreePath(string file) =>
            Path.GetRelativePath(vm.Store.RootPath, file)[..^ConnectionStore.FileExtension.Length].Replace('\\', '/');

        SshForwardTransport.Plan Prepare(string connectionId, string host, int port)
        {
            var saved = vm.Store.FindById(connectionId);
            if (saved?.Connection is not { IsSsh: true } connection
                || !string.Equals(connection.Host.Trim(), host, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The saved SSH connection has changed.");
            return SshForwardTransport.Prepare(connection, TreePath(saved.Path), port,
                vm.Store.TryLoadById, ActivateMainWindow);
        }

        var window = new BackgroundPortForwardWindow(BackgroundPortForwards.Shared,
            () => Choices(vm.Nodes).ToArray(), Prepare, vm.SelectedNode?.Connection?.ConnectionId);
        _backgroundPortForwardWindow = window;
        window.Closed += (_, _) => _backgroundPortForwardWindow = null;
        window.Show(this);
    }
}
