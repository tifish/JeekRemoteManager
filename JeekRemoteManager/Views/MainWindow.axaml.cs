using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Jeek.Avalonia.Localization;
using JeekRemoteManager.Models;
using JeekRemoteManager.Services;
using JeekRemoteManager.ViewModels;

namespace JeekRemoteManager.Views;

public partial class MainWindow : Window
{
    private TreeNodeViewModel? _lastToggledFolder;
    private bool _lastToggledFolderExpanded;
    private bool _windowSizeRestored;
    private bool _ignoreWindowSizeChange;
    private bool _canPersistWindowSize;
    private double _defaultMinWidth;
    private double _defaultMinHeight;
    private DispatcherTimer? _windowSizeSaveTimer;

    // Auto-locks "Show password" after a stretch of inactivity in the main
    // window, so a revealed password isn't left on screen when the user
    // walks away. Any pointer or key input resets the timer.
    private static readonly TimeSpan ShowPasswordIdleTimeout = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan WindowSizeSaveDelay = TimeSpan.FromMilliseconds(500);
    private DispatcherTimer? _showPasswordIdleTimer;

    public MainWindow()
    {
        InitializeComponent();
        _defaultMinWidth = MinWidth;
        _defaultMinHeight = MinHeight;
        Tree.AddHandler(
            InputElement.PointerPressedEvent,
            OnTreePointerPressed,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        Tree.AddHandler(
            InputElement.DoubleTappedEvent,
            OnTreeDoubleTapped,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        AddHandler(
            InputElement.PointerPressedEvent,
            OnPointerActivity,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        AddHandler(
            InputElement.PointerMovedEvent,
            OnPointerActivity,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        AddHandler(
            InputElement.KeyDownEvent,
            OnKeyActivity,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        DataContextChanged += (_, _) => WireUp();
        SizeChanged += OnWindowSizeChanged;
        Opened += (_, _) =>
        {
            WireUp();
            EnsureWindowFitsCurrentScreen();
            _canPersistWindowSize = true;
            // Put keyboard focus on the restored selection so the user can act on
            // it immediately. RequestFocusTree fired during construction is a no-op
            // because the callback isn't wired up until the window opens.
            FocusSelectedTreeItem();
        };
        Closing += (_, _) =>
            FlushCurrentSettingsState();
    }

    public void FlushCurrentSettingsState()
    {
        var vm = DataContext as MainWindowViewModel;
        // Sync pending UI state into AppSettings before deciding whether the file changed.
        vm?.SaveLastSelectedConnection();
        SaveCurrentWindowSize(vm);
        vm?.FlushSettings();
    }

    private void WireUp()
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        vm.Clipboard = Clipboard;
        vm.ConfirmAsync = ConfirmAsync;
        vm.PromptAsync = PromptAsync;
        vm.PickKeyFileAsync = PickKeyFileAsync;
        vm.PickSettingsAsync = PickSettingsAsync;
        vm.PickFolderAsync = path => PickFolderAsync(path);
        vm.OpenSshTerminalAsync = async (connection, sourcePath) =>
        {
            _ = await EnsureSshTerminalAsync(connection, sourcePath);
        };
        vm.EnsureSshTerminalAsync = EnsureSshTerminalAsync;
        vm.ApplyTerminalFontSize = ApplyTerminalFontToOpenTabs;
        vm.ConfirmHostKeyTrust = HostKeyDialog.PromptTrust;
        vm.RequestFocusTree = FocusSelectedTreeItem;
        vm.PropertyChanged -= OnViewModelPropertyChanged;
        vm.PropertyChanged += OnViewModelPropertyChanged;
        RestoreWindowSize(vm);
    }

    private void RestoreWindowSize(MainWindowViewModel vm)
    {
        if (_windowSizeRestored)
            return;

        _windowSizeRestored = true;
        if (!vm.TryGetSavedMainWindowSize(out var width, out var height))
        {
            width = Width;
            height = Height;
        }

        _ignoreWindowSizeChange = true;
        try
        {
            var size = ClampWindowSizeToCurrentScreen(width, height);
            Width = size.Width;
            Height = size.Height;
        }
        finally
        {
            _ignoreWindowSizeChange = false;
        }
    }

    private void EnsureWindowFitsCurrentScreen()
    {
        _ignoreWindowSizeChange = true;
        try
        {
            var size = ClampWindowSizeToCurrentScreen(Bounds.Width, Bounds.Height);
            Width = size.Width;
            Height = size.Height;
        }
        finally
        {
            _ignoreWindowSizeChange = false;
        }
    }

    private void OnWindowSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (!_canPersistWindowSize
            || !_windowSizeRestored
            || _ignoreWindowSizeChange
            || WindowState != WindowState.Normal)
            return;

        ScheduleWindowSizeSave();
    }

    private void ScheduleWindowSizeSave()
    {
        if (_windowSizeSaveTimer is null)
        {
            _windowSizeSaveTimer = new DispatcherTimer { Interval = WindowSizeSaveDelay };
            _windowSizeSaveTimer.Tick += (_, _) =>
            {
                _windowSizeSaveTimer!.Stop();
                SaveCurrentWindowSize(DataContext as MainWindowViewModel);
            };
        }

        _windowSizeSaveTimer.Stop();
        _windowSizeSaveTimer.Start();
    }

    private void SaveCurrentWindowSize(MainWindowViewModel? vm)
    {
        _windowSizeSaveTimer?.Stop();

        if (vm is null
            || !_canPersistWindowSize
            || !_windowSizeRestored
            || WindowState != WindowState.Normal)
            return;

        vm.SaveMainWindowSize(Bounds.Width, Bounds.Height);
    }

    private Size ClampWindowSizeToCurrentScreen(double width, double height)
    {
        if (!double.IsFinite(width) || width <= 0)
            width = Width;
        if (!double.IsFinite(height) || height <= 0)
            height = Height;

        if (TryGetCurrentScreenWorkingSize(out var workingSize))
        {
            MinWidth = Math.Min(_defaultMinWidth, workingSize.Width);
            MinHeight = Math.Min(_defaultMinHeight, workingSize.Height);
            width = Math.Min(width, workingSize.Width);
            height = Math.Min(height, workingSize.Height);
        }

        width = ClampWindowDimension(width, MinWidth);
        height = ClampWindowDimension(height, MinHeight);
        return new Size(width, height);
    }

    private bool TryGetCurrentScreenWorkingSize(out Size workingSize)
    {
        workingSize = default;

        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null)
            return false;

        var scaling = screen.Scaling > 0 ? screen.Scaling : 1;
        workingSize = new Size(screen.WorkingArea.Width / scaling, screen.WorkingArea.Height / scaling);
        return workingSize.Width > 0 && workingSize.Height > 0;
    }

    private static double ClampWindowDimension(double value, double minimum) =>
        double.IsFinite(minimum) && minimum > 0 ? Math.Max(value, minimum) : value;

    private Task<TerminalScriptSession?> EnsureSshTerminalAsync(Connection connection, string? sourcePath) =>
        EnsureSshTerminalAsync(connection, sourcePath, forceNew: false);

    private Task<TerminalScriptSession?> EnsureSshTerminalAsync(Connection connection, string? sourcePath, bool forceNew)
    {
        if (!forceNew)
        {
            var existing = FindTerminalTab(connection, sourcePath);
            if (existing is not null)
            {
                RightTabs.SelectedItem = existing.Value.Tab;
                existing.Value.View.FocusTerminal();
                return Task.FromResult<TerminalScriptSession?>(CreateTerminalScriptSession(existing.Value.View, existing.Value.Tab));
            }
        }

        var view = new TerminalView();
        var tab = new TabItem
        {
            Header = BuildTerminalTabHeader(connection, out var closeButton),
            Content = view,
        };
        closeButton.Click += (_, _) => CloseTerminalTab(tab);
        tab.ContextMenu = BuildTerminalTabContextMenu(connection, tab);

        // Middle-click or double-click on the tab header closes it, matching the
        // close button and the context menu's Close item.
        tab.AddHandler(
            PointerPressedEvent,
            OnTerminalTabPointerPressed,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        tab.DoubleTapped += (_, _) => CloseTerminalTab(tab);

        RightTabs.Items.Add(tab);
        RightTabs.SelectedItem = tab;

        view.SetFontSize((DataContext as MainWindowViewModel)?.TerminalFontSize ?? 14);
        view.Start(connection, sourcePath);
        return Task.FromResult<TerminalScriptSession?>(CreateTerminalScriptSession(view, tab));
    }

    private TerminalScriptSession CreateTerminalScriptSession(TerminalView view, TabItem tab) =>
        new(
            view.Connection!,
            view.SourcePath,
            view.WaitUntilConnectedAsync,
            view.RunScriptAsync,
            () =>
            {
                RightTabs.SelectedItem = tab;
                view.FocusTerminal();
            },
            view.ShowScriptPanel,
            view.HideScriptPanel);

    private (TerminalView View, TabItem Tab)? FindTerminalTab(Connection connection, string? sourcePath)
    {
        foreach (var item in RightTabs.Items)
        {
            if (item is not TabItem { Content: TerminalView view } tab)
                continue;

            if (!string.IsNullOrEmpty(sourcePath)
                && !string.IsNullOrEmpty(view.SourcePath)
                && PathEquals(view.SourcePath, sourcePath))
            {
                return (view, tab);
            }

            if (string.IsNullOrEmpty(sourcePath)
                && ReferenceEquals(view.Connection, connection))
            {
                return (view, tab);
            }
        }

        return null;
    }

    private static bool PathEquals(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    // Right-click menu on an SSH terminal tab: install the local public key on the
    // host (ssh-copy-id), or run one of the connection's SSH scripts.
    private ContextMenu BuildTerminalTabContextMenu(Connection connection, TabItem tab)
    {
        var copyKey = new MenuItem { Header = Localizer.Get("CopyPublicKeyToServer") };
        copyKey.Click += async (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                RightTabs.SelectedItem = tab;
                if (tab.Content is TerminalView view)
                {
                    view.FocusTerminal();
                    await vm.CopyPublicKeyToServerAsync(connection, key => view.InstallPublicKeyAsync(key));
                }
                else
                {
                    await vm.CopyPublicKeyToServerAsync(connection);
                }
            }
        };

        var runScript = new MenuItem { Header = Localizer.Get("RunScript") };
        runScript.Click += (_, _) =>
        {
            // Let the context menu close first, then open the script-suite chooser.
            Dispatcher.UIThread.Post(
                () => ShowTerminalScriptChooser(tab),
                DispatcherPriority.Background);
        };

        var close = new MenuItem { Header = Localizer.Get("Close") };
        close.Click += (_, _) => CloseTerminalTab(tab);

        var menu = new ContextMenu();
        menu.Items.Add(copyKey);
        menu.Items.Add(runScript);
        menu.Items.Add(new Separator());
        menu.Items.Add(close);
        return menu;
    }

    // Mirrors OnRunScriptMenuClick but targets the terminal tab's connection and
    // shows the script panel over that same terminal.
    private void ShowTerminalScriptChooser(TabItem tab)
        => ShowTerminalScriptChooser(tab, tab);

    private void ShowTerminalScriptChooser(TabItem tab, Control? anchor)
    {
        if (DataContext is not MainWindowViewModel vm
            || tab.Content is not TerminalView view)
            return;

        var session = CreateTerminalScriptSession(view, tab);
        var choices = vm.PrepareScriptSuiteChoicesForTerminal(session);
        if (choices.Count == 0)
            return;

        if (choices.Count == 1)
        {
            vm.OpenScriptSuiteChoice(choices[0]);
            ShowScriptPanelInTerminal(session);
            return;
        }

        var flyout = new MenuFlyout();
        foreach (var choice in choices)
        {
            var item = new MenuItem { Header = choice.ToString() };
            item.Click += (_, _) =>
            {
                vm.OpenScriptSuiteChoice(choice);
                ShowScriptPanelInTerminal(session);
            };
            flyout.Items.Add(item);
        }

        flyout.ShowAt(anchor ?? tab);
    }

    private void ApplyTerminalFontToOpenTabs(int size)
    {
        foreach (var item in RightTabs.Items)
            if (item is TabItem { Content: TerminalView view })
                view.SetFontSize(size);
    }

    // Tab title stays the connection name; the remote OSC title does not override it.
    private static Control BuildTerminalTabHeader(Connection connection, out Button closeButton)
    {
        var icon = new TextBlock
        {
            Text = "\uE756",
            VerticalAlignment = VerticalAlignment.Center,
        };
        icon.Classes.Add("icon");

        var title = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(connection.Name) ? connection.Host : connection.Name,
            MaxWidth = 180,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        title.Classes.Add("tab-label");

        closeButton = new Button
        {
            Content = new TextBlock
            {
                Text = "\uE711", // Segoe MDL2 ChromeClose
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
            },
            VerticalAlignment = VerticalAlignment.Center,
        };
        closeButton.Classes.Add("tab-close");
        ToolTip.SetTip(closeButton, Localizer.Get("Close"));

        var content = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            ColumnSpacing = 7,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(icon, 0);
        Grid.SetColumn(title, 1);
        Grid.SetColumn(closeButton, 2);
        content.Children.Add(icon);
        content.Children.Add(title);
        content.Children.Add(closeButton);

        var pill = new Border { Child = content };
        pill.Classes.Add("tab-pill");
        return pill;
    }

    // Middle-clicking anywhere on a terminal tab closes it.
    private void OnTerminalTabPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is TabItem tab
            && e.GetCurrentPoint(tab).Properties.IsMiddleButtonPressed)
        {
            CloseTerminalTab(tab);
            e.Handled = true;
        }
    }

    private void CloseTerminalTab(TabItem tab)
    {
        // Closing the active tab moves selection to the previous (left) tab, like a
        // normal tabbed editor. Closing a background tab leaves the selection alone.
        var wasSelected = ReferenceEquals(RightTabs.SelectedItem, tab);
        var index = RightTabs.Items.IndexOf(tab);

        if (tab.Content is TerminalView view)
            view.Close();

        RightTabs.Items.Remove(tab);

        if (wasSelected && RightTabs.Items.Count > 0)
            RightTabs.SelectedIndex = Math.Clamp(index - 1, 0, RightTabs.Items.Count - 1);
    }

    // When a terminal tab becomes active, give it keyboard focus so typing goes
    // straight to the remote shell.
    private void OnRightTabsSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Use sender, not the RightTabs field: this can fire during XAML init
        // (the TabControl auto-selects the first tab) before the field is assigned.
        var view = (sender as TabControl)?.SelectedItem is TabItem { Content: TerminalView v } ? v : null;

        // The font-size buttons are only relevant while a terminal tab is active.
        if (DataContext is MainWindowViewModel vm)
            vm.IsTerminalActive = view is not null;

        view?.FocusTerminal();
    }

