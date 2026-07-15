using System;
using System.Collections.Generic;
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
    private string? _pendingTreeFocusPath;
    private DispatcherTimer? _pendingTreeFocusClearTimer;
    private TabItem? _draggedTerminalTab;
    private Point _terminalTabDragStart;
    private bool _isTerminalTabDragging;
    private TreeNodeViewModel? _treeDragNode;
    private System.Collections.Generic.List<TreeNodeViewModel>? _treeDragNodes;
    private Point _treeDragStart;
    private bool _isTreeDragging;
    private TreeNodeViewModel? _treeDropTarget;

    // Set when a plain press lands on a node that is part of the current
    // multi-selection: the press is swallowed to keep the selection intact for
    // a potential multi-drag, and the selection collapses to this node on
    // release if no drag started (Explorer-style deferred deselection).
    private TreeNodeViewModel? _pendingCollapseToNode;
    private bool _treePanelWidthRestored;
    private double _treePanelWidth = 306;

    // Auto-locks "Show password" after a stretch of inactivity in the main
    // window, so a revealed password isn't left on screen when the user
    // walks away. Any pointer or key input resets the timer.
    private const double TerminalTabDragThreshold = 6;
    private const double TreeDragThreshold = 6;
    private static readonly Cursor TreeDragMoveCursor = new(StandardCursorType.DragMove);
    private static readonly Cursor TreeDragNoDropCursor = new(StandardCursorType.No);
    private static readonly TimeSpan ShowPasswordIdleTimeout = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan WindowSizeSaveDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan PendingTreeFocusRestoreWindow = TimeSpan.FromSeconds(20);
    private DispatcherTimer? _showPasswordIdleTimer;

    public MainWindow()
    {
        InitializeComponent();
        UpdateWindowTitle();
        _defaultMinWidth = MinWidth;
        _defaultMinHeight = MinHeight;
        Tree.SelectionChanged += OnTreeSelectionChanged;
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
        Tree.AddHandler(
            InputElement.KeyDownEvent,
            OnTreeKeyDown,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        Tree.AddHandler(
            InputElement.PointerMovedEvent,
            OnTreeDragPointerMoved,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        Tree.AddHandler(
            InputElement.PointerReleasedEvent,
            OnTreeDragPointerReleased,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        RightTabs.AddHandler(
            InputElement.PointerMovedEvent,
            OnTerminalTabDragPointerMoved,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        TreeSplitter.DragCompleted += (_, _) => PersistConnectionPanelWidth();
        RightTabs.AddHandler(
            InputElement.PointerReleasedEvent,
            OnTerminalTabDragPointerReleased,
            RoutingStrategies.Tunnel,
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
        DataContextChanged += (_, _) =>
        {
            WireUp();
            BuildMoreActionsMenu();
        };
        Localizer.LanguageChanged += (_, _) =>
        {
            BuildMoreActionsMenu();
            UpdateWindowTitle();
        };
        SizeChanged += OnWindowSizeChanged;
        CommandBar.LayoutUpdated += (_, _) => UpdateToolbarCompactMode();
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

    private void UpdateWindowTitle() =>
        Title = DebugInstanceContext.DecorateTitle(Localizer.Get("WindowTitle"));

    /// <summary>Localized common-menu labels exposed for Debug MCP verification.</summary>
    public IReadOnlyList<string> MoreActionsMenuHeaders =>
        (MoreActionsButton.Flyout as MenuFlyout)?.Items
        .OfType<MenuItem>()
        .Select(item => item.Header?.ToString() ?? string.Empty)
        .ToArray() ?? [];

    /// <summary>Rendered terminal panel toolbar order exposed for Debug MCP verification.</summary>
    public IReadOnlyList<string> TerminalPanelToolbarOrder =>
        ToolbarTerminal.Children
            .OfType<Button>()
            .Where(button => ReferenceEquals(button, AiPanelToolbarButton)
                          || ReferenceEquals(button, MonitorToolbarButton)
                          || ReferenceEquals(button, FileBrowserToolbarButton))
            .Select(button => ReferenceEquals(button, AiPanelToolbarButton) ? "AI"
                : ReferenceEquals(button, MonitorToolbarButton) ? "Monitor"
                : "FileBrowser")
            .ToArray();

    /// <summary>Active terminal tab menu labels exposed for Debug MCP verification.</summary>
    public IReadOnlyList<string> ActiveTerminalTabMenuHeaders =>
        (RightTabs.SelectedItem as TabItem)?.ContextMenu?.Items
            .OfType<MenuItem>()
            .Select(item => item.Header?.ToString() ?? string.Empty)
            .ToArray() ?? [];

    /// <summary>Exercises AI turn-completion text reconciliation without requiring a live
    /// provider session. Exposed for Debug MCP verification.</summary>
    public string DebugReconcileCompletedAgentText(string streamedText, string completedText) =>
        AgentChatViewModel.SelectCompletedText(streamedText, completedText);

    /// <summary>Verifies that a terminal Codex error notification does not end the AI turn
    /// before the matching turn/completed notification. Exposed for Debug MCP.</summary>
    public string DebugCodexTurnErrorLifecycle(string terminalError) =>
        CodexChatSession.DebugTurnErrorLifecycle(terminalError);

    private void BuildMoreActionsMenu()
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        var menu = new MenuFlyout();
        foreach (var entry in ApplicationMenuDefinition.CommonItems)
        {
            if (menu.Items.Count > 0)
                menu.Items.Add(new Separator());

            var icon = new TextBlock { Text = entry.IconGlyph };
            icon.Classes.Add("menu-icon");
            if (entry.IsAccent)
                icon.Classes.Add("accent");

            var item = new MenuItem
            {
                Header = Localizer.Get(entry.LocalizationKey),
                Icon = icon,
            };
            if (entry.ToolTipLocalizationKey is { } toolTipKey)
                ToolTip.SetTip(item, Localizer.Get(toolTipKey));

            switch (entry.Action)
            {
                case ApplicationMenuAction.Settings:
                    item.Command = vm.OpenSettingsCommand;
                    break;
                case ApplicationMenuAction.ImportFromFinalShell:
                    item.Command = vm.ImportFinalShellCommand;
                    break;
                case ApplicationMenuAction.CheckForUpdates:
                    item.Command = vm.CheckForUpdatesCommand;
                    break;
                case ApplicationMenuAction.Exit:
                    item.Click += (_, _) => (Application.Current as App)?.RequestExit();
                    break;
            }

            menu.Items.Add(item);
        }

        MoreActionsButton.Flyout = menu;
    }

    public void FlushCurrentSettingsState()
    {
        var vm = DataContext as MainWindowViewModel;
        // Sync pending UI state into AppSettings before deciding whether the file changed.
        vm?.SaveLastSelectedConnection();
        SaveCurrentWindowSize(vm);
        PersistConnectionPanelWidth();
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
        vm.OpenNewSshTerminalAsync = async (connection, sourcePath) =>
        {
            _ = await EnsureSshTerminalAsync(connection, sourcePath, forceNew: true);
        };
        vm.EnsureSshTerminalAsync = EnsureSshTerminalAsync;
        vm.EnsureSshTerminalQuietlyAsync = (connection, sourcePath) =>
            EnsureSshTerminalAsync(connection, sourcePath, forceNew: false, select: false);
        vm.ApplyTerminalFontSize = ApplyTerminalFontToOpenTabs;
        vm.ConfirmHostKeyTrust = HostKeyDialog.PromptTrust;
        vm.RequestFocusTree = FocusSelectedTreeItem;
        vm.RequestFocusTreeNode = FocusTreeItem;
        vm.RequestFocusTreeNameEditor = FocusTreeNameEditor;
        vm.PropertyChanged -= OnViewModelPropertyChanged;
        vm.PropertyChanged += OnViewModelPropertyChanged;
        RestoreWindowSize(vm);
        RestoreConnectionPanelWidth(vm);
    }

    private void RestoreConnectionPanelWidth(MainWindowViewModel vm)
    {
        if (_treePanelWidthRestored)
            return;

        _treePanelWidthRestored = true;
        _treePanelWidth = vm.ConnectionPanelWidth;
        ApplyConnectionPanelState(collapsed: false);
    }

    // ColumnDefinitions don't generate fields, so reach the tree column through the grid.
    private ColumnDefinition TreeColumn => MainGrid.ColumnDefinitions[0];

    private void OnToggleConnectionPanelClick(object? sender, RoutedEventArgs e)
    {
        var collapsed = TreePanel.IsVisible;
        ApplyConnectionPanelState(collapsed);
    }

    /// <summary>Collapses or expands the connection tree panel. Collapsing remembers
    /// the splitter-set width so expanding restores it within the session.</summary>
    private void ApplyConnectionPanelState(bool collapsed)
    {
        if (collapsed && TreeColumn.Width.IsAbsolute && TreeColumn.Width.Value > 0)
        {
            _treePanelWidth = TreeColumn.Width.Value;
            PersistConnectionPanelWidth();
        }

        TreePanel.IsVisible = !collapsed;
        TreeSplitter.IsVisible = !collapsed;
        TreeColumn.Width = new GridLength(collapsed ? 0 : _treePanelWidth, GridUnitType.Pixel);
        // Zero the spacing too, or the two inter-column gaps would leave a
        // 16px dead strip at the left edge while the panel is hidden.
        MainGrid.ColumnSpacing = collapsed ? 0 : 8;
        ToggleTreePanelIcon.Text = collapsed ? "\uE8A0" : "\uE89F"; // OpenPane / ClosePane
        ToggleTreePanelButton.Classes.Set("panel-on", !collapsed);
    }

    private void PersistConnectionPanelWidth()
    {
        if (TreePanel.IsVisible && TreeColumn.Width.IsAbsolute && TreeColumn.Width.Value > 0)
            _treePanelWidth = TreeColumn.Width.Value;

        if (DataContext is MainWindowViewModel vm)
            vm.ConnectionPanelWidth = _treePanelWidth;
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

    // Natural (label-included) width the command bar wanted when it last switched
    // to compact; the bar expands back once the window offers at least that much.
    private double _toolbarFullWidth;

    /// <summary>
    /// Toggles the command bar's icon-only mode. The bar's sections are plain
    /// horizontal StackPanels, which do not shrink — on a too-narrow window they
    /// would just paint over each other. So after every layout pass, compare the
    /// buttons' natural width against the available width and hide the text labels
    /// (the "compact" style class) when they no longer fit.
    /// </summary>
    private void UpdateToolbarCompactMode()
    {
        var available = CommandBar.Bounds.Width;
        if (available <= 0)
            return;

        if (!CommandBar.Classes.Contains("compact"))
        {
            var needed = NaturalPanelWidth(ToolbarBrand)
                         + NaturalPanelWidth(ToolbarNew)
                         + NaturalPanelWidth(ToolbarTerminal)
                         + CommandBar.ColumnSpacing * 2;
            if (needed > available)
            {
                _toolbarFullWidth = needed;
                CommandBar.Classes.Add("compact");
            }
        }
        else if (available >= _toolbarFullWidth)
        {
            // Optimistic when the cached width is stale (e.g. buttons appeared or
            // disappeared meanwhile): labels come back, and if they still do not
            // fit the next pass re-compacts with a fresh measurement.
            CommandBar.Classes.Remove("compact");
        }
    }

    /// <summary>Sum of the visible children's desired widths plus spacing — the width
    /// the panel paints at, unlike its DesiredSize which the Grid clamps.</summary>
    private static double NaturalPanelWidth(StackPanel panel)
    {
        double width = 0;
        var visibleCount = 0;
        foreach (var child in panel.Children)
        {
            if (!child.IsVisible)
                continue;
            width += child.DesiredSize.Width;
            visibleCount++;
        }

        if (visibleCount > 1)
            width += panel.Spacing * (visibleCount - 1);
        return width;
    }

    private Task<TerminalScriptSession?> EnsureSshTerminalAsync(Connection connection, string? sourcePath) =>
        EnsureSshTerminalAsync(connection, sourcePath, forceNew: false);

    private Task<TerminalScriptSession?> EnsureSshTerminalAsync(
        Connection connection,
        string? sourcePath,
        bool forceNew,
        bool select = true)
    {
        if (!forceNew)
        {
            var existing = FindTerminalTab(connection, sourcePath);
            while (existing is not null)
            {
                if (existing.Value.View.CanReuseSession)
                {
                    if (select)
                    {
                        RightTabs.SelectedItem = existing.Value.Tab;
                        existing.Value.View.FocusTerminal();
                    }
                    return Task.FromResult<TerminalScriptSession?>(CreateTerminalScriptSession(existing.Value.View, existing.Value.Tab));
                }

                CloseTerminalTab(existing.Value.Tab);
                existing = FindTerminalTab(connection, sourcePath);
            }
        }

        var (view, tab) = CreateTerminalTab(connection, sourcePath, select);
        view.Start(connection, sourcePath);
        return Task.FromResult<TerminalScriptSession?>(CreateTerminalScriptSession(view, tab));
    }

    private (TerminalView View, TabItem Tab) CreateTerminalTab(Connection connection, string? sourcePath, bool select = true)
    {
        var sessionNumber = NextTerminalSessionNumber(connection, sourcePath);
        var view = new TerminalView();
        view.PanelStateChanged += (_, _) => UpdateTerminalPanelToggleStates();
        var tab = new TabItem
        {
            Header = BuildTerminalTabHeader(connection, sessionNumber, out var closeButton),
            Content = view,
            Tag = sessionNumber,
        };
        tab.Classes.Add("terminal-tab");
        closeButton.Click += (_, _) => CloseTerminalTab(tab);
        tab.ContextMenu = BuildTerminalTabContextMenu(connection, tab);

        // Terminal tabs can be reordered by dragging the header. Middle-click or
        // double-click still closes the tab, matching the close button and menu.
        tab.AddHandler(
            PointerPressedEvent,
            OnTerminalTabPointerPressed,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        tab.DoubleTapped += (_, _) => CloseTerminalTab(tab);

        RightTabs.Items.Add(tab);
        if (select)
            RightTabs.SelectedItem = tab;

        view.SetFontSize((DataContext as MainWindowViewModel)?.TerminalFontSize ?? 14);
        return (view, tab);
    }

    /// <summary>
    /// Opens a new terminal tab on the same connection. When the source tab's SSH
    /// transport is live, the new tab piggybacks on it (a new channel on the
    /// authenticated connection — instant, no re-login); otherwise it connects fresh.
    /// </summary>
    private void DuplicateTerminalTab(TabItem sourceTab)
    {
        if (sourceTab.Content is not TerminalView source || source.Connection is not { } connection)
            return;

        var shared = source.ShareClientForDuplicate();
        var (view, _) = CreateTerminalTab(connection, source.SourcePath);
        view.Start(connection, source.SourcePath, shared, isDuplicatedSession: true);
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
            view.HideScriptPanel,
            () => view.IsScriptRunning);

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

    /// <summary>
    /// Smallest free session number (1-based) among open terminal tabs on the same
    /// connection. The first tab gets 1 (no suffix shown); duplicates get (2), (3)…
    /// Closing (2) and duplicating again reuses (2) instead of growing forever.
    /// </summary>
    private int NextTerminalSessionNumber(Connection connection, string? sourcePath)
    {
        var used = new HashSet<int>();
        foreach (var item in RightTabs.Items)
        {
            if (item is not TabItem { Content: TerminalView view } tab)
                continue;

            var samePath = !string.IsNullOrEmpty(sourcePath)
                && !string.IsNullOrEmpty(view.SourcePath)
                && PathEquals(view.SourcePath, sourcePath);
            if (!samePath && !ReferenceEquals(view.Connection, connection))
                continue;

            used.Add(tab.Tag is int number ? number : 1);
        }

        var next = 1;
        while (used.Contains(next))
            next++;
        return next;
    }

    private static bool PathEquals(string a, string b) =>
        string.Equals(NormalizePath(a), NormalizePath(b), StringComparison.OrdinalIgnoreCase);

    // Right-click menu on an SSH terminal tab. Mirrors the terminal toolbar
    // (duplicate session, run script, AI assistant, copy public key) plus Close.
    private ContextMenu BuildTerminalTabContextMenu(Connection connection, TabItem tab)
    {
        var duplicate = new MenuItem
        {
            Header = Localizer.Get("DuplicateSession"),
            Icon = CreateMenuIcon("\uE8C8", "duplicate"),
        };
        duplicate.Click += (_, _) => DuplicateTerminalTab(tab);

        var fileBrowser = new MenuItem
        {
            Header = Localizer.Get("FileBrowser"),
            Icon = CreateMenuIcon("\uE8B7", "file-browser"),
        };
        fileBrowser.Click += (_, _) =>
        {
            RightTabs.SelectedItem = tab;
            if (tab.Content is TerminalView view)
                view.ToggleFileBrowserPanel();
        };

        var aiPanel = new MenuItem
        {
            Header = Localizer.Get("AiAssistant"),
            Icon = CreateMenuIcon("\uE99A", "ai"),
        };
        aiPanel.Click += (_, _) =>
        {
            RightTabs.SelectedItem = tab;
            if (tab.Content is TerminalView view)
                view.ToggleAiPanel();
        };

        var monitor = new MenuItem
        {
            Header = Localizer.Get("ServerMonitor"),
            Icon = CreateMenuIcon("\uE9D2", "monitor"),
        };
        monitor.Click += (_, _) =>
        {
            RightTabs.SelectedItem = tab;
            if (tab.Content is TerminalView view)
                view.ToggleMonitorPanel();
        };

        var copyKey = new MenuItem
        {
            Header = Localizer.Get("CopyPublicKeyToServer"),
            Icon = CreateMenuIcon("\uE8D7", "key"),
        };
        ToolTip.SetTip(copyKey, Localizer.Get("CopyPublicKeyToServerTooltip"));
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

        var runScript = new MenuItem
        {
            Header = Localizer.Get("RunScript"),
            Icon = CreateMenuIcon("\uE756", "script"),
        };
        runScript.Click += (_, _) =>
        {
            // Let the context menu close first, then open the script-suite chooser.
            Dispatcher.UIThread.Post(
                () => ShowTerminalScriptChooser(tab),
                DispatcherPriority.Background);
        };

        var close = new MenuItem
        {
            Header = Localizer.Get("Close"),
            Icon = CreateMenuIcon("\uE711", "danger"),
        };
        close.Click += (_, _) => CloseTerminalTab(tab);

        var menu = new ContextMenu();
        menu.Items.Add(duplicate);
        menu.Items.Add(runScript);
        menu.Items.Add(aiPanel);
        // The monitor samples over SSH exec channels; a local WSL shell has none.
        if (connection.IsSsh)
            menu.Items.Add(monitor);
        menu.Items.Add(fileBrowser);
        // Public keys are an SSH concept; a local WSL shell has no server to copy to.
        if (!connection.IsWsl)
            menu.Items.Add(copyKey);
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
            var item = new MenuItem
            {
                Header = choice.ToString(),
                Icon = CreateMenuIcon("\uE756", "script"),
            };
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
    private static Control BuildTerminalTabHeader(Connection connection, int sessionNumber, out Button closeButton)
    {
        var title = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(connection.Name) ? connection.Host : connection.Name,
            MaxWidth = 180,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        title.Classes.Add("tab-label");

        // Session number sits outside the trimmed title so it stays visible
        // even when a long connection name gets ellipsized.
        TextBlock? numberLabel = null;
        if (sessionNumber > 1)
        {
            numberLabel = new TextBlock
            {
                Text = $"({sessionNumber})",
                Opacity = 0.65,
                VerticalAlignment = VerticalAlignment.Center,
            };
            numberLabel.Classes.Add("tab-label");
        }

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
            ColumnDefinitions = new ColumnDefinitions(numberLabel is null ? "Auto,*,Auto" : "Auto,*,Auto,Auto"),
            ColumnSpacing = 7,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var icon = CreateConnectionTypeIcon(connection.Type);
        Grid.SetColumn(icon, 0);
        Grid.SetColumn(title, 1);
        content.Children.Add(icon);
        content.Children.Add(title);
        if (numberLabel is not null)
        {
            Grid.SetColumn(numberLabel, 2);
            content.Children.Add(numberLabel);
        }
        Grid.SetColumn(closeButton, numberLabel is null ? 2 : 3);
        content.Children.Add(closeButton);

        var pill = new Border { Child = content };
        pill.Classes.Add("tab-pill");
        return pill;
    }

    private static Control CreateConnectionTypeIcon(ConnectionType type)
    {
        var text = new TextBlock
        {
            Text = type.ToGlyph(),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        text.Classes.Add("connection-icon");

        var badge = new Border { Child = text };
        badge.Classes.Add("connection-icon-badge");

        if (type == ConnectionType.Ssh)
        {
            text.Classes.Add("ssh-connection-icon");
            badge.Classes.Add("ssh-connection-icon");
        }
        else
        {
            text.Classes.Add("emoji");
        }

        return badge;
    }

    private static TextBlock CreateMenuIcon(string text, string? modifierClass = null, bool emoji = false)
    {
        var icon = new TextBlock { Text = text };
        icon.Classes.Add("menu-icon");
        icon.Classes.Add(emoji ? "emoji" : "icon");
        if (!string.IsNullOrWhiteSpace(modifierClass))
            icon.Classes.Add(modifierClass);
        return icon;
    }

    // Middle-clicking anywhere on a terminal tab closes it.
    private void OnTerminalTabPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not TabItem tab)
            return;

        var point = e.GetCurrentPoint(tab);
        if (point.Properties.IsMiddleButtonPressed)
        {
            CloseTerminalTab(tab);
            e.Handled = true;
            return;
        }

        if (!point.Properties.IsLeftButtonPressed || IsTerminalTabDragBlockedBySource(e.Source))
            return;

        _draggedTerminalTab = tab;
        _terminalTabDragStart = e.GetPosition(RightTabs);
        _isTerminalTabDragging = false;
        e.Pointer.Capture(tab);
    }

    private void OnTerminalTabDragPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_draggedTerminalTab is not { } tab)
            return;

        if (!e.GetCurrentPoint(RightTabs).Properties.IsLeftButtonPressed)
        {
            ResetTerminalTabDrag(e);
            return;
        }

        var currentPosition = e.GetPosition(RightTabs);
        if (!_isTerminalTabDragging)
        {
            var delta = currentPosition - _terminalTabDragStart;
            if (Math.Abs(delta.X) < TerminalTabDragThreshold && Math.Abs(delta.Y) < TerminalTabDragThreshold)
                return;

            _isTerminalTabDragging = true;
            tab.Classes.Add("dragging");
        }

        MoveDraggedTerminalTab(currentPosition);
        e.Handled = true;
    }

    private void OnTerminalTabDragPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_draggedTerminalTab is null)
            return;

        var wasDragging = _isTerminalTabDragging;
        ResetTerminalTabDrag(e);
        if (wasDragging)
            e.Handled = true;
    }

    private void MoveDraggedTerminalTab(Point pointerPosition)
    {
        if (_draggedTerminalTab is not { Content: TerminalView } dragged)
            return;

        var currentIndex = RightTabs.Items.IndexOf(dragged);
        if (currentIndex < 0)
            return;

        var targetIndex = GetTerminalTabInsertionIndex(pointerPosition, dragged);
        var insertIndex = targetIndex > currentIndex ? targetIndex - 1 : targetIndex;
        insertIndex = Math.Clamp(insertIndex, FirstTerminalTabIndex(), RightTabs.Items.Count - 1);
        if (insertIndex == currentIndex)
            return;

        var selectedItem = RightTabs.SelectedItem;
        RightTabs.Items.RemoveAt(currentIndex);
        RightTabs.Items.Insert(insertIndex, dragged);
        RightTabs.SelectedItem = selectedItem;
    }

    private int GetTerminalTabInsertionIndex(Point pointerPosition, TabItem dragged)
    {
        var firstTerminalTabIndex = FirstTerminalTabIndex();
        var fallbackIndex = RightTabs.Items.Count;

        for (var i = firstTerminalTabIndex; i < RightTabs.Items.Count; i++)
        {
            if (RightTabs.Items[i] is not TabItem { Content: TerminalView } tab || ReferenceEquals(tab, dragged))
                continue;

            if (!TryGetTabBoundsInRightTabs(tab, out var bounds))
                continue;

            if (pointerPosition.X < bounds.X + bounds.Width / 2)
                return i;

            fallbackIndex = i + 1;
        }

        return Math.Max(firstTerminalTabIndex, fallbackIndex);
    }

    private int FirstTerminalTabIndex()
    {
        var editorIndex = RightTabs.Items.IndexOf(EditorTab);
        return editorIndex >= 0 ? editorIndex + 1 : 0;
    }

    private bool TryGetTabBoundsInRightTabs(TabItem tab, out Rect bounds)
    {
        bounds = default;
        var topLeft = tab.TranslatePoint(new Point(0, 0), RightTabs);
        if (topLeft is null || tab.Bounds.Width <= 0 || tab.Bounds.Height <= 0)
            return false;

        bounds = new Rect(topLeft.Value, tab.Bounds.Size);
        return true;
    }

    private void ResetTerminalTabDrag(PointerEventArgs e)
    {
        if (_draggedTerminalTab is { } tab)
            tab.Classes.Remove("dragging");

        _draggedTerminalTab = null;
        _isTerminalTabDragging = false;
        e.Pointer.Capture(null);
    }

    private static bool IsTerminalTabDragBlockedBySource(object? source) =>
        source is Avalonia.Visual visual
        && visual.FindAncestorOfType<Button>(includeSelf: true) is not null;

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
        // SelectionChanged bubbles: a selection change in any list INSIDE a tab
        // (e.g. the file browser's list) reaches this handler too, and refocusing
        // the terminal for those would yank keyboard focus out of that list.
        if (!ReferenceEquals(e.Source, sender))
            return;

        // Use sender, not the RightTabs field: this can fire during XAML init
        // (the TabControl auto-selects the first tab) before the field is assigned.
        var view = (sender as TabControl)?.SelectedItem is TabItem { Content: TerminalView v } ? v : null;

        // The font-size buttons are only relevant while a terminal tab is active.
        if (DataContext is MainWindowViewModel vm)
            vm.IsTerminalActive = view is not null;

        UpdateTerminalPanelToggleStates();
        view?.FocusTerminal();
    }

    /// <summary>Syncs the monitor/AI/file-browser toolbar buttons' "on" highlight
    /// with the active terminal tab's panel visibility.</summary>
    private void UpdateTerminalPanelToggleStates()
    {
        // Can run during XAML init (the TabControl auto-selects its first tab)
        // before the named controls are assigned.
        if (RightTabs is null || MonitorToolbarButton is null)
            return;

        var view = RightTabs.SelectedItem is TabItem { Content: TerminalView v } ? v : null;
        MonitorToolbarButton.Classes.Set("panel-on", view?.IsMonitorPanelOpen == true);
        AiPanelToolbarButton.Classes.Set("panel-on", view?.IsAiPanelOpen == true);
        FileBrowserToolbarButton.Classes.Set("panel-on", view?.IsFileBrowserPanelOpen == true);
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

    private void OnDuplicateSessionToolbarClick(object? sender, RoutedEventArgs e)
    {
        if (RightTabs.SelectedItem is TabItem { Content: TerminalView } tab)
            DuplicateTerminalTab(tab);
        e.Handled = true;
    }

    private void OnAiPanelToolbarClick(object? sender, RoutedEventArgs e)
    {
        if (RightTabs.SelectedItem is TabItem { Content: TerminalView view })
            view.ToggleAiPanel();
        e.Handled = true;
    }

    private void OnFileBrowserToolbarClick(object? sender, RoutedEventArgs e)
    {
        if (RightTabs.SelectedItem is TabItem { Content: TerminalView view })
            view.ToggleFileBrowserPanel();
        e.Handled = true;
    }

    private async void OnForceInterruptTerminalToolbarClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        await RequestForceInterruptTerminalAsync();
    }

    private async void OnReconnectTerminalToolbarClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        await RequestReconnectTerminalAsync();
    }

    /// <summary>Confirms and force-interrupts the active terminal. Public for Debug MCP.</summary>
    public async Task<bool> RequestForceInterruptTerminalAsync()
    {
        if (RightTabs.SelectedItem is not TabItem { Content: TerminalView view })
            return false;

        if (!await ConfirmAsync(
                Localizer.Get("TerminalForceInterruptConfirmTitle"),
                Localizer.Get("TerminalForceInterruptConfirmPrompt")))
        {
            view.FocusTerminal();
            return false;
        }

        view.ForceInterruptTerminalCommand();
        return true;
    }

    /// <summary>Confirms and reconnects the active terminal. Public for Debug MCP.</summary>
    public async Task<bool> RequestReconnectTerminalAsync()
    {
        if (RightTabs.SelectedItem is not TabItem { Content: TerminalView view })
            return false;

        if (!await ConfirmAsync(
                Localizer.Get("TerminalReconnectConfirmTitle"),
                Localizer.Get("TerminalReconnectConfirmPrompt")))
        {
            view.FocusTerminal();
            return false;
        }

        view.ReconnectTerminal();
        return true;
    }

    private void OnMonitorToolbarClick(object? sender, RoutedEventArgs e)
    {
        if (RightTabs.SelectedItem is TabItem { Content: TerminalView view })
            view.ToggleMonitorPanel();
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

        // A multi-selection runs the chosen suite on every selected connection
        // (each in its own terminal tab) with the batch panel aggregating status.
        if (vm.HasMultiSelection)
        {
            var batchChoices = vm.PrepareBatchScriptSuiteChoices();
            if (batchChoices.Count == 0)
                return;

            if (batchChoices.Count == 1)
            {
                OpenBatchScriptPanel(vm, batchChoices[0]);
                e.Handled = true;
                return;
            }

            var batchFlyout = new MenuFlyout();
            foreach (var choice in batchChoices)
            {
                var item = new MenuItem
                {
                    Header = choice.ToString(),
                    Icon = CreateMenuIcon("\uE756", "script"),
                };
                item.Click += (_, _) => OpenBatchScriptPanel(vm, choice);
                batchFlyout.Items.Add(item);
            }

            ShowScriptSuiteFlyout(batchFlyout, anchor);
            e.Handled = true;
            return;
        }

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
                Icon = CreateMenuIcon("\uE756", "script"),
            };
            item.Click += async (_, _) =>
            {
                await OpenScriptPanelInNewTerminalAsync(node, choice);
            };
            flyout.Items.Add(item);
        }

        ShowScriptSuiteFlyout(flyout, anchor);
        e.Handled = true;
    }

    /// <summary>
    /// Shows a script-suite chooser flyout. A click on a context-menu item closes
    /// the menu — and a flyout anchored to the disappearing item never shows — so
    /// in that case the flyout is deferred a beat and re-anchored to the selected
    /// tree row (falling back to the tree itself).
    /// </summary>
    private void ShowScriptSuiteFlyout(MenuFlyout flyout, Control anchor)
    {
        if (anchor.FindAncestorOfType<ContextMenu>(includeSelf: true) is null)
        {
            flyout.ShowAt(anchor);
            return;
        }

        var target = Tree.SelectedItem is { } selected
            ? (Control?)FindTreeViewItem(Tree, selected) ?? Tree
            : Tree;
        Dispatcher.UIThread.Post(() => flyout.ShowAt(target));
    }

    private void OpenBatchScriptPanel(MainWindowViewModel vm, ScriptSuiteChoiceViewModel choice)
    {
        vm.OpenBatchScriptSuiteChoice(choice);
        if (vm.BatchPanel is not null)
            RightTabs.SelectedItem = EditorTab;
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
        else if (e.PropertyName == nameof(MainWindowViewModel.SelectedNode)
                 && sender is MainWindowViewModel vm)
            RestorePendingTreeFocus(vm.SelectedNode);
        else if (e.PropertyName == nameof(MainWindowViewModel.IsTerminalActive))
            // The terminal buttons just appeared or disappeared, so the cached
            // full width is stale — drop compact and let the next layout pass
            // re-measure from scratch.
            CommandBar.Classes.Remove("compact");
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

    private void OnPointerActivity(object? sender, PointerEventArgs e)
    {
        if (e is PointerPressedEventArgs && !IsTreeSource(e.Source))
            ClearPendingTreeFocusRestore();

        NoteUserActivity();
    }

    private void OnKeyActivity(object? sender, KeyEventArgs e)
    {
        if (TryHandlePendingTreeNavigation(e))
        {
            NoteUserActivity();
            return;
        }

        if (!IsTreeSource(e.Source))
            ClearPendingTreeFocusRestore();

        NoteUserActivity();
    }

    private bool TryHandlePendingTreeNavigation(KeyEventArgs e)
    {
        if (_pendingTreeFocusPath is null
            || e.Handled
            || !IsTreeNavigationKey(e.Key)
            || IsTreeNameEditorSource(e.Source)
            || DataContext is not MainWindowViewModel vm)
        {
            return false;
        }

        var visibleNodes = FlattenVisibleTreeNodes(vm.Nodes).ToList();
        if (visibleNodes.Count == 0)
            return false;

        var currentIndex = FindVisibleTreeNodeIndex(visibleNodes, vm.SelectedNode);
        if (currentIndex < 0)
            currentIndex = visibleNodes.FindIndex(n =>
                !n.IsRecent && PathEquals(_pendingTreeFocusPath, n.FullPath));
        if (currentIndex < 0)
            currentIndex = 0;

        var nextIndex = e.Key switch
        {
            Key.Up => Math.Max(0, currentIndex - 1),
            Key.Down => Math.Min(visibleNodes.Count - 1, currentIndex + 1),
            Key.Home => 0,
            Key.End => visibleNodes.Count - 1,
            Key.PageUp => Math.Max(0, currentIndex - 10),
            Key.PageDown => Math.Min(visibleNodes.Count - 1, currentIndex + 10),
            _ => currentIndex,
        };

        vm.SelectedNode = visibleNodes[nextIndex];
        TrackPendingTreeFocusRestore(vm.SelectedNode);
        e.Handled = true;
        return true;
    }

    private static bool IsTreeNavigationKey(Key key) =>
        key is Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown;

    private static int FindVisibleTreeNodeIndex(
        System.Collections.Generic.IReadOnlyList<TreeNodeViewModel> visibleNodes,
        TreeNodeViewModel? node)
    {
        if (node is null)
            return -1;

        for (var i = 0; i < visibleNodes.Count; i++)
            if (ReferenceEquals(visibleNodes[i], node))
                return i;

        for (var i = 0; i < visibleNodes.Count; i++)
            if (visibleNodes[i].IsRecent == node.IsRecent
                && PathEquals(visibleNodes[i].FullPath, node.FullPath))
                return i;

        return -1;
    }

    private static System.Collections.Generic.IEnumerable<TreeNodeViewModel> FlattenVisibleTreeNodes(
        System.Collections.Generic.IEnumerable<TreeNodeViewModel> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            if (!node.IsExpanded)
                continue;

            foreach (var child in FlattenVisibleTreeNodes(node.Children))
                yield return child;
        }
    }

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
    /// keystrokes (Enter, F2, Delete, Ctrl+C/X/V) hit the new item.
    /// </summary>
    private void FocusSelectedTreeItem()
    {
        var item = Tree.SelectedItem as TreeNodeViewModel;
        FocusTreeItem(item);
    }

    private void FocusTreeItem(TreeNodeViewModel? node)
    {
        FocusTreeItem(node, attemptsRemaining: 4);
    }

    private void FocusTreeItem(TreeNodeViewModel? node, int attemptsRemaining)
    {
        // Defer until after the TreeView has materialised the container.
        Dispatcher.UIThread.Post(() =>
        {
            var item = node ?? Tree.SelectedItem as TreeNodeViewModel;
            if (item != null)
            {
                if (_pendingTreeFocusPath is not null
                    && !PathEquals(_pendingTreeFocusPath, item.FullPath))
                {
                    return;
                }

                var container = FindTreeViewItem(Tree, item);
                if (container != null)
                {
                    container.Focus();
                    return;
                }
            }

            if (attemptsRemaining > 0)
            {
                FocusTreeItem(node, attemptsRemaining - 1);
                return;
            }

            Tree.Focus();
        }, DispatcherPriority.Background);
    }

    private void TrackPendingTreeFocusRestore(TreeNodeViewModel? node)
    {
        if (node is null)
            return;

        _pendingTreeFocusPath = NormalizePath(node.FullPath);
        FocusTreeItem(node);
        RestartPendingTreeFocusTimer();
    }

    private void RestorePendingTreeFocus(TreeNodeViewModel? node)
    {
        if (_pendingTreeFocusPath is null)
            return;

        if (node is null)
            return;

        if (!PathEquals(_pendingTreeFocusPath, node.FullPath))
        {
            _pendingTreeFocusPath = NormalizePath(node.FullPath);
            RestartPendingTreeFocusTimer();
        }

        FocusTreeItem(node);
    }

    private void RestartPendingTreeFocusTimer()
    {
        if (_pendingTreeFocusClearTimer is null)
        {
            _pendingTreeFocusClearTimer = new DispatcherTimer { Interval = PendingTreeFocusRestoreWindow };
            _pendingTreeFocusClearTimer.Tick += (_, _) => ClearPendingTreeFocusRestore();
        }

        _pendingTreeFocusClearTimer.Stop();
        _pendingTreeFocusClearTimer.Start();
    }

    private void ClearPendingTreeFocusRestore()
    {
        _pendingTreeFocusPath = null;
        _pendingTreeFocusClearTimer?.Stop();
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

    private void FocusTreeNameEditor(TreeNodeViewModel node)
    {
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var visual in Tree.GetVisualDescendants())
            {
                if (visual is TextBox editor
                    && editor.Classes.Contains("tree-name-editor")
                    && ReferenceEquals(editor.DataContext, node))
                {
                    editor.Focus();
                    editor.SelectAll();
                    return;
                }
            }

            FocusSelectedTreeItem();
        }, DispatcherPriority.Background);
    }

    private void OnTreeNameEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: TreeNodeViewModel node }
            || DataContext is not MainWindowViewModel vm)
            return;

        // Bubble handler on the editor, running after the TextBox consumed the
        // keys it uses for caret movement. Navigation keys still unhandled here
        // (Up/Down/PageUp/PageDown on a single-line editor) must not reach the
        // TreeView: tree navigation would move the selection and kick the
        // editor out of edit mode via LostFocus.
        if (HandleTreeNameEditorKey(vm, node, e.Key) || IsEditorCaretNavigationKey(e.Key))
        {
            e.Handled = true;
        }
    }

    private void OnTreeNameEditorLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: TreeNodeViewModel node }
            && DataContext is MainWindowViewModel vm)
        {
            vm.CommitNodeNameEdit(node, requestFocus: false);
        }
    }

    private void OnTreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || DataContext is not MainWindowViewModel vm)
            return;

        if (TryGetTreeNameEditorNode(e.Source, out var editingNode) && editingNode is not null)
        {
            // Tunnel phase: commit/cancel immediately, but let every other key
            // continue to the TextBox (caret movement); OnTreeNameEditorKeyDown
            // swallows whatever the TextBox leaves unhandled.
            if (HandleTreeNameEditorKey(vm, editingNode, e.Key))
                e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            // Ctrl+Enter forces a fresh session; plain Enter reuses an open tab.
            var command = e.KeyModifiers.HasFlag(KeyModifiers.Control)
                ? (System.Windows.Input.ICommand)vm.ConnectNewCommand
                : vm.ConnectCommand;
            if (command.CanExecute(null))
            {
                e.Handled = true;
                command.Execute(null);
            }
            return;
        }

        // Ctrl+A selects every visible regular node (Recent shadows excluded —
        // they are shortcuts, not batch-operation targets).
        if (e.Key == Key.A && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            var all = FlattenVisibleTreeNodes(vm.Nodes).Where(n => !n.IsRecent).ToList();
            if (all.Count > 0 && Tree.SelectedItems is { } items)
            {
                items.Clear();
                foreach (var node in all)
                    items.Add(node);
            }
            e.Handled = true;
        }
    }

    private bool HandleTreeNameEditorKey(MainWindowViewModel vm, TreeNodeViewModel node, Key key)
    {
        if (key == Key.Enter)
        {
            vm.CommitNodeNameEdit(node);
            TrackPendingTreeFocusRestore(vm.SelectedNode);
            return true;
        }

        if (key == Key.Escape)
        {
            vm.CancelNodeNameEdit(node);
            return true;
        }

        return false;
    }

    private static bool IsEditorCaretNavigationKey(Key key) =>
        key is Key.Up or Key.Down or Key.PageUp or Key.PageDown
            or Key.Left or Key.Right or Key.Home or Key.End;

    private void OnTreeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        if (IsTreeNameEditorSource(e.Source))
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

        if (vm.SelectedNode is { IsConnection: true })
        {
            // Ctrl+double-click forces a fresh session; a plain double-click
            // reuses an open tab.
            var command = e.KeyModifiers.HasFlag(KeyModifiers.Control)
                ? (System.Windows.Input.ICommand)vm.ConnectNewCommand
                : vm.ConnectCommand;
            if (command.CanExecute(null))
            {
                command.Execute(null);
                e.Handled = true;
            }
        }
    }

    /// <summary>Mirrors the TreeView's multi-selection into the view model so
    /// batch-capable commands can act on it.</summary>
    private void OnTreeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        var nodes = Tree.SelectedItems?.OfType<TreeNodeViewModel>().ToList()
            ?? new System.Collections.Generic.List<TreeNodeViewModel>();
        vm.SetSelectedNodes(nodes);
    }

    /// <summary>Arms the Recent one-click-launch suppression for the current
    /// input event; cleared on the next dispatcher tick so re-entrant selection
    /// changes from the same event are suppressed too.</summary>
    private void SuppressRecentAutoLaunchForCurrentEvent(MainWindowViewModel vm)
    {
        vm.SuppressRecentAutoLaunch = true;
        Dispatcher.UIThread.Post(
            () => vm.SuppressRecentAutoLaunch = false,
            DispatcherPriority.Background);
    }

    // - Ctrl/Shift+click extends the selection (handled by the TreeView itself).
    // - A plain press on a node inside the current multi-selection keeps the
    //   selection for a potential multi-drag or context menu.
    // - Right-clicking on a node selects it so the context menu acts on it.
    // - Clicking (left or right) on empty tree area clears the selection so
    //   new/paste operations target the root folder.
    private void OnTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        if (IsTreeNameEditorSource(e.Source))
            return;

        // Ctrl/Shift+click is a selection gesture: let the TreeView extend the
        // selection itself. Suppress the Recent one-click launch — adding a
        // Recent shadow to a selection must not fire it.
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control)
            || e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            SuppressRecentAutoLaunchForCurrentEvent(vm);
            return;
        }

        var hitItem = e.Source is Visual pressSource
            ? pressSource.FindAncestorOfType<TreeViewItem>(includeSelf: true)
            : null;
        var hitNode = hitItem?.DataContext as TreeNodeViewModel;

        // Plain press on a node that is part of the current multi-selection:
        // keep the selection intact so it can be dragged as a group or targeted
        // by the context menu. A left-click collapses to the pressed node on
        // release when no drag started.
        if (hitNode is not null
            && Tree.SelectedItems is { Count: > 1 } selectedItems
            && selectedItems.Contains(hitNode))
        {
            if (e.GetCurrentPoint(Tree).Properties.IsLeftButtonPressed)
            {
                if (!hitNode.IsRecent)
                {
                    _treeDragNode = hitNode;
                    _treeDragStart = e.GetPosition(Tree);
                    _isTreeDragging = false;
                }
                _pendingCollapseToNode = hitNode;
                e.Handled = true;
            }
            // Right press: the context menu acts on the whole selection.
            return;
        }

        // Arm a potential drag-move: any left press on a regular node may turn
        // into a drag once the pointer travels past the threshold.
        if (e.GetCurrentPoint(Tree).Properties.IsLeftButtonPressed
            && hitNode is { IsRecent: false })
        {
            _treeDragNode = hitNode;
            _treeDragStart = e.GetPosition(Tree);
            _isTreeDragging = false;
        }

        if (TryToggleFolderFromSource(e, out var toggledNode) && toggledNode is not null)
        {
            RememberFolderToggle(toggledNode);
            vm.SelectedNode = toggledNode;
            e.Handled = true;
            return;
        }

        if (hitNode is not null)
        {
            // Only right-click reselects; left-click on a node is handled by the
            // TreeView normally and would otherwise fight its own selection.
            if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
            {
                // Right-clicking a Recent shadow should open the context menu, not
                // fire the one-click launch the way a left-click selection would.
                if (hitNode is { IsRecent: true, IsConnection: true })
                    SuppressRecentAutoLaunchForCurrentEvent(vm);
                vm.SelectedNode = hitNode;
            }
        }
        else
        {
            // Empty area → clear selection so the next New/Paste targets root.
            vm.SelectedNode = null;
        }
    }

    // --- Tree drag & drop (move a node into a folder, or into the root) ---

    private void OnTreeDragPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_treeDragNode is not { } node)
            return;

        if (!e.GetCurrentPoint(Tree).Properties.IsLeftButtonPressed)
        {
            ResetTreeDrag(e);
            return;
        }

        var position = e.GetPosition(Tree);
        if (!_isTreeDragging)
        {
            var delta = position - _treeDragStart;
            if (Math.Abs(delta.X) < TreeDragThreshold && Math.Abs(delta.Y) < TreeDragThreshold)
                return;

            _isTreeDragging = true;
            _treeDragNodes = BuildTreeDragSet(node);
            e.Pointer.Capture(Tree);

            // Pressing a folder toggles its expansion, but this press turned out
            // to be a drag, not a click — undo the toggle.
            if (node.IsFolder && ReferenceEquals(node, _lastToggledFolder))
            {
                node.IsExpanded = !_lastToggledFolderExpanded;
                RememberFolderToggle(node);
            }
        }

        UpdateTreeDropTarget(position);
        e.Handled = true;
    }

    /// <summary>The nodes a drag starting on <paramref name="pressed"/> should
    /// move: the whole multi-selection when the pressed node is part of it,
    /// otherwise just the pressed node.</summary>
    private System.Collections.Generic.List<TreeNodeViewModel> BuildTreeDragSet(TreeNodeViewModel pressed)
    {
        if (Tree.SelectedItems is { Count: > 1 } items && items.Contains(pressed))
        {
            var set = items.OfType<TreeNodeViewModel>()
                .Where(n => n is { IsRecent: false, IsNameEditing: false })
                .ToList();
            if (set.Count > 0)
                return set;
        }

        return new System.Collections.Generic.List<TreeNodeViewModel> { pressed };
    }

    private void OnTreeDragPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var collapseTarget = _pendingCollapseToNode;
        _pendingCollapseToNode = null;

        if (_treeDragNode is not { } node)
        {
            // A press on a multi-selected Recent shadow never arms a drag; the
            // release still collapses the selection to the clicked node.
            if (collapseTarget is not null)
                CollapseSelectionTo(collapseTarget);
            return;
        }

        var wasDragging = _isTreeDragging;
        var dragNodes = _treeDragNodes
            ?? new System.Collections.Generic.List<TreeNodeViewModel> { node };
        string? dropPath = null;
        if (wasDragging && TryResolveTreeDrop(dragNodes, e.GetPosition(Tree), out _, out var targetPath))
            dropPath = targetPath;

        ResetTreeDrag(e);

        if (!wasDragging)
        {
            if (collapseTarget is not null)
                CollapseSelectionTo(collapseTarget);
            return;
        }

        if (dropPath is not null && DataContext is MainWindowViewModel vm)
            vm.MoveNodesTo(dragNodes, dropPath);
        e.Handled = true;
    }

    /// <summary>Collapses a multi-selection to a single node (the deferred
    /// deselection of a plain click on an already-selected node).</summary>
    private void CollapseSelectionTo(TreeNodeViewModel node)
    {
        if (Tree.SelectedItems is not { } items)
            return;

        items.Clear();
        items.Add(node);
    }

    private void UpdateTreeDropTarget(Point position)
    {
        TreeNodeViewModel? target = null;
        var valid = _treeDragNode is { } node
            && TryResolveTreeDrop(
                _treeDragNodes ?? new System.Collections.Generic.List<TreeNodeViewModel> { node },
                position, out target, out _);

        var highlight = valid ? target : null;
        if (!ReferenceEquals(highlight, _treeDropTarget))
        {
            if (_treeDropTarget is not null)
                _treeDropTarget.IsDragOver = false;
            _treeDropTarget = highlight;
            if (highlight is not null)
                highlight.IsDragOver = true;
        }

        Tree.Cursor = valid ? TreeDragMoveCursor : TreeDragNoDropCursor;
    }

    /// <summary>
    /// Resolves the folder a drop at <paramref name="position"/> would move the
    /// dragged nodes into: a folder row targets that folder, a connection row its
    /// containing folder, empty space the root. Returns false when the drop is
    /// invalid for every dragged node (Recent target, into itself/its subtree, or
    /// a same-folder no-op). <paramref name="highlightNode"/> is the hovered row
    /// to highlight — the row under the pointer, not necessarily the folder
    /// receiving the drop — and is null when the pointer is over empty space
    /// (root drop).
    /// </summary>
    private bool TryResolveTreeDrop(
        System.Collections.Generic.IReadOnlyList<TreeNodeViewModel> sources,
        Point position,
        out TreeNodeViewModel? highlightNode,
        out string targetPath)
    {
        highlightNode = null;
        targetPath = string.Empty;

        if (DataContext is not MainWindowViewModel vm)
            return false;

        TreeNodeViewModel? targetFolder = null;
        var hit = Tree.InputHitTest(position) as Visual;
        var item = hit?.FindAncestorOfType<TreeViewItem>(includeSelf: true);
        if (item?.DataContext is TreeNodeViewModel node)
        {
            if (node.IsRecent)
                return false;

            highlightNode = node;
            targetFolder = node.IsFolder ? node : node.Parent;
        }

        targetPath = targetFolder?.FullPath ?? vm.RootPath;

        // The drop is valid when at least one dragged node would actually move.
        foreach (var source in sources)
        {
            // Dropping where the node already lives is a no-op.
            var currentParent = Path.GetDirectoryName(
                source.FullPath.TrimEnd(Path.DirectorySeparatorChar));
            if (currentParent is not null && PathEquals(currentParent, targetPath))
                continue;

            // A folder cannot be dropped into itself or its own subtree.
            if (source.IsFolder && ConnectionStore.IsSameOrInside(source.FullPath, targetPath))
                continue;

            return true;
        }

        return false;
    }

    private void ResetTreeDrag(PointerEventArgs e)
    {
        if (_treeDropTarget is not null)
            _treeDropTarget.IsDragOver = false;

        _treeDropTarget = null;
        _treeDragNode = null;
        _treeDragNodes = null;
        _pendingCollapseToNode = null;
        _isTreeDragging = false;
        Tree.Cursor = Cursor.Default;
        e.Pointer.Capture(null);
    }

    private static bool IsTreeNameEditorSource(object? source) =>
        TryGetTreeNameEditorNode(source, out _);

    private bool IsTreeSource(object? source) =>
        source is Visual visual
        && visual.FindAncestorOfType<TreeView>(includeSelf: true) is { } tree
        && ReferenceEquals(tree, Tree);

    private static bool TryGetTreeNameEditorNode(object? source, out TreeNodeViewModel? node)
    {
        node = null;
        if (source is not Visual visual)
            return false;

        var editor = visual.FindAncestorOfType<TextBox>(includeSelf: true);
        if (editor is null
            || !editor.Classes.Contains("tree-name-editor")
            || editor.DataContext is not TreeNodeViewModel editorNode)
            return false;

        node = editorNode;
        return true;
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path;
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

        var yes = new Button
        {
            Name = "ConfirmYesButton",
            Content = Localizer.Get("DialogYes"),
            MinWidth = 80,
            IsDefault = true,
        };
        var no = new Button
        {
            Name = "ConfirmNoButton",
            Content = Localizer.Get("DialogNo"),
            MinWidth = 80,
            IsCancel = true,
        };

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
        int currentIntervalHours,
        string? currentEditorPath)
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

        // Editor for the file browser's remote editing (F4); blank = shell association.
        var editorBox = new TextBox
        {
            Text = currentEditorPath ?? "",
            Watermark = Localizer.Get("SettingsEditorWatermark"),
        };
        var editorBrowse = new Button { Content = Localizer.Get("Browse") };
        editorBrowse.Click += async (_, _) =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Localizer.Get("DialogPickEditorTitle"),
                AllowMultiple = false,
            });
            if (files.Count > 0 && files[0].TryGetLocalPath() is { } picked)
                editorBox.Text = picked;
        };
        var editorRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 8,
        };
        Grid.SetColumn(editorBox, 0);
        Grid.SetColumn(editorBrowse, 1);
        editorRow.Children.Add(editorBox);
        editorRow.Children.Add(editorBrowse);

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
                    new TextBlock { Text = Localizer.Get("SettingsEditorLabel"), FontWeight = FontWeight.SemiBold },
                    editorRow,
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
                intervalHours,
                editorBox.Text?.Trim()));
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
