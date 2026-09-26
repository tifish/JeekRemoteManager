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

/// <summary>Terminal tabs: creation, headers, context menus, drag reordering, the global agent tab, and product MCP session addressing.</summary>
public partial class MainWindow
{
    #region Product MCP session addressing

    /// <summary>
    /// Open terminal tabs, in tab order, for the product MCP surface. Sessions are addressed
    /// by the same tree-relative path the agent workspace uses (<c>vps/bwg</c>, <c>vps/bwg (2)</c>)
    /// so an id is meaningful to a user and survives without a registry.
    /// </summary>
    internal IReadOnlyList<(string SessionId, TerminalView View, TabItem Tab)> EnumerateTerminalSessions()
    {
        var connectionsRoot = (DataContext as MainWindowViewModel)?.RootPath
                              ?? SettingsService.ResolveConnectionsRoot(StorageLocation.UserDirectory);
        var sessions = new List<(string, TerminalView, TabItem)>();
        foreach (var item in RightTabs.Items)
        {
            if (item is not TabItem { Content: TerminalView view } tab)
                continue;

            sessions.Add((BuildSessionId(connectionsRoot, view), view, tab));
        }

        return sessions;
    }

    private static string BuildSessionId(string connectionsRoot, TerminalView view) =>
        AgentCliWorkspace
            .ResolveRelativePath(connectionsRoot, view.SourcePath, view.Connection, view.SessionNumber)
            .Replace('\\', '/');

    /// <summary>Opens a terminal tab for the product MCP surface and returns its session id.</summary>
    internal string OpenTerminalSession(
        Connection connection,
        string? sourcePath,
        bool duplicate,
        bool activate)
    {
        TerminalView view;
        if (duplicate
            && EnumerateTerminalSessions().FirstOrDefault(s =>
                !string.IsNullOrEmpty(s.View.SourcePath)
                && !string.IsNullOrEmpty(sourcePath)
                && PathEquals(s.View.SourcePath!, sourcePath!)) is { View: { } source })
        {
            // Piggyback on the authenticated transport, like the tab's own Duplicate command.
            var shared = LoginCommandSequence.HasStructuredReuseWorkflow(connection.EffectiveLoginCommands)
                ? null
                : source.ShareClientForDuplicate();
            (view, _) = CreateTerminalTab(connection, sourcePath, activate);
            view.Start(connection, sourcePath, shared, isDuplicatedSession: true);
        }
        else
        {
            (view, _) = CreateTerminalTab(connection, sourcePath, activate);
            view.Start(connection, sourcePath);
        }

        if (activate)
            ActivateWindow();

        var connectionsRoot = (DataContext as MainWindowViewModel)?.RootPath
                              ?? SettingsService.ResolveConnectionsRoot(StorageLocation.UserDirectory);
        return BuildSessionId(connectionsRoot, view);
    }

    internal bool CloseTerminalSession(TabItem tab)
    {
        CloseTerminalTab(tab);
        return true;
    }

    /// <summary>Creates a process-free terminal tab for the Debug MCP lifecycle probe.</summary>
    /// <summary>Debug MCP: the appearance new tabs get, and a way to apply another one.</summary>
    internal TerminalAppearanceSettings DebugTerminalAppearance => _terminalAppearance;

    internal void DebugApplyTerminalAppearance(TerminalAppearanceSettings appearance) =>
        ApplyTerminalAppearanceToOpenTabs(appearance);

    internal TabItem DebugCreateTerminalTabForLifecycleProbe()
    {
        var (_, tab) = CreateTerminalTab(
            new Connection { Name = "terminal lifecycle probe", Type = ConnectionType.Wsl },
            sourcePath: null);
        return tab;
    }

