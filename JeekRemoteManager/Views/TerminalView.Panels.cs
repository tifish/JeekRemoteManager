using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Diagnostics;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using JeekRemoteManager.Models;
using JeekRemoteManager.Services;
using JeekRemoteManager.ViewModels;
using Renci.SshNet;
using SvcSystems.UI.Terminal;
using JeekTools;

namespace JeekRemoteManager.Views;

/// <summary>Side panels of a terminal tab: AI assistant, file browser and server monitor.</summary>
public partial class TerminalView
{
    /// <summary>Shows or hides the AI agent CLI side panel for this terminal, creating its
    /// per-connection MCP server and panel view-model on first open.</summary>
    public void ToggleAiPanel()
    {
        if (AiPanelHost.IsVisible)
        {
            _ = CloseAiPanelAsync();
            return;
        }

        if (_aiViewModel is null)
            _aiViewModel = CreateAgentCliPanelViewModel();

        AiPanelHost.IsVisible = true;
        // The AI panel open state is a global preference (not per-connection):
        // remember the last toggle so new SSH tabs and restarts reopen it after login.
        if (DataContext is MainWindowViewModel mainVm)
            mainVm.AiPanelOpen = true;
        ApplyAiPanelLayout();
        PanelStateChanged?.Invoke(this, EventArgs.Empty);

        // Match the main terminal font, then remeasure ConPTY against the opened column.
        AiPanel.SetFontSize(Term.FontSize);
        Dispatcher.UIThread.Post(async () =>
        {
            AiPanel.NotifyHostLayoutChanged();
            // Auto-start the agent CLI so opening the panel is enough.
            if (_aiViewModel is not null)
                await _aiViewModel.EnsureStartedAsync();
            if (!IsLoginManualInputPending)
                AiPanel.FocusCliTerminal();
        }, DispatcherPriority.Loaded);
        if (IsLoginManualInputPending)
            FocusTerminal();
    }

    /// <summary>Closes the AI panel and releases its process/session instead of
    /// keeping a hidden third-party CLI alive in the background.</summary>
    public Task CloseAiPanelAsync()
    {
        var ai = _aiViewModel;
        if (AiPanelHost.IsVisible)
            PersistAiPanelWidth();

        AiPanelHost.IsVisible = false;
        // Closing is also a preference change: new SSH sessions stay closed until
        // the user opens the panel again.
        if (DataContext is MainWindowViewModel mainVm)
            mainVm.AiPanelOpen = false;
        _aiViewModel = null;
        AiPanel.DataContext = null;
        ApplyAiPanelLayout();
        PanelStateChanged?.Invoke(this, EventArgs.Empty);
        FocusTerminal();

        return ai is null ? Task.CompletedTask : DisposeAiPanelAsync(ai);
    }

    private static async Task DisposeAiPanelAsync(AgentCliPanelViewModel ai) =>
        await ai.DisposeAsync().ConfigureAwait(false);

    /// <summary>Shows or hides the SFTP file browser panel below the terminal, dialing
    /// its own SFTP connection on first open (lazy: tabs that never open it never pay).</summary>
    public void ToggleFileBrowserPanel()
    {
        if (_connection is null)
            return;

        _fileBrowserViewModel ??= CreateFileBrowserViewModel();

        var show = !FileBrowserHost.IsVisible;
        if (!show)
            PersistFileBrowserHeight();

        FileBrowserHost.IsVisible = show;
        FileSplitter.IsVisible = show;
        ApplyFileBrowserPlacement();
        PanelStateChanged?.Invoke(this, EventArgs.Empty);

        if (show)
        {
            _fileBrowserViewModel.NotifyPanelShown();
            // Open at the remembered height (shared across tabs, persisted across runs).
            _fileBrowserHeight = Math.Clamp(
                (DataContext as MainWindowViewModel)?.FileBrowserPanelHeight ?? _fileBrowserHeight, 120, 1600);
            FileBrowserRow.MinHeight = 120;
            FileBrowserRow.Height = new GridLength(_fileBrowserHeight, GridUnitType.Pixel);
            // Focus the list so typing locates files instead of reaching the shell.
            Dispatcher.UIThread.Post(() => FileBrowser.FocusList(), DispatcherPriority.Background);
            _ = LoadFileBrowserAndRefocusAsync(_fileBrowserViewModel);
        }
        else
        {
            _fileBrowserViewModel.NotifyPanelHidden();
            // Collapse the row so it leaves no gap.
            FileBrowserRow.MinHeight = 0;
            FileBrowserRow.Height = new GridLength(0, GridUnitType.Pixel);
            if (IsSshTerminalHidden)
                Dispatcher.UIThread.Post(() => AiPanel.FocusCliTerminal(), DispatcherPriority.Background);
            else
                FocusTerminal();
        }
    }

