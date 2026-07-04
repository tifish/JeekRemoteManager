using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Jeek.Avalonia.Localization;
using JeekRemoteManager.ViewModels;

namespace JeekRemoteManager.Views;

/// <summary>
/// The SFTP file browser panel hosted under a terminal tab. All logic lives in
/// <see cref="FileBrowserViewModel"/>; this code-behind supplies the pieces that
/// need a control: pickers/dialogs, the live selection, clipboard, and drag-drop.
/// </summary>
public partial class FileBrowserView : UserControl
{
    private string _searchPrefix = "";
    private DateTime _lastSearchInput;

    public FileBrowserView()
    {
        InitializeComponent();

        // Block DataContext inheritance from the terminal view (whose context is
        // the main window view model) until the real view model is assigned.
        DataContext = null;
        DataContextChanged += (_, _) => WireViewModel();

        DragDrop.SetAllowDrop(FileList, true);
        FileList.AddHandler(DragDrop.DragOverEvent, OnFileListDragOver);
        FileList.AddHandler(DragDrop.DropEvent, OnFileListDrop);

        // Type-ahead: printable keys locate entries by name prefix instead of
        // falling through to whatever else handles keyboard input.
        FileList.AddHandler(TextInputEvent, OnFileListTextInput, RoutingStrategies.Tunnel);
        // Tunnel: ListBox's own key handling consumes Enter (selection commit)
        // before bubbling handlers would see it, so intercept on the way down.
        FileList.AddHandler(KeyDownEvent, OnFileListKeyDown, RoutingStrategies.Tunnel);
    }

    private FileBrowserViewModel? Vm => DataContext as FileBrowserViewModel;

    /// <summary>Moves keyboard focus into the file list (called when the panel opens),
    /// so typing locates entries instead of feeding the SSH terminal. Selects the
    /// first row when nothing is selected, giving keyboard navigation an anchor.</summary>
    public void FocusList()
    {
        if (FileList.ItemCount > 0 && FileList.SelectedIndex < 0)
            FileList.SelectedIndex = 0;
        FileList.Focus();
    }