    /// <summary>
    /// Drives all three connection-tree open modes against one network-free source
    /// tab. The two new tabs target a refusing local port; Debug MCP inspects their
    /// transport policies and closes them immediately.
    /// </summary>
    internal (
        TabItem Source,
        TabItem Connected,
        TabItem NewSession,
        TabItem NewTcpConnection) DebugOpenConnectionActionsProbe()
    {
        var connection = new Connection
        {
            Name = "connection actions probe",
            Type = ConnectionType.Ssh,
            Host = "127.0.0.1",
            Port = 1,
            Username = "probe",
            LoginCommands = "#reuse-enter\n1\n#duplicate\n#reuse-leave\nexit",
        };
        var (sourceView, sourceTab) = CreateTerminalTab(connection, sourcePath: null);
        sourceView.DebugAttachReusableConnectionProbe(connection);

        _ = EnsureSshTerminalAsync(
            connection,
            sourcePath: null,
            mode: TerminalOpenMode.ReuseTab);
        var connectedTab = SelectedTerminalTab
                           ?? throw new InvalidOperationException("Connect did not select a terminal tab.");

        _ = EnsureSshTerminalAsync(
            connection,
            sourcePath: null,
            mode: TerminalOpenMode.NewSession);
        var newSessionTab = SelectedTerminalTab
                            ?? throw new InvalidOperationException("New session did not open a terminal tab.");

        _ = EnsureSshTerminalAsync(
            connection,
            sourcePath: null,
            mode: TerminalOpenMode.NewTcpConnection);
        var newTcpConnectionTab = SelectedTerminalTab
                                  ?? throw new InvalidOperationException(
                                      "New TCP connection did not open a terminal tab.");

        return (sourceTab, connectedTab, newSessionTab, newTcpConnectionTab);
    }

    /// <summary>
    /// An SSH-typed probe tab, so Debug MCP can exercise the server monitor panel. The
    /// host never has to answer: the suspend/resume state machine is independent of
    /// whether a sample ever succeeds.
    /// </summary>
    internal TabItem DebugCreateSshTerminalTabForMonitorProbe()
    {
        var connection = new Connection
        {
            Name = "monitor suspend probe",
            Type = ConnectionType.Ssh,
            Host = "127.0.0.1",
        };
        var (view, tab) = CreateTerminalTab(connection, sourcePath: null);
        // Deliberately not Start(): the suspend/resume state machine is independent of
        // whether a sample ever succeeds, and this keeps the probe off the network.
        view.DebugAttachConnectionWithoutConnecting(connection);
        return tab;
    }

    /// <summary>Selects a tab, so Debug MCP can push a terminal tab to the background.</summary>
    internal void DebugSelectTab(TabItem tab) => RightTabs.SelectedItem = tab;

    /// <summary>The fixed editor tab, used by Debug MCP to move off a terminal tab.</summary>
    internal TabItem DebugEditorTab => EditorTab;

    /// <summary>Currently selected terminal tab, or null when another tab kind is selected.</summary>
    internal TabItem? SelectedTerminalTab =>
        RightTabs.SelectedItem as TabItem is { Content: TerminalView } tab ? tab : null;

    /// <summary>Brings the window forward so the user can act on a GUI prompt.</summary>
    internal void ActivateMainWindow() => ActivateWindow();

    /// <summary>Selects a tab and brings the window forward — used when a tool needs the user.</summary>
    internal void ActivateTerminalSession(TabItem tab, TerminalView view)
    {
        RightTabs.SelectedItem = tab;
        ActivateWindow();
        view.FocusTerminal();
    }

    /// <summary>
    /// Moves one terminal tab to a zero-based position among terminal tabs. The fixed editor and
    /// global-agent tabs and the current selection stay untouched. Used by the product MCP surface.
    /// </summary>
    internal void MoveTerminalSession(TabItem tab, int position)
    {
        var terminalTabs = RightTabs.Items
            .OfType<TabItem>()
            .Where(item => item.Content is TerminalView)
            .ToList();
        var currentPosition = terminalTabs.IndexOf(tab);
        if (currentPosition < 0)
            throw new InvalidOperationException("The terminal tab is not open.");
        if (position < 0 || position >= terminalTabs.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(position),
                position,
                $"Position must be between 0 and {terminalTabs.Count - 1}.");
        }
        if (position == currentPosition)
            return;

        var selectedItem = RightTabs.SelectedItem;
        RightTabs.Items.Remove(tab);
        terminalTabs.Remove(tab);