    /// <summary>Waits for the panel's first directory listing, then focuses the list
    /// again so the first row is selected — unless the user has deliberately clicked
    /// back into the terminal in the meantime.</summary>
    private async Task LoadFileBrowserAndRefocusAsync(FileBrowserViewModel vm)
    {
        try
        {
            await vm.EnsureLoadedAsync();
        }
        catch
        {
            return;
        }

        if (_disposed || !FileBrowserHost.IsVisible)
            return;

        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        if (focused is Avalonia.Visual visual && Term.IsVisualAncestorOf(visual))
            return;

        FileBrowser.FocusList();
    }

    private FileBrowserViewModel CreateFileBrowserViewModel()
    {
        var connection = _connection!;
        string label;
        Func<IFileSystemSession> createSession;
        if (connection.IsWsl)
        {
            // An empty distro means the default one; resolve the actual name here
            // because the UNC share is addressed by name.
            var distro = connection.WslDistro.Trim();
            if (distro.Length == 0)
                distro = WslDistroService.ListDistros().FirstOrDefault(d => d.IsDefault)?.Name ?? "";
            label = distro.Length == 0 ? "WSL" : distro;
            var user = connection.Username.Trim();
            var resolvedDistro = distro;
            createSession = () => new WslFileSession(resolvedDistro, user);
        }
        else
        {
            var host = connection.Host.Trim();
            label = string.IsNullOrWhiteSpace(connection.Username)
                ? host
                : $"{connection.Username.Trim()}@{host}";
            // Each SFTP session dials its own connection; build fresh auth methods
            // per dial (they hold per-attempt state).
            var resolveConnection = ResolveConnection;
            createSession = () => new SftpSession(connection, resolveConnection, promptUser: KeyboardInteractiveDialog.Prompt);
        }

        var vm = new FileBrowserViewModel(
            createSession,
            path =>
            {
                // Ctrl+U discards anything half-typed at the prompt so `cd` runs clean.
                WriteToShell("\u0015cd " + QuoteForRemoteShell(path) + "\r");
                FocusTerminal();
            },
            label,
            () => (DataContext as MainWindowViewModel)?.FileBrowserEditorPath);
        FileBrowser.DataContext = vm;
        return vm;
    }

    private void PersistFileBrowserHeight()
    {
        if (!FileBrowserRow.Height.IsAbsolute || FileBrowserRow.Height.Value <= 0)
            return;

        _fileBrowserHeight = FileBrowserRow.Height.Value;
        if (DataContext is MainWindowViewModel vm)
            vm.FileBrowserPanelHeight = _fileBrowserHeight;
    }