    // The right-click already selected the connection, which binds its editor.
    // Bring the editor tab to the front so the editor is visible even when a
    // terminal tab is currently active.
    private void OnEditMenuClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel { SelectedNode: { IsConnection: true } })
            return;

        RightTabs.SelectedItem = EditorTab;
    }

    private void OnRunScriptTerminalToolbarClick(object? sender, RoutedEventArgs e)
    {
        if (RightTabs.SelectedItem is not TabItem { Content: TerminalView } tab)
        {
            return;
        }

        ShowTerminalScriptChooser(tab, sender as Control);
        e.Handled = true;
    }

    private async void OnCopyPublicKeyTerminalToolbarClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm
            || RightTabs.SelectedItem is not TabItem { Content: TerminalView view }
            || view.Connection is not { } connection)
        {
            return;
        }

        view.FocusTerminal();
        await vm.CopyPublicKeyToServerAsync(connection, key => view.InstallPublicKeyAsync(key));
        e.Handled = true;
    }

    private async void OnRunScriptMenuClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || sender is not Control anchor)
            return;

        if (vm.SelectedNode is not { IsConnection: true, Connection: not null } node)
            return;

        var choices = vm.PrepareScriptSuiteChoicesForSelectedConnection();
        if (choices.Count == 0)
            return;

        if (choices.Count == 1)
        {
            await OpenScriptPanelInNewTerminalAsync(node, choices[0]);
            e.Handled = true;
            return;
        }

        var flyout = new MenuFlyout();
        foreach (var choice in choices)
        {
            var item = new MenuItem
            {
                Header = choice.ToString(),
            };
            item.Click += async (_, _) =>
            {
                await OpenScriptPanelInNewTerminalAsync(node, choice);
            };
            flyout.Items.Add(item);
        }

        flyout.ShowAt(anchor);
        e.Handled = true;
    }

    private async Task OpenScriptPanelInNewTerminalAsync(TreeNodeViewModel node, ScriptSuiteChoiceViewModel choice)
    {
        if (DataContext is not MainWindowViewModel vm || node.Connection is null)
            return;

        var session = await EnsureSshTerminalAsync(node.Connection, node.FullPath, forceNew: true);
        if (session is null)
            return;

        var choices = vm.PrepareScriptSuiteChoicesForTerminal(session);
        var terminalChoice = choices.FirstOrDefault(c =>
            string.Equals(c.Suite.RelativePath, choice.Suite.RelativePath, StringComparison.OrdinalIgnoreCase));
        vm.OpenScriptSuiteChoice(terminalChoice ?? choice);
        ShowScriptPanelInTerminal(session);
    }

    private void ShowScriptPanelInTerminal(TerminalScriptSession session)
    {
        HideScriptPanels();
        session.Activate();
        session.ShowScriptPanel();
    }

    private void HideScriptPanels()
    {
        foreach (var item in RightTabs.Items)
            if (item is TabItem { Content: TerminalView view })
                view.HideScriptPanel();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.ShowPassword))
            UpdateShowPasswordIdleTimer();
        else if (e.PropertyName == nameof(MainWindowViewModel.ScriptPanel)
                 && sender is MainWindowViewModel { ScriptPanel: null })
            HideScriptPanels();
    }

    /// <summary>
    /// Toggling "Show password" on requires re-entering the master password;
    /// toggling off is unconditional. We bind IsChecked one-way to the VM, so
    /// the visual flip the user just performed has to be reverted manually here
    /// when we either send them to verification or reject the change.
    /// </summary>
    private async void OnShowPasswordClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox box || DataContext is not MainWindowViewModel vm)
            return;

        var desired = box.IsChecked == true;
        if (!desired)
        {
            vm.ShowPassword = false;
            return;
        }

        // Snap the box back to the VM's current (unchecked) state until verified;
        // the binding can't do this for us because it's OneWay.
        box.IsChecked = vm.ShowPassword;

        var master = MasterKeyService.Current;
        if (master is null || !master.IsUnlocked)
            return;

        var verified = await MasterPasswordDialog.ShowVerifyAsync(
            this,
            Localizer.Get("MasterUnlockTitle"),
            Localizer.Get("MasterRevealPrompt"),
            master.VerifyPassword);

        if (verified)
            vm.ShowPassword = true;
    }

    private void OnPointerActivity(object? sender, PointerEventArgs e) => NoteUserActivity();

    private void OnKeyActivity(object? sender, KeyEventArgs e) => NoteUserActivity();

    private void NoteUserActivity()
    {
        if (DataContext is MainWindowViewModel { ShowPassword: true })
            RestartShowPasswordIdleTimer();
    }

    private void UpdateShowPasswordIdleTimer()
    {
        if (DataContext is MainWindowViewModel { ShowPassword: true })
            RestartShowPasswordIdleTimer();
        else
            StopShowPasswordIdleTimer();
    }

    private void RestartShowPasswordIdleTimer()
    {
        if (_showPasswordIdleTimer is null)
        {
            _showPasswordIdleTimer = new DispatcherTimer { Interval = ShowPasswordIdleTimeout };
            _showPasswordIdleTimer.Tick += OnShowPasswordIdleElapsed;
        }
        _showPasswordIdleTimer.Stop();
        _showPasswordIdleTimer.Start();
    }

    private void StopShowPasswordIdleTimer() => _showPasswordIdleTimer?.Stop();

    private void OnShowPasswordIdleElapsed(object? sender, System.EventArgs e)
    {
        StopShowPasswordIdleTimer();
        if (DataContext is MainWindowViewModel vm)
            vm.ShowPassword = false;
    }

    /// <summary>
    /// Focuses the TreeViewItem container of the current selection so subsequent
    /// keystrokes (Enter, F2, Delete, Ctrl+C/X/V) hit the new item. Falls back
    /// to focusing the TreeView itself if the container isn't realized yet.
    /// </summary>
    private void FocusSelectedTreeItem()
    {
        // Defer until after the TreeView has materialised the container.
        Dispatcher.UIThread.Post(() =>
        {
            var item = Tree.SelectedItem;
            if (item != null)
            {
                var container = FindTreeViewItem(Tree, item);
                if (container != null)
                {
                    container.Focus();
                    return;
                }
            }
            Tree.Focus();
        }, DispatcherPriority.Background);
    }

    private static TreeViewItem? FindTreeViewItem(ItemsControl items, object data)
    {
        foreach (var visual in items.GetVisualDescendants())
        {
            if (visual is TreeViewItem tvi && ReferenceEquals(tvi.DataContext, data))
                return tvi;
        }
        return null;
    }

    private void OnTreeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        var hitItem = e.Source is Visual source
            ? source.FindAncestorOfType<TreeViewItem>(includeSelf: true)
            : null;

        if (hitItem?.DataContext is TreeNodeViewModel { IsFolder: true } folder)
        {
            if (ReferenceEquals(folder, _lastToggledFolder))
            {
                Dispatcher.UIThread.Post(
                    () => folder.IsExpanded = _lastToggledFolderExpanded,
                    DispatcherPriority.Background);
            }

            e.Handled = true;
            return;
        }

        if (hitItem?.DataContext is TreeNodeViewModel { IsConnection: true } node)
            vm.SelectedNode = node;

        if (vm.SelectedNode is { IsConnection: true } && vm.ConnectCommand.CanExecute(null))
        {
            vm.ConnectCommand.Execute(null);
            e.Handled = true;
        }
    }

    // - Right-clicking on a node selects it so the context menu acts on it.
    // - Clicking (left or right) on empty tree area clears the selection so
    //   new/paste operations target the root folder.
    private void OnTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        if (TryToggleFolderFromSource(e, out var toggledNode) && toggledNode is not null)
        {
            RememberFolderToggle(toggledNode);
            vm.SelectedNode = toggledNode;
            e.Handled = true;
            return;
        }

        var hitItem = e.Source is Visual source
            ? source.FindAncestorOfType<TreeViewItem>(includeSelf: true)
            : null;

        if (hitItem is { DataContext: TreeNodeViewModel node })
        {
            // Only right-click reselects; left-click on a node is handled by the
            // TreeView normally and would otherwise fight its own selection.
            if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
            {
                // Right-clicking a Recent shadow should open the context menu, not
                // fire the one-click launch the way a left-click selection would.
                // We arm the suppress flag and clear it on the next dispatcher tick
                // so any re-entrant SelectedNode change from the TreeView's own
                // selection logic during this input event is suppressed too.
                if (node is { IsRecent: true, IsConnection: true })
                {
                    vm.SuppressRecentAutoLaunch = true;
                    Dispatcher.UIThread.Post(
                        () => vm.SuppressRecentAutoLaunch = false,
                        DispatcherPriority.Background);
                }
                vm.SelectedNode = node;
            }
        }
        else
        {
            // Empty area → clear selection so the next New/Paste targets root.
            vm.SelectedNode = null;
        }
    }

    private static bool TryToggleFolderFromSource(
        PointerPressedEventArgs e,
        out TreeNodeViewModel? toggledNode)
    {
        toggledNode = null;

        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed)
            return false;

        var item = e.Source is Visual source
            ? source.FindAncestorOfType<TreeViewItem>(includeSelf: true)
            : null;

        if (item?.DataContext is not TreeNodeViewModel { IsFolder: true } node)
            return false;

        node.IsExpanded = !node.IsExpanded;
        toggledNode = node;
        return true;
    }

    private void RememberFolderToggle(TreeNodeViewModel node)
    {
        _lastToggledFolder = node;
        _lastToggledFolderExpanded = node.IsExpanded;
    }

    private async Task<string?> PickFolderAsync(string suggestedPath, string? title = null)
    {
        var options = new FolderPickerOpenOptions
        {
            Title = title ?? Localizer.Get("DialogPickFinalShellTitle"),
            AllowMultiple = false,
        };

        if (!string.IsNullOrEmpty(suggestedPath) && System.IO.Directory.Exists(suggestedPath))
        {
            try
            {
                options.SuggestedStartLocation =
                    await StorageProvider.TryGetFolderFromPathAsync(suggestedPath);
            }
            catch
            {
                // Best-effort — proceed without a suggestion.
            }
        }

        var folders = await StorageProvider.OpenFolderPickerAsync(options);
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    private async Task<string?> PickKeyFileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Localizer.Get("DialogPickKeyTitle"),
            AllowMultiple = false,
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    private Task<bool> ConfirmAsync(string title, string message)
    {
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
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
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

        dialog.ShowDialog(this);
        return tcs.Task;
    }

    private Task<string?> PromptAsync(string title, string message, string initial)
    {
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
        // Focus + select once the window is actually shown, so it reliably sticks.
        dialog.Opened += (_, _) => { input.Focus(); input.SelectAll(); };

        dialog.ShowDialog(this);
        return tcs.Task;
    }

    private Task<SettingsDialogResult?> PickSettingsAsync(
        StorageLocation current,
        string? currentCustomPath,
        string? currentLanguage,
        string? currentTheme,
        bool currentCheckOnStartup,
        int currentIntervalHours)
    {
        var tcs = new TaskCompletionSource<SettingsDialogResult?>();

        var userRadio = new RadioButton
        {
            GroupName = "storage",
            IsChecked = current == StorageLocation.UserDirectory,
            Content = BuildOption(
                Localizer.Get("StorageUserOption"),
                SettingsService.ResolveConfigRoot(StorageLocation.UserDirectory)),
        };

        var programRadio = new RadioButton
        {
            GroupName = "storage",
            IsChecked = current == StorageLocation.ProgramDirectory,
            Content = BuildOption(
                Localizer.Get("StorageProgramOption"),
                SettingsService.ResolveConfigRoot(StorageLocation.ProgramDirectory)),
        };

        // The custom location lets the user point storage at any base directory.
        // Browsing fills in customPath and selects this radio.
        var customPath = currentCustomPath;
        var customPathText = new TextBlock
        {
            FontSize = 11,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var browseButton = new Button { Content = Localizer.Get("Browse") };

        void RefreshCustomPathText()
        {
            customPathText.Foreground = Avalonia.Media.Brushes.Gray;
            customPathText.Text = string.IsNullOrWhiteSpace(customPath)
                ? Localizer.Get("StorageCustomNotSet")
                : SettingsService.ResolveConfigRoot(StorageLocation.CustomDirectory, customPath);
        }

        RefreshCustomPathText();

        var customRadio = new RadioButton
        {
            GroupName = "storage",
            IsChecked = current == StorageLocation.CustomDirectory,
            Content = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new TextBlock { Text = Localizer.Get("StorageCustomOption") },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        Children = { browseButton, customPathText },
                    },
                },
            },
        };

        browseButton.Click += async (_, _) =>
        {
            var picked = await PickFolderAsync(customPath ?? string.Empty, Localizer.Get("DialogPickStorageTitle"));
            if (picked is null)
                return;
            customPath = picked;
            customRadio.IsChecked = true;
            RefreshCustomPathText();
        };

        // null code = follow system. Native names match the in-app language list.
        var languages = new[]
        {
            new LanguageChoice(Localizer.Get("FollowSystem"), null),
            new LanguageChoice("English", "en"),
            new LanguageChoice("中文", "zh"),
        };
        var languageBox = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = languages,
        };
        languageBox.SelectedIndex =
            System.Array.FindIndex(languages, c => c.Code == currentLanguage) is var i && i >= 0 ? i : 0;

        // null code = follow system theme.
        var themes = new[]
        {
            new ThemeChoice(Localizer.Get("FollowSystem"), null),
            new ThemeChoice(Localizer.Get("ThemeLight"), "Light"),
            new ThemeChoice(Localizer.Get("ThemeDark"), "Dark"),
        };
        var themeBox = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = themes,
        };
        themeBox.SelectedIndex =
            System.Array.FindIndex(themes, c => c.Code == currentTheme) is var ti && ti >= 0 ? ti : 0;

        var changePassword = new Button
        {
            Content = Localizer.Get("ChangeMasterPassword"),
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        var checkOnStartupBox = new CheckBox
        {
            Content = Localizer.Get("CheckUpdateOnStartup"),
            IsChecked = currentCheckOnStartup,
        };

        // 0 = disabled. Hours used directly as the value.
        var intervals = new[]
        {
            new IntervalChoice(Localizer.Get("IntervalNever"), 0),
            new IntervalChoice(Localizer.Get("IntervalEvery6Hours"), 6),
            new IntervalChoice(Localizer.Get("IntervalDaily"), 24),
            new IntervalChoice(Localizer.Get("IntervalWeekly"), 24 * 7),
        };
        var intervalBox = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = intervals,
        };
        intervalBox.SelectedIndex =
            System.Array.FindIndex(intervals, c => c.Hours == currentIntervalHours) is var ii && ii >= 0
                ? ii
                : 2;

        var ok = new Button { Content = Localizer.Get("DialogOk"), MinWidth = 80, IsDefault = true };
        var cancel = new Button { Content = Localizer.Get("DialogCancel"), MinWidth = 80, IsCancel = true };

        var dialog = new Window
        {
            Title = Localizer.Get("DialogSettingsTitle"),
            Width = 460,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(16),
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = Localizer.Get("Language"), FontWeight = FontWeight.SemiBold },
                    languageBox,
                    new TextBlock { Text = Localizer.Get("Theme"), FontWeight = FontWeight.SemiBold },
                    themeBox,
                    new TextBlock { Text = Localizer.Get("DialogStorageQuestion"), FontWeight = FontWeight.SemiBold },
                    userRadio,
                    programRadio,
                    customRadio,
                    new TextBlock { Text = Localizer.Get("Password"), FontWeight = FontWeight.SemiBold },
                    changePassword,
                    new TextBlock { Text = Localizer.Get("AutoUpdate"), FontWeight = FontWeight.SemiBold },
                    checkOnStartupBox,
                    new TextBlock { Text = Localizer.Get("UpdateCheckInterval") },
                    intervalBox,
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

        changePassword.Click += async (_, _) =>
        {
            await MasterPasswordDialog.ShowAsync(
                dialog,
                Localizer.Get("MasterChangeTitle"),
                Localizer.Get("MasterChangePrompt"),
                newPassword =>
                {
                    (DataContext as MainWindowViewModel)?.ChangeMasterPassword(newPassword);
                    return true;
                });
        };

        ok.Click += (_, _) =>
        {
            var storage = programRadio.IsChecked == true
                ? StorageLocation.ProgramDirectory
                : customRadio.IsChecked == true
                    ? StorageLocation.CustomDirectory
                    : StorageLocation.UserDirectory;

            // The custom option is meaningless without a directory — keep the dialog
            // open and flag the missing path rather than committing a bad setting.
            if (storage == StorageLocation.CustomDirectory && string.IsNullOrWhiteSpace(customPath))
            {
                customPathText.Foreground = Avalonia.Media.Brushes.Red;
                customPathText.Text = Localizer.Get("StorageCustomRequired");
                return;
            }

            var language = (languageBox.SelectedItem as LanguageChoice)?.Code;
            var theme = (themeBox.SelectedItem as ThemeChoice)?.Code;
            var checkOnStartup = checkOnStartupBox.IsChecked == true;
            var intervalHours = (intervalBox.SelectedItem as IntervalChoice)?.Hours ?? 0;
            tcs.TrySetResult(new SettingsDialogResult(
                storage,
                storage == StorageLocation.CustomDirectory ? customPath : currentCustomPath,
                language,
                theme,
                checkOnStartup,
                intervalHours));
            dialog.Close();
        };
        cancel.Click += (_, _) => { tcs.TrySetResult(null); dialog.Close(); };
        dialog.Closed += (_, _) => tcs.TrySetResult(null);

        dialog.ShowDialog(this);
        return tcs.Task;
    }

    /// <summary>A selectable UI language; <see cref="Code"/> is null for "follow system".</summary>
    private sealed record LanguageChoice(string Label, string? Code)
    {
        public override string ToString() => Label;
    }

    /// <summary>A selectable UI theme; <see cref="Code"/> is null for "follow system".</summary>
    private sealed record ThemeChoice(string Label, string? Code)
    {
        public override string ToString() => Label;
    }

    /// <summary>An auto-update polling interval; <see cref="Hours"/> is 0 for "never".</summary>
    private sealed record IntervalChoice(string Label, int Hours)
    {
        public override string ToString() => Label;
    }

    private static Control BuildOption(string title, string path) => new StackPanel
    {
        Spacing = 2,
        Children =
        {
            new TextBlock { Text = title },
            new TextBlock
            {
                Text = path,
                FontSize = 11,
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap,
            },
        },
    };
}