        var insertIndex = position >= terminalTabs.Count
            ? terminalTabs.Count == 0
                ? FirstTerminalTabIndex()
                : RightTabs.Items.IndexOf(terminalTabs[^1]) + 1
            : RightTabs.Items.IndexOf(terminalTabs[position]);
        RightTabs.Items.Insert(insertIndex, tab);
        RightTabs.SelectedItem = selectedItem;
    }

    private void ActivateWindow()
    {
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Show();
        Activate();
    }

    #endregion

    private (TerminalView View, TabItem Tab) CreateTerminalTab(Connection connection, string? sourcePath, bool select = true)
    {
        var sessionNumber = NextTerminalSessionNumber(connection, sourcePath);
        var adjacentTitles = FindAdjacentConnectionTitles(sourcePath);
        var appearance = _terminalAppearance;
        // Captured here, on the UI thread: the resolver runs on the dial's worker thread,
        // where reading the window's DataContext throws. The store is one instance for the
        // window's lifetime (a storage move re-roots it), so the capture never goes stale.
        var store = (DataContext as MainWindowViewModel)?.Store;
        var view = new TerminalView(appearance.ScrollbackLines)
        {
            SessionNumber = sessionNumber,
            BastionSessionPool = _bastionSessionPool,
            ResolveConnection = store is null ? null : store.TryLoadByTreePath,
        };
        view.PanelStateChanged += (_, _) => UpdateTerminalPanelToggleStates();
        var tab = new TabItem
        {
            Header = BuildTerminalTabHeader(connection, sessionNumber, adjacentTitles, out var closeButton),
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
        view.SetFontFamily(TerminalAppearance.ResolveFontFamily(appearance.FontFamily));
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

        var shared = LoginCommandSequence.HasStructuredReuseWorkflow(connection.EffectiveLoginCommands)
            ? null
            : source.ShareClientForDuplicate();
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

        // Header flips between start/stop each time the menu opens.
        var sessionLog = new MenuItem
        {
            Name = "TerminalTabSessionLogItem",
            Icon = CreateMenuIcon("\uE8A5", "log"),
        };
        sessionLog.Click += (_, _) =>
        {
            if (tab.Content is not TerminalView view)
                return;
            try
            {
                if (view.IsSessionLogging)
                    view.StopSessionLog();
                else
                    view.StartSessionLog();
            }
            catch (Exception ex)
            {
                if (DataContext is MainWindowViewModel vm)
                    vm.StatusMessage = ex.Message;
            }
        };

        var openLogFolder = new MenuItem
        {
            Header = Localizer.Get("SessionLogOpenFolder"),
            Icon = CreateMenuIcon("\uE838", "folder"),
        };
        openLogFolder.Click += (_, _) =>
        {
            Directory.CreateDirectory(TerminalSessionLog.Folder);
            Process.Start(new ProcessStartInfo(TerminalSessionLog.Folder) { UseShellExecute = true });
        };

        var close = new MenuItem
        {
            Header = Localizer.Get("Close"),
            Icon = CreateMenuIcon("\uE711", "danger"),
        };
        close.Click += (_, _) => CloseTerminalTab(tab);

        var closeOthers = new MenuItem
        {
            Header = Localizer.Get("CloseOthers"),
            Icon = CreateMenuIcon("\uE8BB", "danger"),
        };
        closeOthers.Click += (_, _) => CloseOtherTerminalTabs(tab);

        var closeAll = new MenuItem
        {
            Header = Localizer.Get("CloseAll"),
            Icon = CreateMenuIcon("\uE74D", "danger"),
        };
        closeAll.Click += (_, _) => CloseAllTerminalTabs();

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
        menu.Items.Add(sessionLog);
        menu.Items.Add(openLogFolder);
        menu.Opening += (_, _) => sessionLog.Header = Localizer.Get(
            tab.Content is TerminalView { IsSessionLogging: true } ? "SessionLogStop" : "SessionLogStart");
        menu.Items.Add(new Separator());
        menu.Items.Add(close);
        menu.Items.Add(closeOthers);
        menu.Items.Add(closeAll);
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

    /// <summary>Appearance new tabs are created with; replaced whenever it is applied.</summary>
    private TerminalAppearanceSettings _terminalAppearance = new(
        null, TerminalAppearance.DefaultSchemeName, TerminalAppearance.DefaultScrollbackLines);

    /// <summary>
    /// Applies a terminal appearance: the color scheme goes into the application resources
    /// every terminal renders from, the font to each open terminal. Scrollback is kept for
    /// tabs opened from now on — an existing buffer cannot be resized.
    /// </summary>
    private void ApplyTerminalAppearanceToOpenTabs(TerminalAppearanceSettings appearance)
    {
        _terminalAppearance = appearance;
        if (Application.Current is { } app)
            TerminalAppearance.ApplyColorScheme(app.Resources, TerminalAppearance.FindScheme(appearance.ColorScheme));

        var family = TerminalAppearance.ResolveFontFamily(appearance.FontFamily);
        GlobalAgentPanel.SetFontFamily(family);
        GlobalAgentPanel.RefreshTerminalColors();
        foreach (var item in RightTabs.Items)
        {
            if (item is TabItem { Content: TerminalView view })
            {
                view.SetFontFamily(family);
                view.RefreshTerminalColors();
            }
        }
    }

    private void ApplyTerminalFontToOpenTabs(int size)
    {
        GlobalAgentPanel.SetFontSize(size);
        foreach (var item in RightTabs.Items)
            if (item is TabItem { Content: TerminalView view })
                view.SetFontSize(size);
    }

    internal AgentCliPanelView DebugGlobalAgentPanel => GlobalAgentPanel;

    // Tab title stays the connection name; the remote OSC title does not override it.
    private static Control BuildTerminalTabHeader(
        Connection connection,
        int sessionNumber,
        IReadOnlyList<string> adjacentTitles,
        out Button closeButton)
    {
        var fullTitle = GetTerminalTabTitle(connection);
        var emphasis = TerminalTabTitle.FindEmphasis(fullTitle, adjacentTitles);
        var title = BuildTerminalTabTitle(fullTitle, emphasis);

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

    internal static Grid BuildTerminalTabTitle(
        string fullTitle,
        TerminalTabTitleEmphasis emphasis = default)
    {
        if (!emphasis.IsEmpty)
            return BuildEmphasizedTerminalTabTitle(fullTitle, emphasis);

        var titleParts = TerminalTabTitle.Split(fullTitle);
        var leadingTitle = CreateTerminalTabTitleText(
            titleParts.LeadingText,
            TextTrimming.CharacterEllipsis);
        var trailingTitle = CreateTerminalTabTitleText(
            titleParts.TrailingText,
            TextTrimming.None);

        // The first column is trimmed to the remaining width while the second
        // column always keeps the last few characters visible.
        var title = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            MaxWidth = 180,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(leadingTitle, 0);
        Grid.SetColumn(trailingTitle, 1);
        title.Children.Add(leadingTitle);
        title.Children.Add(trailingTitle);
        ToolTip.SetTip(title, fullTitle);
        return title;
    }

    private static Grid BuildEmphasizedTerminalTabTitle(
        string fullTitle,
        TerminalTabTitleEmphasis emphasis)
    {
        const double numericContextMaxWidth = 60;
        var fullDistinctText = fullTitle.Substring(emphasis.Start, emphasis.Length);
        var isPureNumber = TerminalTabTitle.IsPureNumber(fullDistinctText);
        var displayRange = isPureNumber
            ? TerminalTabTitle.FindNumericDisplayRange(fullTitle, emphasis)
            : emphasis;
        var commonPrefix = CreateTerminalTabTitleText(
            fullTitle[..displayRange.Start],
            TextTrimming.CharacterEllipsis);
        var distinct = isPureNumber
            ? CreateTerminalTabNumericContext(fullTitle, displayRange, emphasis)
            : CreateTerminalTabTitleText(
                fullDistinctText,
                TextTrimming.CharacterEllipsis);
        if (!isPureNumber)
        {
            distinct.FontWeight = FontWeight.Bold;
            distinct.Classes.Add("tab-title-emphasis");
        }

        var commonSuffix = CreateTerminalTabTitleText(
            fullTitle[displayRange.End..],
            TextTrimming.CharacterEllipsis);

        if (isPureNumber)
        {
            // A numeric identifier is often the only useful distinction between
            // otherwise identical server names. Keep up to four digits together
            // and let the shared context on both sides yield the space instead.
            commonPrefix.MaxWidth = numericContextMaxWidth;
            commonSuffix.MaxWidth = numericContextMaxWidth;
        }
        else
        {
            distinct.MaxWidth = 120;
        }

        // In similarity mode the distinguishing text gets the fixed-width
        // center column. Common text on either side yields first, so the
        // emphasized part remains visible without reserving the title's tail.
        var title = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions(
                $"{(commonPrefix.Text?.Length > 0 ? "*" : "0")},Auto,"
                + $"{(commonSuffix.Text?.Length > 0 ? "*" : "0")}"),
            MaxWidth = 180,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(commonPrefix, 0);
        Grid.SetColumn(distinct, 1);
        Grid.SetColumn(commonSuffix, 2);
        title.Children.Add(commonPrefix);
        title.Children.Add(distinct);
        title.Children.Add(commonSuffix);
        ToolTip.SetTip(title, fullTitle);
        return title;
    }

    private static TextBlock CreateTerminalTabNumericContext(
        string fullTitle,
        TerminalTabTitleEmphasis displayRange,
        TerminalTabTitleEmphasis emphasis)
    {
        var text = fullTitle.Substring(displayRange.Start, displayRange.Length);
        var emphasisStart = Math.Max(displayRange.Start, emphasis.Start) - displayRange.Start;
        var emphasisEnd = Math.Min(displayRange.End, emphasis.End) - displayRange.Start;
        var inlines = new InlineCollection();
        if (emphasisStart > 0)
            inlines.Add(text[..emphasisStart]);

        var emphasizedRun = new Run(text[emphasisStart..emphasisEnd])
        {
            FontWeight = FontWeight.Bold,
        };
        emphasizedRun.Classes.Add("tab-title-emphasis");
        inlines.Add(emphasizedRun);

        if (emphasisEnd < text.Length)
            inlines.Add(text[emphasisEnd..]);

        var title = CreateTerminalTabTitleText("", TextTrimming.None);
        title.Text = null;
        title.Inlines = inlines;
        title.Classes.Add("tab-title-numeric-context");
        return title;
    }

    private static TextBlock CreateTerminalTabTitleText(
        string text,
        TextTrimming textTrimming)
    {
        var title = new TextBlock
        {
            Text = text,
            TextTrimming = textTrimming,
            VerticalAlignment = VerticalAlignment.Center,
        };
        title.Classes.Add("tab-label");
        return title;
    }

    private IReadOnlyList<string> FindAdjacentConnectionTitles(string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)
            || DataContext is not MainWindowViewModel vm
            || FindRealConnectionNode(vm.Nodes, sourcePath) is not { } node)
        {
            return [];
        }

        IList<TreeNodeViewModel> siblings = node.Parent?.Children ?? vm.Nodes;
        var index = siblings.IndexOf(node);
        var adjacentTitles = new List<string>(2);
        AddAdjacentTitle(index - 1);
        AddAdjacentTitle(index + 1);
        return adjacentTitles;

        void AddAdjacentTitle(int adjacentIndex)
        {
            if (adjacentIndex < 0
                || adjacentIndex >= siblings.Count
                || siblings[adjacentIndex] is not { IsConnection: true, Connection: { } connection })
            {
                return;
            }

            adjacentTitles.Add(GetTerminalTabTitle(connection));
        }
    }

    private static TreeNodeViewModel? FindRealConnectionNode(
        IEnumerable<TreeNodeViewModel> nodes,
        string sourcePath)
    {
        foreach (var node in nodes)
        {
            if (!node.IsRecent
                && node.IsConnection
                && PathEquals(node.FullPath, sourcePath))
            {
                return node;
            }

            if (FindRealConnectionNode(node.Children, sourcePath) is { } found)
                return found;
        }

        return null;
    }

    private static string GetTerminalTabTitle(Connection connection) =>
        string.IsNullOrWhiteSpace(connection.Name) ? connection.Host : connection.Name;

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
        var globalAgentIndex = RightTabs.Items.IndexOf(GlobalAgentTab);
        return Math.Max(editorIndex, globalAgentIndex) + 1;
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

    private AgentCliPanelViewModel GetOrCreateGlobalAgentViewModel()
    {
        if (_globalAgentViewModel is not null)
            return _globalAgentViewModel;

        var mainVm = DataContext as MainWindowViewModel;
        var workingDirectory = AgentCliWorkspace.EnsureApplication();
        var preferred = mainVm?.AiProvider;
        var preferredKind = AgentCliCatalog.Discover()
            .FirstOrDefault(descriptor =>
                descriptor.Label.Equals(preferred, StringComparison.OrdinalIgnoreCase))
            ?.Kind;
        var preferredRunMode = mainVm is null
            ? AgentCliRunMode.Cli
            : preferredKind is { } kind
                ? mainVm.GetAiRunModeForKind(kind)
                : mainVm.AiRunMode;

        var vm = new AgentCliPanelViewModel(
            workingDirectory,
            preferred,
            hideSshTerminal: false,
            preferredRunMode: preferredRunMode,
            resolvePreferredRunMode: kind =>
                (DataContext as MainWindowViewModel)?.GetAiRunModeForKind(kind)
                ?? AgentCliRunMode.Cli,
            showConnectionOptions: false);

        vm.PrepareWorkspace = () => AgentCliWorkspace.EnsureApplication();
        vm.PropertyChanged += (_, e) =>
        {
            if (DataContext is not MainWindowViewModel ownerVm)
                return;
            if (e.PropertyName == nameof(AgentCliPanelViewModel.SelectedProvider))
                ownerVm.AiProvider = vm.SelectedProvider.Label;
            else if (e.PropertyName is nameof(AgentCliPanelViewModel.SelectedRunModeOption)
                     or nameof(AgentCliPanelViewModel.RunMode))
                ownerVm.SetAiRunModeForKind(vm.SelectedProvider.Kind, vm.RunMode);
        };

        GlobalAgentPanel.DataContext = vm;
        _globalAgentViewModel = vm;
        return vm;
    }

    private void EnsureGlobalAgentTabVisible()
    {
        if (IsGlobalAgentTabOpen)
            return;

        var editorIndex = RightTabs.Items.IndexOf(EditorTab);
        RightTabs.Items.Insert(Math.Max(0, editorIndex + 1), GlobalAgentTab);
    }

    /// <summary>
    /// Creates the optional tab and its view model without selecting it or launching an agent.
    /// Used by the Debug MCP lifecycle probe.
    /// </summary>
    internal AgentCliPanelViewModel PrepareGlobalAgentTabForDebug()
    {
        EnsureGlobalAgentTabVisible();
        return GetOrCreateGlobalAgentViewModel();
    }

    /// <summary>
    /// Selects the global agent tab and starts its configured surface. The Debug MCP uses
    /// <see cref="PrepareGlobalAgentTabForDebug"/> so it can verify the lifecycle and workspace
    /// without launching a third-party CLI.
    /// </summary>
    internal void OpenGlobalAgent()
    {
        EnsureGlobalAgentTabVisible();
        var vm = GetOrCreateGlobalAgentViewModel();
        RightTabs.SelectedItem = GlobalAgentTab;
        GlobalAgentPanel.NotifyHostLayoutChanged();
        _ = vm.EnsureStartedAsync();
    }

    internal async Task CloseGlobalAgentAsync()
    {
        if (IsGlobalAgentTabActive)
            RightTabs.SelectedItem = EditorTab;

        RightTabs.Items.Remove(GlobalAgentTab);

        var vm = _globalAgentViewModel;
        _globalAgentViewModel = null;
        GlobalAgentPanel.DataContext = null;
        if (vm is not null)
            await vm.DisposeAsync();
    }

    private async void OnGlobalAgentCloseRequested(object? sender, EventArgs e) =>
        await CloseGlobalAgentAsync();

    private void CloseTerminalTab(TabItem tab)
    {
        // Closing the active tab moves selection to the previous (left) tab, like a
        // normal tabbed editor. Closing a background tab leaves the selection alone.
        var wasSelected = ReferenceEquals(RightTabs.SelectedItem, tab);
        var index = RightTabs.Items.IndexOf(tab);

        if (tab.Content is TerminalView view)
            view.Close();

        RightTabs.Items.Remove(tab);
        // Localized controls subscribe to the application-wide localizer. If the
        // binding engine retains a removed TabItem through that subscription, leaving
        // Content attached keeps the entire terminal visual tree and its native
        // compositor resources alive. Sever every expensive branch after removal so
        // a stale header/menu binding can retain at most the empty TabItem shell.
        tab.Content = null;
        tab.Header = null;
        tab.ContextMenu = null;
        tab.DataContext = null;
        tab.Tag = null;

        if (wasSelected && RightTabs.Items.Count > 0)
            RightTabs.SelectedIndex = Math.Clamp(index - 1, 0, RightTabs.Items.Count - 1);

        RefreshAutomationPeerChildren(RightTabs);
    }

    /// <summary>
    /// Rebuilds the cached automation-peer child lists under <paramref name="root"/>.
    ///
    /// A peer marks its children invalid when its owner's visual children change, but it
    /// keeps handing back the list it built last until something asks for children again
    /// — and nothing does unless a screen reader is attached. That stale list still holds
    /// the closed tab's peer, and a peer owns its control, so the whole TerminalView (its
    /// visual tree, terminal buffer and view models) stays alive behind it.
    ///
    /// Only refreshes peers that already exist; it never creates one, so this costs
    /// nothing when no automation client has ever asked.
    /// </summary>
    private static void RefreshAutomationPeerChildren(Visual root)
    {
        foreach (var visual in root.GetSelfAndVisualDescendants())
        {
            if (visual is not Control control)
                continue;

            try
            {
                ControlAutomationPeer.FromElement(control)?.GetChildren();
            }
            catch
            {
                // A peer for a control that is mid-teardown is not worth failing a close over.
            }
        }
    }

    /// <summary>
    /// Closes every terminal tab except <paramref name="keep"/>. Permanent tabs
    /// (editor, global agent) are never closed.
    /// </summary>
    internal void CloseOtherTerminalTabs(TabItem keep)
    {
        foreach (var tab in EnumerateTerminalTabs().Where(t => !ReferenceEquals(t, keep)).ToList())
            CloseTerminalTab(tab);
    }

    /// <summary>
    /// Closes every terminal tab. Permanent tabs (editor, global agent) stay open.
    /// </summary>
    internal void CloseAllTerminalTabs()
    {
        foreach (var tab in EnumerateTerminalTabs().ToList())
            CloseTerminalTab(tab);
    }

    private IEnumerable<TabItem> EnumerateTerminalTabs()
    {
        foreach (var item in RightTabs.Items)
        {
            if (item is TabItem { Content: TerminalView } tab)
                yield return tab;
        }
    }

    // When a terminal tab becomes active, restore the control that had keyboard
    // focus in that tab (the shell, file browser, AI panel, etc.).
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

        if (ReferenceEquals((sender as TabControl)?.SelectedItem, GlobalAgentTab))
        {
            var globalVm = GetOrCreateGlobalAgentViewModel();
            _ = globalVm.EnsureStartedAsync();
            GlobalAgentPanel.NotifyHostLayoutChanged();
        }

        UpdateTerminalPanelToggleStates();
        UpdateTerminalTabActivation();
        view?.RestoreLastFocus();
    }

    /// <summary>
    /// Tells every terminal tab whether it is the one on screen, so background tabs can
    /// stop polling their server monitor. The window being hidden to the tray or
    /// minimized counts as no tab being visible.
    /// </summary>
    private void UpdateTerminalTabActivation()
    {
        if (RightTabs is null)
            return;

        var windowVisible = IsVisible && WindowState != WindowState.Minimized;
        var selected = RightTabs.SelectedItem;
        foreach (var item in RightTabs.Items)
        {
            if (item is TabItem { Content: TerminalView view } tab)
                view.SetTabActive(windowVisible && ReferenceEquals(tab, selected));
        }
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
}