    /// <summary>Shows or hides the server monitor panel right of the terminal. Sampling
    /// runs only while the panel is visible, over a hidden duplicated shell on this
    /// terminal's SSH connection — SSH-type connections only.</summary>
    public void ToggleMonitorPanel()
    {
        if (_connection is not { IsSsh: true })
            return;

        _monitorViewModel ??= CreateServerMonitorViewModel();

        var show = !MonitorPanelHost.IsVisible;
        if (!show)
            PersistMonitorPanelWidth();

        MonitorPanelHost.IsVisible = show;
        MonitorSplitter.IsVisible = show;
        PanelStateChanged?.Invoke(this, EventArgs.Empty);

        if (show)
        {
            // Open at the remembered width (shared across tabs, persisted across runs).
            _monitorPanelWidth = Math.Clamp(
                (DataContext as MainWindowViewModel)?.MonitorPanelWidth ?? _monitorPanelWidth, 180, 600);
            MonitorColumn.MinWidth = 180;
            MonitorColumn.Width = new GridLength(_monitorPanelWidth, GridUnitType.Pixel);
            _monitorViewModel.Start();
            ApplyMonitorSamplingState();
        }
        else
        {
            // Collapse the column so it leaves no gap.
            MonitorColumn.MinWidth = 0;
            MonitorColumn.Width = new GridLength(0, GridUnitType.Pixel);
            _monitorSuspendTimer?.Stop();
            _monitorViewModel.Stop();
            FocusTerminal();
        }
    }

    /// <summary>
    /// How long monitor sampling keeps running after this tab goes to the background.
    /// Flipping between tabs (or briefly minimizing) must not stop and restart the poll
    /// loop, so suspension waits out this delay; coming back cancels it immediately.
    /// </summary>
    private static readonly TimeSpan MonitorSuspendDelay = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Called by the host when this tab becomes (or stops being) the visible one, which
    /// includes the window being hidden to the tray or minimized. Sampling a server the
    /// user cannot see costs a remote command every two seconds for nothing.
    /// </summary>
    public void SetTabActive(bool isActive)
    {
        _tabActive = isActive;
        ApplyMonitorSamplingState();
    }

    /// <summary>Whether monitor sampling is currently paused, exposed for Debug MCP.</summary>
    public bool IsMonitorSamplingSuspended => _monitorViewModel?.IsMonitorSuspended == true;

    /// <summary>Whether the suspend grace period is counting down, exposed for Debug MCP.</summary>
    public bool IsMonitorSuspendPending => _monitorSuspendTimer?.IsEnabled == true;

    /// <summary>Skips the remaining grace period, for Debug MCP and teardown.</summary>
    public void FlushPendingMonitorSuspend()
    {
        if (_monitorSuspendTimer is not { IsEnabled: true })
            return;

        _monitorSuspendTimer.Stop();
        _monitorViewModel?.Suspend();
    }

    private void ApplyMonitorSamplingState()
    {
        if (_monitorViewModel is null)
            return;

        _monitorSuspendTimer?.Stop();

        // Only the visible panel of the visible tab should be talking to the server.
        if (_tabActive && MonitorPanelHost.IsVisible)
        {
            _monitorViewModel.Resume();
            return;
        }

        if (_monitorSuspendTimer is null)
        {
            _monitorSuspendTimer = new DispatcherTimer { Interval = MonitorSuspendDelay };
            _monitorSuspendTimer.Tick += (_, _) =>
            {
                _monitorSuspendTimer!.Stop();
                _monitorViewModel?.Suspend();
            };
        }

        _monitorSuspendTimer.Start();
    }

    private ServerMonitorViewModel CreateServerMonitorViewModel()
    {
        var connection = _connection!;
        var host = connection.Host.Trim();
        var user = connection.Username.Trim();
        var label = (user.Length == 0 ? host : $"{user}@{host}")
            + (connection.Port == 22 ? "" : $":{connection.Port}");

        var vm = new ServerMonitorViewModel(
            // Takes a counted reference on the live shared client so the transport
            // survives until the monitor lets go, even if the tab reconnects meanwhile.
            () => _client is { IsConnected: true } client && client.TryAddRef() ? client : null,
            connection.TerminalType,
            connection.EffectiveLoginCommands,
            label,
            host,
            BastionSessionPool,
            connection);
        MonitorPanel.DataContext = vm;
        return vm;
    }