    private void OnFileListKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm)
            return;

        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                vm.OpenSelectedCommand.Execute(null);
                break;
            case Key.Back:
                e.Handled = true;
                vm.GoUpCommand.Execute(null);
                break;
            // The ListBox only scrolls the viewport for these; move the selection
            // with them, like a file manager.
            case Key.Home:
                e.Handled = true;
                MoveSelectionTo(0);
                break;
            case Key.End:
                e.Handled = true;
                MoveSelectionTo(vm.Items.Count - 1);
                break;
            case Key.PageUp:
                e.Handled = true;
                MoveSelectionTo(Math.Max(0, FileList.SelectedIndex) - VisibleRowCount());
                break;
            case Key.PageDown:
                e.Handled = true;
                MoveSelectionTo(Math.Max(0, FileList.SelectedIndex) + VisibleRowCount());
                break;
        }
    }

    private void MoveSelectionTo(int index)
    {
        var count = FileList.ItemCount;
        if (count == 0)
            return;

        FileList.SelectedIndex = Math.Clamp(index, 0, count - 1);
        FileList.ScrollIntoView(FileList.SelectedIndex);
        KeepFocusInList();
    }

    /// <summary>Rows that fit in the list viewport, minus one so a page jump keeps
    /// one row of overlap for orientation.</summary>
    private int VisibleRowCount()
    {
        var container = FileList.ContainerFromIndex(Math.Max(0, FileList.SelectedIndex))
                        ?? FileList.ContainerFromIndex(0);
        var rowHeight = container is { Bounds.Height: > 0 } realized ? realized.Bounds.Height : 28.0;
        return Math.Max(1, (int)(FileList.Bounds.Height / rowHeight) - 1);
    }

    /// <summary>Scrolling a virtualized list can recycle the row container that held
    /// focus, dropping focus to the window's first focusable control (a toolbar
    /// button). Re-anchor focus on the list when that happens.</summary>
    private void KeepFocusInList()
    {
        Dispatcher.UIThread.Post(() =>
        {
            var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
            var insideList = focused is Avalonia.Visual visual
                && (ReferenceEquals(visual, FileList) || FileList.IsVisualAncestorOf(visual));
            if (!insideList)
                FileList.Focus();
        }, DispatcherPriority.Background);
    }

    private void OnFileListTextInput(object? sender, TextInputEventArgs e)
    {
        var text = e.Text;
        if (string.IsNullOrEmpty(text) || Vm is not { } vm || vm.Items.Count == 0)
            return;

        e.Handled = true;

        var now = DateTime.UtcNow;
        if ((now - _lastSearchInput).TotalMilliseconds > 1000)
            _searchPrefix = "";
        _lastSearchInput = now;

        // Repeating the same single key cycles through entries with that initial
        // (the Explorer convention); anything else extends the prefix.
        if (_searchPrefix.Length == 1
            && string.Equals(_searchPrefix, text, StringComparison.OrdinalIgnoreCase))
        {
            SelectNextByPrefix(_searchPrefix, startAfterSelection: true);
            return;
        }

        _searchPrefix += text;
        SelectNextByPrefix(_searchPrefix, startAfterSelection: false);
    }

    private void SelectNextByPrefix(string prefix, bool startAfterSelection)
    {
        if (Vm is not { } vm)
            return;

        var items = vm.Items;
        var start = startAfterSelection ? FileList.SelectedIndex + 1 : 0;
        for (var offset = 0; offset < items.Count; offset++)
        {
            var index = (start + offset) % items.Count;
            if (!items[index].Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            FileList.SelectedIndex = index;
            FileList.ScrollIntoView(index);
            KeepFocusInList();
            return;
        }
    }

    private void WireViewModel()
    {
        if (Vm is not { } vm)
            return;

        vm.GetSelection = () =>
            FileList.SelectedItems?.OfType<RemoteFileEntry>().ToList()
            ?? (IReadOnlyList<RemoteFileEntry>)Array.Empty<RemoteFileEntry>();
        vm.PickUploadFilesAsync = PickUploadFilesAsync;
        vm.PickDownloadFolderAsync = PickDownloadFolderAsync;
        vm.PromptAsync = PromptAsync;
        vm.ConfirmAsync = ConfirmAsync;
        vm.SetClipboardTextAsync = text =>
            TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(text) ?? Task.CompletedTask;
        vm.IsListFocused = () =>
            TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is Avalonia.Visual focused
            && (ReferenceEquals(focused, FileList) || FileList.IsVisualAncestorOf(focused));
        vm.RequestFocusList = FocusList;
        vm.RequestSelectEntry = entry =>
        {
            FileList.SelectedItem = entry;
            FileList.ScrollIntoView(entry);
        };
        vm.RequestFocusPathInput = () =>
        {
            PathBox.Focus();
            PathBox.SelectAll();
        };
    }

    private void OnPathBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Vm is not { } vm)
            return;
        e.Handled = true;
        vm.NavigateToInputCommand.Execute(null);
    }

    private void OnFileListDoubleTapped(object? sender, TappedEventArgs e)
    {
        // Only a double-click on an actual row opens it; empty space is inert.
        if ((e.Source as Avalonia.Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is null)
            return;
        Vm?.OpenSelectedCommand.Execute(null);
    }

    private void OnFileListDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = Vm is not null && e.DataTransfer.Contains(DataFormat.File)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnFileListDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (Vm is not { } vm || !e.DataTransfer.Contains(DataFormat.File))
            return;

        var paths = (e.DataTransfer.TryGetFiles() ?? [])
            .Select(item => item.TryGetLocalPath())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .ToList();
        if (paths.Count > 0)
            _ = vm.QueueUploadLocalPathsAsync(paths);
    }

    private async Task<IReadOnlyList<string>> PickUploadFilesAsync()
    {
        if (TopLevel.GetTopLevel(this) is not { } topLevel)
            return Array.Empty<string>();

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Localizer.Get("FileBrowserPickUploadFiles"),
            AllowMultiple = true,
        });
        return files
            .Select(file => file.TryGetLocalPath())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .ToArray();
    }

    private async Task<string?> PickDownloadFolderAsync()
    {
        if (TopLevel.GetTopLevel(this) is not { } topLevel)
            return null;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Localizer.Get("FileBrowserPickDownloadFolder"),
            AllowMultiple = false,
        });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    private Task<bool> ConfirmAsync(string title, string message)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
            return Task.FromResult(false);

        var tcs = new TaskCompletionSource<bool>();
        var yes = new Button { Content = Localizer.Get("DialogYes"), MinWidth = 80, IsDefault = true };
        var no = new Button { Content = Localizer.Get("DialogNo"), MinWidth = 80, IsCancel = true };

        var dialog = new Window
        {
            Title = title,
            Width = 380,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(16),
                Spacing = 16,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { yes, no },
                    },
                },
            },
        };

        yes.Click += (_, _) => { tcs.TrySetResult(true); dialog.Close(); };
        no.Click += (_, _) => { tcs.TrySetResult(false); dialog.Close(); };
        dialog.Closed += (_, _) => tcs.TrySetResult(false);

        dialog.ShowDialog(owner);
        return tcs.Task;
    }

    private Task<string?> PromptAsync(string title, string message, string initial)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
            return Task.FromResult<string?>(null);

        var tcs = new TaskCompletionSource<string?>();
        var input = new TextBox { Text = initial };
        var ok = new Button { Content = Localizer.Get("DialogOk"), MinWidth = 80, IsDefault = true };
        var cancel = new Button { Content = Localizer.Get("DialogCancel"), MinWidth = 80, IsCancel = true };

        var dialog = new Window
        {
            Title = title,
            Width = 380,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(16),
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = message },
                    input,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { ok, cancel },
                    },
                },
            },
        };

        ok.Click += (_, _) => { tcs.TrySetResult(input.Text); dialog.Close(); };
        cancel.Click += (_, _) => { tcs.TrySetResult(null); dialog.Close(); };
        dialog.Closed += (_, _) => tcs.TrySetResult(null);
        dialog.Opened += (_, _) => { input.Focus(); input.SelectAll(); };

        dialog.ShowDialog(owner);
        return tcs.Task;
    }
}
