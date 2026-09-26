using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Jeek.Avalonia.Localization;
using JeekRemoteManager.Controls;
using JeekRemoteManager.Models;
using JeekRemoteManager.Services;
using JeekRemoteManager.ViewModels;
using JeekTools;

namespace JeekRemoteManager.Views;

public partial class MainWindow : Window
{
    internal const string ProjectHomepage = "https://github.com/tifish/JeekRemoteManager";

    private enum TerminalOpenMode
    {
        ReuseTab,
        NewSession,
        NewTcpConnection,
    }

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
    private AgentCliPanelViewModel? _globalAgentViewModel;
    private readonly BastionSessionPool _bastionSessionPool = new();

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
        RightTabs.Items.Remove(GlobalAgentTab);
        GlobalAgentPanel.CloseRequested += OnGlobalAgentCloseRequested;
        UpdateWindowTitle();
        _defaultMinWidth = MinWidth;
        _defaultMinHeight = MinHeight;
        Tree.SelectionChanged += OnTreeSelectionChanged;
        // Hiding to the tray or minimizing means no terminal tab is on screen, so the
        // monitor panels of every tab should wind down as well.
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty || e.Property == WindowStateProperty)
                UpdateTerminalTabActivation();
        };
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
        PositionChanged += OnWindowPositionChanged;
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
        Closing += (_, _) => FlushCurrentSettingsState();
        // App cancels Closing to hide to the tray. That is not the end of the
        // window's lifetime: existing terminals and future logins still need this pool.
        Closed += (_, _) =>
        {
            _bastionSessionPool.Dispose();
            if (_globalAgentViewModel is not null)
                _ = _globalAgentViewModel.DisposeAsync();
        };
    }

    private void UpdateWindowTitle() =>
        Title = DebugInstanceContext.DecorateTitle(Localizer.Get("WindowTitle"));

    /// <summary>Localized main-menu labels exposed for Debug MCP verification.</summary>
    public IReadOnlyList<string> MoreActionsMenuHeaders =>
        (MoreActionsButton.Flyout as MenuFlyout)?.Items
        .OfType<MenuItem>()
        .Select(item => item.Header?.ToString() ?? string.Empty)
        .ToArray() ?? [];

    /// <summary>Authenticated bastion-pool state exposed for Debug MCP verification.</summary>
    public string BastionSessionPoolSnapshot => _bastionSessionPool.Snapshot;

    public int BastionSessionPoolCount => _bastionSessionPool.SessionCount;

    /// <summary>The real window-owned pool for network-free Debug MCP lifecycle checks.</summary>
    internal BastionSessionPool DebugBastionSessionPool => _bastionSessionPool;

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

    /// <summary>Application-wide agent surface exposed to the Debug MCP runtime probe.</summary>
    internal AgentCliPanelViewModel GlobalAgentViewModel => GetOrCreateGlobalAgentViewModel();

    internal string GlobalAgentWorkspacePath => GlobalAgentViewModel.WorkingDirectory;

    internal bool IsGlobalAgentTabActive => ReferenceEquals(RightTabs.SelectedItem, GlobalAgentTab);

    internal bool IsGlobalAgentTabOpen => RightTabs.Items.Contains(GlobalAgentTab);

    internal bool HasGlobalAgentViewModel => _globalAgentViewModel is not null;

    internal object? SelectedRightTab => RightTabs.SelectedItem;

    private void BuildMoreActionsMenu()
    {
        if (DataContext is not MainWindowViewModel)
            return;

        var menu = new MenuFlyout();
        foreach (var entry in ApplicationMenuDefinition.Items)
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

            var action = entry.Action;
            item.Click += (_, _) => ExecuteApplicationMenuAction(action);
            menu.Items.Add(item);
        }

        MoreActionsButton.Flyout = menu;
    }

    /// <summary>
    /// Runs one shared application-menu action. Used by both the main-window
    /// overflow menu and the tray menu so handlers stay in one place.
    /// </summary>
    public void ExecuteApplicationMenuAction(ApplicationMenuAction action)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        switch (action)
        {
            case ApplicationMenuAction.Settings:
                if (vm.OpenSettingsCommand.CanExecute(null))
                    vm.OpenSettingsCommand.Execute(null);
                break;
            case ApplicationMenuAction.LinkApplicationToProject:
                _ = ShowApplicationMcpLinkDialogAsync();
                break;
            case ApplicationMenuAction.ImportFromFinalShell:
                if (vm.ImportFinalShellCommand.CanExecute(null))
                    vm.ImportFinalShellCommand.Execute(null);
                break;
            case ApplicationMenuAction.ImportFromSecureCrt:
                if (vm.ImportSecureCrtCommand.CanExecute(null))
                    vm.ImportSecureCrtCommand.Execute(null);
                break;
            case ApplicationMenuAction.ImportFromXshell:
                if (vm.ImportXshellCommand.CanExecute(null))
                    vm.ImportXshellCommand.Execute(null);
                break;
            case ApplicationMenuAction.CheckForUpdates:
                if (vm.CheckForUpdatesCommand.CanExecute(null))
                    vm.CheckForUpdatesCommand.Execute(null);
                break;
            case ApplicationMenuAction.About:
                _ = ShowAboutDialogAsync();
                break;
            case ApplicationMenuAction.Exit:
                (Application.Current as App)?.RequestExit();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action), action, null);
        }
    }

    public Task ShowApplicationMcpLinkDialogAsync()
    {
        if (DataContext is not MainWindowViewModel vm)
            return Task.CompletedTask;
        return McpProjectLinkDialog.ShowApplicationAsync(this, vm);
    }

    /// <summary>Builds the application-wide MCP write dialog without showing it, for Debug MCP.</summary>
    public McpProjectLinkDialog CreateApplicationMcpLinkDialog()
    {
        if (DataContext is not MainWindowViewModel vm)
            throw new InvalidOperationException("The main window view model is not available.");
        return McpProjectLinkDialog.CreateApplication(vm);
    }

    /// <summary>
    /// Writes the application-wide product MCP entry into a project folder. Public so Debug MCP
    /// can exercise the exact main-menu action after bypassing only the native folder picker.
    /// </summary>
    public string WriteApplicationMcpToProject(
        string projectDirectory,
        IReadOnlyCollection<string>? selectedTargetPaths = null)
    {
        var project = AgentProjectLink.WriteApplicationInto(
            projectDirectory,
            selectedTargetPaths);
        if (DataContext is MainWindowViewModel vm)
            vm.StatusMessage = string.Format(Localizer.Get("AiLinkApplicationProjectDone"), project);
        return project;
    }

    /// <summary>Removes the application-wide product MCP entry written by the main menu.</summary>
    public string RemoveApplicationMcpFromProject(string projectDirectory)
    {
        var project = AgentProjectLink.RemoveApplicationFrom(projectDirectory);
        if (DataContext is MainWindowViewModel vm)
            vm.StatusMessage = string.Format(Localizer.Get("AiUnlinkApplicationProjectDone"), project);
        return project;
    }

    public async Task ShowAboutDialogAsync()
    {
        var dialog = CreateAboutDialog();
        await dialog.ShowDialog(this);
    }

    internal Window CreateAboutDialog()
    {
        var versionText = new TextBlock
        {
            Name = "AboutVersionText",
            Text = (DataContext as MainWindowViewModel)?.VersionDisplay ?? Localizer.Get("StatusDevBuild"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Classes = { "hint" },
        };
        var homepageText = new SelectableTextBlock
        {
            Name = "AboutHomepageText",
            Text = ProjectHomepage,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };
        var homepageButton = new Button
        {
            Name = "AboutHomepageButton",
            Content = Localizer.Get("ProjectHomepage"),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        homepageButton.Click += (_, _) => Process.Start(new ProcessStartInfo
        {
            FileName = ProjectHomepage,
            UseShellExecute = true,
        });

        var closeButton = new Button
        {
            Content = Localizer.Get("DialogOk"),
            HorizontalAlignment = HorizontalAlignment.Center,
            MinWidth = 88,
            Classes = { "accent" },
        };
        var dialog = new Window
        {
            Title = Localizer.Get("About"),
            Width = 440,
            Height = 300,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(28),
                Spacing = 14,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Children =
                {
                    new TextBlock
                    {
                        Text = Localizer.Get("WindowTitle"),
                        FontSize = 24,
                        FontWeight = FontWeight.SemiBold,
                        HorizontalAlignment = HorizontalAlignment.Center,
                    },
                    versionText,
                    homepageText,
                    homepageButton,
                    closeButton,
                },
            },
        };
        closeButton.Click += (_, _) => dialog.Close();
        return dialog;
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

    private Task<SettingsDialogResult?> PickSettingsAsync(
        StorageLocation current,
        string? currentCustomPath,
        string? currentLanguage,
        string? currentTheme,
        bool currentCheckOnStartup,
        int currentIntervalHours,
        string? currentEditorPath,
        TerminalAppearanceSettings currentTerminal) =>
        SettingsDialog.ShowAsync(
            this,
            PickFolderAsync,
            newPassword => (DataContext as MainWindowViewModel)?.ChangeMasterPassword(newPassword),
            ApplyTerminalAppearanceToOpenTabs,
            current,
            currentCustomPath,
            currentLanguage,
            currentTheme,
            currentCheckOnStartup,
            currentIntervalHours,
            currentEditorPath,
            currentTerminal);

    private void WireUp()
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        vm.Clipboard = Clipboard;
        vm.ConfirmAsync = ConfirmAsync;
        vm.PromptAsync = PromptAsync;
        vm.PickKeyFileAsync = PickKeyFileAsync;
        vm.PickSettingsAsync = PickSettingsAsync;
        vm.PickFolderAsync = (path, title) => PickFolderAsync(path, title);
        vm.OpenSshTerminalAsync = async (connection, sourcePath) =>
        {
            _ = await EnsureSshTerminalAsync(connection, sourcePath);
        };
        vm.OpenNewSshSessionAsync = async (connection, sourcePath) =>
        {
            _ = await EnsureSshTerminalAsync(connection, sourcePath, TerminalOpenMode.NewSession);
        };
        vm.OpenNewSshTcpConnectionAsync = async (connection, sourcePath) =>
        {
            _ = await EnsureSshTerminalAsync(connection, sourcePath, TerminalOpenMode.NewTcpConnection);
        };
        vm.EnsureSshTerminalAsync = EnsureSshTerminalAsync;
        vm.EnsureSshTerminalQuietlyAsync = (connection, sourcePath) =>
            EnsureSshTerminalAsync(connection, sourcePath, TerminalOpenMode.ReuseTab, select: false);
        vm.ApplyTerminalFontSize = ApplyTerminalFontToOpenTabs;
        ApplyTerminalFontToOpenTabs(vm.TerminalFontSize);
        vm.ApplyTerminalAppearance = ApplyTerminalAppearanceToOpenTabs;
        ApplyTerminalAppearanceToOpenTabs(vm.TerminalAppearance);
        vm.ConfirmHostKeyReplacement = HostKeyDialog.PromptReplace;
        vm.PromptUser = KeyboardInteractiveDialog.Prompt;
        vm.RequestFocusTree = FocusSelectedTreeItem;
        vm.RequestFocusTreeNode = FocusTreeItem;
        vm.RequestFocusTreeNameEditor = FocusTreeNameEditor;
        vm.PropertyChanged -= OnViewModelPropertyChanged;
        vm.PropertyChanged += OnViewModelPropertyChanged;
        RestoreWindowSize(vm);
        RestoreConnectionPanelWidth(vm);
    }

    private Task<TerminalScriptSession?> EnsureSshTerminalAsync(Connection connection, string? sourcePath) =>
        EnsureSshTerminalAsync(connection, sourcePath, TerminalOpenMode.ReuseTab);

    private Task<TerminalScriptSession?> EnsureSshTerminalAsync(
        Connection connection,
        string? sourcePath,
        TerminalOpenMode mode,
        bool select = true)
    {
        if (mode == TerminalOpenMode.ReuseTab)
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

        var duplicateSource = mode == TerminalOpenMode.NewSession
            ? FindTerminalTab(connection, sourcePath)
            : null;
        var (view, tab) = CreateTerminalTab(connection, sourcePath, select);
        if (duplicateSource is { } source)
        {
            var shared = LoginCommandSequence.HasStructuredReuseWorkflow(
                connection.EffectiveLoginCommands)
                ? null
                : source.View.ShareClientForDuplicate();
            view.Start(
                connection,
                sourcePath,
                shared,
                isDuplicatedSession: true);
        }
        else
        {
            view.Start(
                connection,
                sourcePath,
                forceNewTcpConnection: mode == TerminalOpenMode.NewTcpConnection);
        }
        return Task.FromResult<TerminalScriptSession?>(CreateTerminalScriptSession(view, tab));
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

    private void OnGlobalAgentToolbarClick(object? sender, RoutedEventArgs e)
    {
        OpenGlobalAgent();
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

        var session = await EnsureSshTerminalAsync(
            node.Connection,
            node.FullPath,
            TerminalOpenMode.NewSession);
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

}