    private void PersistMonitorPanelWidth()
    {
        if (!MonitorColumn.Width.IsAbsolute || MonitorColumn.Width.Value <= 0)
            return;

        _monitorPanelWidth = MonitorColumn.Width.Value;
        if (DataContext is MainWindowViewModel vm)
            vm.MonitorPanelWidth = _monitorPanelWidth;
    }

    /// <summary>
    /// Lays out the terminal/AI columns for the current panel state. Three states: panel
    /// hidden (terminal full width), side panel (terminal + splitter + fixed-width panel),
    /// and hidden-terminal mode (the AI panel takes the whole tab above an optional
    /// file browser).
    /// </summary>
    private void ApplyAiPanelLayout()
    {
        var show = AiPanelHost.IsVisible;
        var hideSshTerminal = IsSshTerminalHidden;

        // Hiding the terminal area (not just collapsing the column) keeps the pty size
        // stable, so remote command output isn't rewrapped to a zero-width window.
        TerminalArea.IsVisible = !hideSshTerminal;
        TerminalColumn.MinWidth = hideSshTerminal ? 0 : 200;
        TerminalColumn.Width = hideSshTerminal
            ? new GridLength(0, GridUnitType.Pixel)
            : new GridLength(1, GridUnitType.Star);

        AiSplitter.IsVisible = show && !hideSshTerminal;

        if (show)
            Dispatcher.UIThread.Post(() => AiPanel.NotifyHostLayoutChanged(), DispatcherPriority.Loaded);

        if (!show)
        {
            // Collapse the column so it leaves no gap.
            AiColumn.MinWidth = 0;
            AiColumn.Width = new GridLength(0, GridUnitType.Pixel);
        }
        else if (hideSshTerminal)
        {
            AiColumn.MinWidth = 0;
            AiColumn.Width = new GridLength(1, GridUnitType.Star);
        }
        else
        {
            // Open at the remembered width (shared across tabs, persisted across runs).
            _aiPanelWidth = Math.Clamp(
                (DataContext as MainWindowViewModel)?.AiPanelWidth ?? _aiPanelWidth, 240, 1200);
            AiColumn.MinWidth = 240;
            AiColumn.Width = new GridLength(_aiPanelWidth, GridUnitType.Pixel);
        }

        ApplyFileBrowserPlacement();
    }

    /// <summary>Places the shared file-browser row below the terminal normally, or below
    /// the AI conversation while the SSH terminal is hidden.</summary>
    private void ApplyFileBrowserPlacement()
    {
        var hideSshTerminal = IsSshTerminalHidden;
        var column = hideSshTerminal ? 0 : 2;
        Grid.SetColumn(FileSplitter, column);
        Grid.SetColumn(FileBrowserHost, column);

        // With the SSH terminal visible, the AI panel continues alongside both terminal rows.
        // When hidden, it yields the remembered bottom row only while the browser is open.
        Grid.SetRowSpan(AiPanelHost, hideSshTerminal && FileBrowserHost.IsVisible ? 1 : 3);
    }

    private void PersistAiPanelWidth()
    {
        if (!AiColumn.Width.IsAbsolute || AiColumn.Width.Value <= 0)
            return;

        _aiPanelWidth = AiColumn.Width.Value;
        if (DataContext is MainWindowViewModel vm)
            vm.AiPanelWidth = _aiPanelWidth;
    }

    private AgentCliPanelViewModel CreateAgentCliPanelViewModel()
    {

        // Durable workspace mirrors the connection tree path (e.g. Connections/vps/bwg.json
        // → %LOCALAPPDATA%\JeekRemoteManager\AgentWorkspaces\vps\bwg). Write AGENTS.md and
        // project MCP configs as soon as the panel opens so desktop Claude/Codex/Grok can
        // open this folder without any CLI flags.
        var workingDir = ResolveAgentCliWorkingDirectory();

        var preferred = (DataContext as MainWindowViewModel)?.AiProvider;
        var mainVm = DataContext as MainWindowViewModel;
        // Resolve initial run mode from the slot that matches the preferred provider (agents
        // with Desktop vs without), before the panel may fall back to another available agent.
        var preferredKind = AgentCliCatalog.Discover()
            .FirstOrDefault(d => d.Label.Equals(preferred, StringComparison.OrdinalIgnoreCase))
            ?.Kind;
        var preferredRunMode = mainVm is null
            ? AgentCliRunMode.Cli
            : preferredKind is { } kind
                ? mainVm.GetAiRunModeForKind(kind)
                : mainVm.AiRunMode;
        var vm = new AgentCliPanelViewModel(
            workingDir,
            preferred,
            hideSshTerminal: mainVm?.AiHideSshTerminal ?? false,
            onHideSshTerminalChanged: hide =>
            {
                if (DataContext is MainWindowViewModel ownerVm)
                    ownerVm.AiHideSshTerminal = hide;
                PersistAiPanelWidth();
                ApplyAiPanelLayout();
                if (IsLoginManualInputPending)
                    FocusTerminal();
            },
            preferredRunMode: preferredRunMode,
            resolvePreferredRunMode: kind =>
                (DataContext as MainWindowViewModel)?.GetAiRunModeForKind(kind) ?? AgentCliRunMode.Cli);

        // Rewrite AGENTS.md + project MCP configs (including Codex default_tools_approval_mode)
        // before each launch so persisted MCP permissions stay current.
        vm.PrepareWorkspace = () => ResolveAgentCliWorkingDirectory();

        // Workspace identity used when the user writes this connection into a project folder.
        vm.ResolveLinkContext = () => ResolveAgentCliLink();

        // Remember last-chosen provider and per-family run mode across tabs and runs.
        // Claude/Codex share AiRunMode; Grok uses AiGrokRunMode (no Desktop).
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

        AiPanel.DataContext = vm;
        return vm;
    }

    /// <summary>
    /// %LOCALAPPDATA%\JeekRemoteManager\AgentWorkspaces\&lt;tree-relative-path&gt; for this
    /// tab (session 2+ uses a sibling folder matching the tab title, e.g. <c>bwg (2)</c>).
    /// Rewrites AGENTS.md / CLAUDE.md and the project MCP configs so agents need no
    /// command-line context.
    /// </summary>
    private string ResolveAgentCliWorkingDirectory()
    {
        var mainVm = DataContext as MainWindowViewModel;
        var connectionsRoot = mainVm?.RootPath
            ?? SettingsService.ResolveConnectionsRoot(StorageLocation.UserDirectory);

        return AgentCliWorkspace.Ensure(
            connectionsRoot,
            _sourcePath,
            _connection,
            SessionNumber);
    }

    /// <summary>
    /// This tab's workspace identity for <see cref="AgentProjectLink"/>. Linked projects get
    /// a stdio launch of the JeekRemoteManagerMcp adapter pinned to this connection, so nothing in them
    /// depends on a listener that is up right now.
    /// </summary>
    private AgentWorkspaceLink ResolveAgentCliLink()
    {
        var mainVm = DataContext as MainWindowViewModel;
        var connectionsRoot = mainVm?.RootPath
            ?? SettingsService.ResolveConnectionsRoot(StorageLocation.UserDirectory);

        return AgentCliWorkspace.BuildLink(
            connectionsRoot,
            _sourcePath,
            _connection,
            SessionNumber);
    }

    private IAgentRemoteTools? _agentRemoteTools;

    /// <summary>
    /// This tab's remote-terminal tool implementation, reached by the app-wide product MCP
    /// server, which addresses tabs by session id.
    /// </summary>
    public IAgentRemoteTools AgentRemoteTools =>
        _agentRemoteTools ??= new TerminalAgentRemoteTools(this);

    /// <summary>True while this tab has a usable shell (connected or connecting).</summary>
    public bool IsSessionLive => CanReuseSession;

    private string BuildAiConversationLabel()
    {
        var name = _connection?.Name?.Trim();
        return !string.IsNullOrWhiteSpace(name)
            ? name
            : _connection?.TargetLabel ?? "Unknown connection";
    }
}
