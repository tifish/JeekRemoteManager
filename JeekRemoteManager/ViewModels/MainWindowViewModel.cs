using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jeek.Avalonia.Localization;
using JeekRemoteManager.Models;
using JeekRemoteManager.Services;
using JeekTools;

namespace JeekRemoteManager.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private enum ConnectionLaunchMode
    {
        Connect,
        NewSession,
        NewTcpConnection,
    }

    private readonly ConnectionStore _store;
    private readonly ConnectionLauncher _launcher;
    private readonly SettingsService _settings;
    private readonly RemoteScriptStore _scriptStore;
    private ScriptExecutionContext? _scriptContext;

    // Internal (in-app) clipboard for copy/cut/paste of nodes. Multi-selection
    // puts several entries on the clipboard at once.
    private sealed record ClipboardEntry(string Path, bool IsFolder);
    private readonly List<ClipboardEntry> _clipboardEntries = new();
    private bool _clipboardIsCut;

    // Current tree multi-selection, kept in sync with the TreeView's
    // SelectedItems by the view. SelectedNode stays the primary (anchor) node.
    private readonly List<TreeNodeViewModel> _selectedNodes = new();

    // Auto-save: which node the current editor is bound to, plus a debounce timer.
    private TreeNodeViewModel? _editingNode;
    private DispatcherTimer? _autoSaveTimer;
    private bool _editorHasPendingChanges;
    private static readonly TimeSpan AutoSaveDelay = TimeSpan.FromMilliseconds(600);
    private TreeNodeViewModel? _renamingNode;

    // Watches external configuration changes. Portable mode watches the whole
    // Config folder because settings, connections, and custom scripts may all
    // be edited outside the app.
    private FileSystemWatcher? _watcher;
    private DispatcherTimer? _watchReloadTimer;
    private readonly HashSet<string> _pendingWatchedPaths = new(StringComparer.OrdinalIgnoreCase);
    private bool _watchingPortableConfig;
    private static readonly TimeSpan ConnectionWatchReloadDelay = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan PortableConfigReloadDelay = TimeSpan.FromSeconds(10);

    // Synthetic node showing the last-used connections at the top of the tree.
    private const int RecentMax = 10;
    private const string RecentSentinelPath = "<recent>";
    private static readonly TimeSpan RecentRebuildDelay = TimeSpan.FromSeconds(1.5);
    private TreeNodeViewModel? _recentGroup;
    private DispatcherTimer? _recentRebuildTimer;

    private sealed class ScriptExecutionContext
    {
        public ScriptExecutionContext(TreeNodeViewModel node, TerminalScriptSession? terminal)
        {
            Node = node;
            Terminal = terminal;
        }

        public TreeNodeViewModel Node { get; }

        public TerminalScriptSession? Terminal { get; set; }
    }

    public MainWindowViewModel(
        ConnectionStore store,
        ConnectionLauncher launcher,
        SettingsService settings,
        RemoteScriptStore? scriptStore = null)
    {
        _store = store;
        _launcher = launcher;
        _settings = settings;
        _scriptStore = scriptStore ?? new RemoteScriptStore();
        _store.SetRoot(_settings.ResolveConnectionsRoot());
        _scriptStore.SetRoot(_settings.ResolveScriptsRoot());
        ReloadScripts();
        ReloadTree(_settings.Settings.LastSelectedConnectionPath);
        StartWatchingCurrentStorage();

        // Refresh language-dependent computed properties when the user switches language.
        Localizer.LanguageChanged += (_, _) =>
        {
            StatusMessage = L("StatusReady");
            OnPropertyChanged(nameof(TargetDescription));
            OnPropertyChanged(nameof(VersionDisplay));
            OnPropertyChanged(nameof(PlaceholderHint));
            OnPropertyChanged(nameof(EditorTabTitle));
            if (_recentGroup != null)
                _recentGroup.Name = L("RecentGroup");
        };
    }

    // Parameterless constructor for the XAML designer.
    public MainWindowViewModel() : this(new ConnectionStore(), new ConnectionLauncher(), new SettingsService())
    {
    }

    /// <summary>Top-level nodes (the contents of the root connections folder).</summary>
    public ObservableCollection<TreeNodeViewModel> Nodes { get; } = new();

    public ObservableCollection<RemoteScriptSuite> ScriptSuites { get; } = new();

    public string ScriptsRootPath => _scriptStore.RootPath;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConnectNewSessionCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConnectNewTcpConnectionCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    [NotifyCanExecuteChangedFor(nameof(RenameCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyCommand))]
    [NotifyCanExecuteChangedFor(nameof(CutCommand))]
    [NotifyCanExecuteChangedFor(nameof(BrowseKeyCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunSelectedScriptBindingCommand))]
    [NotifyCanExecuteChangedFor(nameof(RevealInTreeCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveFromRecentCommand))]
    [NotifyPropertyChangedFor(nameof(TargetDescription))]
    [NotifyPropertyChangedFor(nameof(IsRecentGroupContext))]
    [NotifyPropertyChangedFor(nameof(IsRecentConnectionContext))]
    [NotifyPropertyChangedFor(nameof(IsRegularContext))]
    [NotifyPropertyChangedFor(nameof(IsRegularConnectionContext))]
    [NotifyPropertyChangedFor(nameof(IsSshConnectionContext))]
    [NotifyPropertyChangedFor(nameof(IsShellConnectionContext))]
    [NotifyPropertyChangedFor(nameof(IsShellSelectionContext))]
    private TreeNodeViewModel? _selectedNode;

    /// <summary>True when the next SelectedNode assignment is a right-click target
    /// for the context menu and should not trigger the one-click Recent shortcut.</summary>
    public bool SuppressRecentAutoLaunch { get; set; }

    /// <summary>True when more than one tree node is selected.</summary>
    public bool HasMultiSelection => _selectedNodes.Count > 1;

    /// <summary>
    /// Replaces the tracked multi-selection with the TreeView's current
    /// SelectedItems (called by the view on every selection change).
    /// </summary>
    public void SetSelectedNodes(IReadOnlyList<TreeNodeViewModel> nodes)
    {
        _selectedNodes.Clear();
        _selectedNodes.AddRange(nodes);

        // A finished batch panel is dismissed by moving on to a single node; a
        // running one stays visible so its progress is not lost.
        if (_selectedNodes.Count <= 1 && BatchPanel is { IsRunning: false })
            BatchPanel = null;

        OnPropertyChanged(nameof(HasMultiSelection));
        OnPropertyChanged(nameof(IsRecentGroupContext));
        OnPropertyChanged(nameof(IsRecentConnectionContext));
        OnPropertyChanged(nameof(IsRegularContext));
        OnPropertyChanged(nameof(IsRegularConnectionContext));
        OnPropertyChanged(nameof(IsSshConnectionContext));
        OnPropertyChanged(nameof(IsShellConnectionContext));
        OnPropertyChanged(nameof(IsShellSelectionContext));
        OnPropertyChanged(nameof(ShowConnectionEditor));
        OnPropertyChanged(nameof(ShowPlaceholder));
        OnPropertyChanged(nameof(PlaceholderHint));
        NotifyTreeActionCanExecuteChanged();
        ConnectNewSessionCommand.NotifyCanExecuteChanged();
        ConnectNewTcpConnectionCommand.NotifyCanExecuteChanged();
        RevealInTreeCommand.NotifyCanExecuteChanged();
        RemoveFromRecentCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// The nodes a batch-capable command should act on: the multi-selection when
    /// one is active (and still contains the anchor), otherwise the single
    /// selected node.
    /// </summary>
    private IReadOnlyList<TreeNodeViewModel> EffectiveSelection()
    {
        if (_selectedNodes.Count > 1
            && (SelectedNode is null || _selectedNodes.Contains(SelectedNode)))
        {
            return _selectedNodes.ToList();
        }

        return SelectedNode is { } node
            ? new[] { node }
            : Array.Empty<TreeNodeViewModel>();
    }

    /// <summary>
    /// Drops nodes that live inside another selected folder, so structural batch
    /// operations (delete, copy, move) don't process an item twice.
    /// </summary>
    private static List<TreeNodeViewModel> RemoveNestedNodes(IReadOnlyList<TreeNodeViewModel> nodes)
    {
        var result = new List<TreeNodeViewModel>();
        foreach (var node in nodes)
        {
            var nestedInAnother = nodes.Any(other =>
                !ReferenceEquals(other, node)
                && other.IsFolder
                && !PathEquals(other.FullPath, node.FullPath)
                && ConnectionStore.IsSameOrInside(other.FullPath, node.FullPath));
            if (!nestedInAnother)
                result.Add(node);
        }

        return result;
    }

    /// <summary>True when the selection is the synthetic "Recent" group folder.</summary>
    public bool IsRecentGroupContext => !HasMultiSelection && SelectedNode is { IsRecent: true, IsFolder: true };

    /// <summary>True when the selection consists of connection shadows under the "Recent" group.</summary>
    public bool IsRecentConnectionContext => HasMultiSelection
        ? _selectedNodes.All(n => n is { IsRecent: true, IsConnection: true })
        : SelectedNode is { IsRecent: true, IsConnection: true };

    /// <summary>True when the selection is on regular (non-Recent) nodes or empty area.</summary>
    public bool IsRegularContext => HasMultiSelection
        ? _selectedNodes.All(n => !n.IsRecent)
        : SelectedNode is null || !SelectedNode.IsRecent;

    /// <summary>True when the selection is a single regular (non-Recent) connection that can be edited.</summary>
    public bool IsRegularConnectionContext =>
        !HasMultiSelection && SelectedNode is { IsRecent: false, IsConnection: true };

    public bool IsSshConnectionContext =>
        !HasMultiSelection
        && SelectedNode is { IsConnection: true, Connection: { Type: ConnectionType.Ssh } };

    /// <summary>True when the selection is a single connection whose terminal can
    /// run scripts (SSH or WSL).</summary>
    public bool IsShellConnectionContext =>
        !HasMultiSelection
        && SelectedNode is { IsConnection: true, Connection: { Type: ConnectionType.Ssh or ConnectionType.Wsl } };

    /// <summary>True when every selected node (single or multi) is a connection
    /// whose terminal can run scripts — gates the Run Script menu item, which
    /// batches over the whole selection.</summary>
    public bool IsShellSelectionContext => HasMultiSelection
        ? _selectedNodes.All(n =>
            n is { IsConnection: true, Connection.Type: ConnectionType.Ssh or ConnectionType.Wsl })
        : IsShellConnectionContext;

    /// <summary>Human-readable description of where New/Paste will create items.</summary>
    public string TargetDescription
    {
        get
        {
            var folder = TargetFolder();
            if (string.Equals(
                    Path.GetFullPath(folder),
                    Path.GetFullPath(_store.RootPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                return L("StatusTargetRoot");
            }
            return L("StatusTarget", Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar)));
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowConnectionEditor))]
    [NotifyPropertyChangedFor(nameof(ShowPlaceholder))]
    private ConnectionEditorViewModel? _editor;

    [ObservableProperty]
    private string _statusMessage = Localizer.Get("StatusReady");

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PasteCommand))]
    private bool _hasClipboard;

    /// <summary>
    /// Whether the connection editor reveals the saved password in clear text.
    /// Enabling requires re-entering the master password and auto-disables after
    /// a short idle period — both handled by the view. Kept on the main VM (not
    /// the editor) so switching between connections doesn't reset the reveal
    /// state mid-session.
    /// </summary>
    [ObservableProperty]
    private bool _showPassword;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasScriptExecution))]
    [NotifyPropertyChangedFor(nameof(ShowConnectionEditor))]
    [NotifyPropertyChangedFor(nameof(ShowPlaceholder))]
    private ScriptSuitePanelViewModel? _scriptPanel;

    public bool HasScriptExecution => ScriptPanel is not null;

    /// <summary>Batch script run over the tree multi-selection, shown in the
    /// editor tab in place of the connection editor while open.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBatchPanel))]
    [NotifyPropertyChangedFor(nameof(ShowConnectionEditor))]
    [NotifyPropertyChangedFor(nameof(ShowPlaceholder))]
    [NotifyPropertyChangedFor(nameof(EditorTabTitle))]
    private BatchScriptPanelViewModel? _batchPanel;

    public bool ShowBatchPanel => BatchPanel is not null;

    /// <summary>Header of the fixed first tab: "Edit" normally, "Batch run" while
    /// the batch script panel has taken the tab over.</summary>
    public string EditorTabTitle => BatchPanel is null ? L("Edit") : L("BatchTabTitle");

    // With a multi-selection the editor is hidden (it would only edit the anchor
    // node, which reads as "edits apply to all selected") and the placeholder
    // shows the selection count instead. An open batch script panel takes over
    // the editor tab entirely.
    public bool ShowConnectionEditor => Editor is not null && !HasMultiSelection && BatchPanel is null;

    public bool ShowPlaceholder => !ShowConnectionEditor && !ShowBatchPanel;

    /// <summary>Hint line of the editor-tab placeholder: the multi-selection
    /// count while one is active, otherwise the "select a connection" prompt.</summary>
    public string PlaceholderHint => HasMultiSelection
        ? L("MultiSelectionHint", _selectedNodes.Count)
        : L("SelectConnectionHint");

    // Wired up by the view so the VM can reach platform services without a
    // hard dependency on the window.
    public IClipboard? Clipboard { get; set; }
    public Func<string, string, Task<bool>>? ConfirmAsync { get; set; }
    public Func<string, string, string, Task<string?>>? PromptAsync { get; set; }
    public Func<Task<string?>>? PickKeyFileAsync { get; set; }
    public Func<StorageLocation, string?, string?, string?, bool, int, string?, TerminalAppearanceSettings, Task<SettingsDialogResult?>>? PickSettingsAsync { get; set; }
    /// <summary>Opens a folder picker. Args: suggested start path, optional dialog title.</summary>
    public Func<string, string?, Task<string?>>? PickFolderAsync { get; set; }

    /// <summary>Opens an in-app SSH terminal for the connection (set by the view).
    /// The second argument is the connection's on-disk file path, carried so the
    /// terminal tab's context menu can act on the originating tree node.</summary>
    public Func<Connection, string?, Task>? OpenSshTerminalAsync { get; set; }

    /// <summary>Like <see cref="OpenSshTerminalAsync"/>, but opens another shell
    /// session and reuses an authenticated SSH transport when one is available.</summary>
    public Func<Connection, string?, Task>? OpenNewSshSessionAsync { get; set; }

    /// <summary>Opens another terminal tab backed by a newly dialed TCP connection.</summary>
    public Func<Connection, string?, Task>? OpenNewSshTcpConnectionAsync { get; set; }

    /// <summary>Returns an SSH terminal tab for script execution, reusing an open
    /// terminal for the same connection file when possible.</summary>
    public Func<Connection, string?, Task<TerminalScriptSession?>>? EnsureSshTerminalAsync { get; set; }

    /// <summary>Like <see cref="EnsureSshTerminalAsync"/>, but does not bring the
    /// tab to the front — batch runs open many terminals while the user watches
    /// the aggregate panel.</summary>
    public Func<Connection, string?, Task<TerminalScriptSession?>>? EnsureSshTerminalQuietlyAsync { get; set; }

    /// <summary>Prompts before replacing a remembered SSH host key.</summary>
    public Func<string, int, string, string, string, bool>? ConfirmHostKeyReplacement { get; set; }

    /// <summary>Set by the view: answers keyboard-interactive prompts (OTP) for dials the
    /// view model starts itself.</summary>
    public Func<SshConnectionFactory.KeyboardInteractiveChallenge, string?>? PromptUser { get; set; }

    /// <summary>Set by the view to push a new font size to all open terminals.</summary>
    public Action<int>? ApplyTerminalFontSize { get; set; }

    /// <summary>Current terminal font size (points); the view sizes terminals with it.</summary>
    public int TerminalFontSize => _settings.Settings.TerminalFontSize;

    /// <summary>Terminal font, color scheme and scrollback as currently saved.</summary>
    public TerminalAppearanceSettings TerminalAppearance => new(
        _settings.Settings.TerminalFontFamily,
        _settings.Settings.TerminalColorScheme,
        _settings.Settings.TerminalScrollbackLines);

    /// <summary>Set by the view: pushes a terminal appearance to every open terminal.</summary>
    public Action<TerminalAppearanceSettings>? ApplyTerminalAppearance { get; set; }

    /// <summary>Persisted width of the in-terminal AI assistant panel (device-independent
    /// pixels), shared across terminal tabs and remembered across runs.</summary>
    public double AiPanelWidth
    {
        get => _settings.Settings.AiPanelWidth;
        set
        {
            var clamped = Math.Clamp(value, 240, 1200);
            if (Math.Abs(clamped - _settings.Settings.AiPanelWidth) < 0.5)
                return;

            _settings.Settings.AiPanelWidth = clamped;
            _settings.SaveIfChanged();
        }
    }

    /// <summary>Persisted height of the in-terminal SFTP file browser panel
    /// (device-independent pixels), shared across terminal tabs.</summary>
    public double FileBrowserPanelHeight
    {
        get => _settings.Settings.FileBrowserPanelHeight;
        set
        {
            var clamped = Math.Clamp(value, 120, 1600);
            if (Math.Abs(clamped - _settings.Settings.FileBrowserPanelHeight) < 0.5)
                return;

            _settings.Settings.FileBrowserPanelHeight = clamped;
            _settings.SaveIfChanged();
        }
    }

    /// <summary>Persisted width of the in-terminal server monitor panel
    /// (device-independent pixels), shared across terminal tabs.</summary>
    public double MonitorPanelWidth
    {
        get => _settings.Settings.MonitorPanelWidth;
        set
        {
            var clamped = Math.Clamp(value, 180, 600);
            if (Math.Abs(clamped - _settings.Settings.MonitorPanelWidth) < 0.5)
                return;

            _settings.Settings.MonitorPanelWidth = clamped;
            _settings.SaveIfChanged();
        }
    }

    /// <summary>Persisted width of the connection tree panel (device-independent pixels).</summary>
    public double ConnectionPanelWidth
    {
        get => _settings.Settings.ConnectionPanelWidth;
        set
        {
            var clamped = Math.Clamp(value, 180, 600);
            if (Math.Abs(clamped - _settings.Settings.ConnectionPanelWidth) < 0.5)
                return;

            _settings.Settings.ConnectionPanelWidth = clamped;
            _settings.SaveIfChanged();
        }
    }

    /// <summary>Persisted collapsed state of the connection tree panel.</summary>
    public bool ConnectionPanelCollapsed
    {
        get => _settings.Settings.ConnectionPanelCollapsed;
        set
        {
            if (_settings.Settings.ConnectionPanelCollapsed == value)
                return;

            _settings.Settings.ConnectionPanelCollapsed = value;
            _settings.SaveIfChanged();
        }
    }

    /// <summary>Editor executable for the file browser's remote editing (F4);
    /// null = system file association. Configured in the Settings dialog.</summary>
    public string? FileBrowserEditorPath => _settings.Settings.FileBrowserEditorPath;

    /// <summary>Last-used AI CLI provider label ("Claude", "Codex", "Grok"), shared across tabs.</summary>
    public string? AiProvider
    {
        get => _settings.Settings.AiProvider;
        set
        {
            _settings.Settings.AiProvider = value;
            _settings.SaveIfChanged();
        }
    }

    /// <summary>
    /// Launch mode shared by agents with a desktop surface.
    /// Terminal-only agents use <see cref="AiGrokRunMode"/> so the option sets do not collide.
    /// </summary>
    public AgentCliRunMode AiRunMode
    {
        get => _settings.Settings.AiRunMode;
        set
        {
            if (_settings.Settings.AiRunMode == value)
                return;
            _settings.Settings.AiRunMode = value;
            _settings.SaveIfChanged();
        }
    }

    /// <summary>
    /// Shared launch mode for providers with CLI / Windows Terminal only.
    /// The persisted name predates OpenCode, Pi, and OMP.
    /// </summary>
    public AgentCliRunMode AiGrokRunMode
    {
        get => _settings.Settings.AiGrokRunMode;
        set
        {
            var normalized = value == AgentCliRunMode.Desktop ? AgentCliRunMode.Cli : value;
            if (_settings.Settings.AiGrokRunMode == normalized)
                return;
            _settings.Settings.AiGrokRunMode = normalized;
            _settings.SaveIfChanged();
        }
    }

    /// <summary>Returns the stored launch mode for the given agent kind.</summary>
    public AgentCliRunMode GetAiRunModeForKind(AgentCliKind kind)
    {
        var modes = AgentCliCatalog.RunModesFor(kind);
        if (modes.Count == 1)
            return modes[0];

        return AgentCliCatalog.SupportsDesktop(kind) ? AiRunMode : AiGrokRunMode;
    }

    /// <summary>
    /// Persists the launch mode for the given agent kind into the correct settings slot. The two
    /// slots are split by whether the agent offers Desktop at all, so choosing CLI for an agent
    /// without it never clears a Desktop preference for one that has it. Agents with a single
    /// mode store nothing: their mode was never a choice, and writing it into a shared slot would
    /// erase a real preference.
    /// </summary>
    public void SetAiRunModeForKind(AgentCliKind kind, AgentCliRunMode mode)
    {
        if (AgentCliCatalog.RunModesFor(kind).Count <= 1)
            return;

        if (AgentCliCatalog.SupportsDesktop(kind))
            AiRunMode = mode;
        else
            AiGrokRunMode = mode;
    }

    /// <summary>Whether the AI panel hides the SSH terminal while open (shared across tabs).</summary>
    public bool AiHideSshTerminal
    {
        get => _settings.Settings.AiHideSshTerminal;
        set
        {
            if (_settings.Settings.AiHideSshTerminal == value)
                return;
            _settings.Settings.AiHideSshTerminal = value;
            _settings.SaveIfChanged();
        }
    }

    /// <summary>Global remembered in-terminal AI panel open state: toggling it in any
    /// SSH tab is recorded here, and new SSH sessions open the panel after login when true.</summary>
    public bool AiPanelOpen
    {
        get => _settings.Settings.AiPanelOpen;
        set
        {
            if (_settings.Settings.AiPanelOpen == value)
                return;
            _settings.Settings.AiPanelOpen = value;
            _settings.SaveIfChanged();
        }
    }

    /// <summary>Last folder chosen in the MCP write dialog. Shared by the
    /// application-wide and per-connection actions.</summary>
    public string? LastMcpProjectDirectory
    {
        get => _settings.Settings.LastMcpProjectDirectory;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (_settings.Settings.LastMcpProjectDirectory == normalized)
                return;
            _settings.Settings.LastMcpProjectDirectory = normalized;
            _settings.SaveIfChanged();
        }
    }

    /// <summary>Agent configs written by the last successful MCP dialog Write.
    /// Shared by the application-wide and per-connection dialogs.</summary>
    public IReadOnlyList<string> LastMcpWrittenTargetPaths
    {
        get => _settings.Settings.LastMcpWrittenTargetPaths ?? [];
        set
        {
            var next = (value ?? [])
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => path.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var current = _settings.Settings.LastMcpWrittenTargetPaths ?? [];
            if (current.Count == next.Count
                && current.SequenceEqual(next, StringComparer.OrdinalIgnoreCase))
            {
                return;
            }

            _settings.Settings.LastMcpWrittenTargetPaths = next;
            _settings.SaveIfChanged();
        }
    }

    /// <summary>True when a terminal tab is the active right-pane tab. Drives the
    /// visibility of the terminal font-size toolbar buttons.</summary>
    [ObservableProperty]
    private bool _isTerminalActive;

    private const int TerminalFontMin = 8;
    private const int TerminalFontMax = 36;

    [RelayCommand]
    private void IncreaseTerminalFont() => AdjustTerminalFont(+1);

    [RelayCommand]
    private void DecreaseTerminalFont() => AdjustTerminalFont(-1);

    private void AdjustTerminalFont(int delta)
    {
        var current = _settings.Settings.TerminalFontSize;
        var next = Math.Clamp(current + delta, TerminalFontMin, TerminalFontMax);
        if (next == current)
            return;

        _settings.Settings.TerminalFontSize = next;
        _settings.SaveIfChanged();
        OnPropertyChanged(nameof(TerminalFontSize));
        ApplyTerminalFontSize?.Invoke(next);
    }

    /// <summary>Asks the view to put keyboard focus on the tree so it receives shortcuts.</summary>
    public Action? RequestFocusTree { get; set; }

    /// <summary>Asks the view to put keyboard focus on a concrete tree node.</summary>
    public Action<TreeNodeViewModel>? RequestFocusTreeNode { get; set; }

    /// <summary>Asks the view to focus the inline tree name editor for a node.</summary>
    public Action<TreeNodeViewModel>? RequestFocusTreeNameEditor { get; set; }

    public string RootPath => _store.RootPath;

    /// <summary>
    /// The one store the window owns. Anything writing connection files has to go
    /// through it rather than construct its own: <see cref="ConnectionStore.KnownSignature"/>
    /// is per-instance, and it is what tells the file watcher a change was ours. A
    /// throwaway store writes without ever updating it, so the watcher treats the app's
    /// own edit as an external one and reloads the whole tree a second time.
    /// </summary>
    public ConnectionStore Store => _store;

    public BastionLoginProfileStore BastionProfiles => _store.BastionProfiles;

    public bool TryGetSavedMainWindowSize(out double width, out double height)
    {
        width = _settings.Settings.MainWindowWidth ?? 0;
        height = _settings.Settings.MainWindowHeight ?? 0;
        return IsValidWindowDimension(width) && IsValidWindowDimension(height);
    }

    public void SaveMainWindowSize(double width, double height)
    {
        if (!IsValidWindowDimension(width) || !IsValidWindowDimension(height))
            return;

        var roundedWidth = Math.Round(width);
        var roundedHeight = Math.Round(height);
        if (_settings.Settings.MainWindowWidth == roundedWidth
            && _settings.Settings.MainWindowHeight == roundedHeight)
            return;

        _settings.Settings.MainWindowWidth = roundedWidth;
        _settings.Settings.MainWindowHeight = roundedHeight;
    }

    /// <summary>Last saved main-window top-left corner, in physical pixels.</summary>
    public bool TryGetSavedMainWindowPosition(out int x, out int y)
    {
        x = _settings.Settings.MainWindowX ?? 0;
        y = _settings.Settings.MainWindowY ?? 0;
        return _settings.Settings.MainWindowX is not null
               && _settings.Settings.MainWindowY is not null;
    }

    public void SaveMainWindowPosition(int x, int y)
    {
        _settings.Settings.MainWindowX = x;
        _settings.Settings.MainWindowY = y;
    }

    /// <summary>Whether the main window should restore maximized.</summary>
    public bool MainWindowMaximized
    {
        get => _settings.Settings.MainWindowMaximized;
        set => _settings.Settings.MainWindowMaximized = value;
    }

    private static bool IsValidWindowDimension(double value) =>
        double.IsFinite(value) && value > 0;

    public string VersionDisplay
    {
        get
        {
            var version = AutoUpdateService.GetLocalCommitCount();
            return version > 0 ? L("StatusBuild", version) : L("StatusDevBuild");
        }
    }

    partial void OnSelectedNodeChanged(TreeNodeViewModel? value)
    {
        // Selecting a "Recent" connection is a one-click shortcut: launch it
        // immediately, then clear the selection. Clearing is required so a
        // subsequent click on the same entry re-fires this path (the Recent
        // group rebuild is delayed, so the same VM instance often remains).
        if (value is { IsRecent: true, IsConnection: true, Connection: not null })
        {
            // Right-click on a Recent shadow flags this so the context menu can
            // act on the selection without the one-click launch firing. The flag
            // is cleared asynchronously by the code-behind so that any re-entrant
            // SelectedNode change emitted by the TreeView during the same input
            // event is also suppressed.
            if (SuppressRecentAutoLaunch)
                return;

            var node = value;
            Dispatcher.UIThread.Post(async () =>
            {
                await LaunchAsync(node);
                if (ReferenceEquals(SelectedNode, node))
                    SelectedNode = null;
            });
            return;
        }

        // Flush any pending auto-save against the PREVIOUS editing target first,
        // before we rebind to the new node.
        FlushPendingAutoSave();

        // Stop watching the old editor.
        if (Editor != null)
            Editor.PropertyChanged -= OnEditorPropertyChanged;

        _editingNode = value is { IsConnection: true, Connection: not null } ? value : null;
        Editor = _editingNode != null
            ? ConnectionEditorViewModel.FromConnection(
                _editingNode.Connection!,
                _store.BastionProfiles)
            : null;
        _editorHasPendingChanges = false;
        _scriptContext = null;
        ScriptPanel = null;

        // Watch the new editor for changes → auto-save with debounce.
        if (Editor != null)
            Editor.PropertyChanged += OnEditorPropertyChanged;

        BrowseKeyCommand.NotifyCanExecuteChanged();
        RunSelectedScriptBindingCommand.NotifyCanExecuteChanged();
    }

    private void OnEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Computed properties don't represent user edits.
        if (e.PropertyName is nameof(ConnectionEditorViewModel.IsSsh)
                          or nameof(ConnectionEditorViewModel.IsRdp)
                          or nameof(ConnectionEditorViewModel.IsWsl)
                          or nameof(ConnectionEditorViewModel.HasHostPort)
                          or nameof(ConnectionEditorViewModel.HasPassword)
                          or nameof(ConnectionEditorViewModel.SupportsScripts)
                          or nameof(ConnectionEditorViewModel.ShowNoWslDistrosHint)
                          or nameof(ConnectionEditorViewModel.AvailableWslDistros))
        {
            RunSelectedScriptBindingCommand.NotifyCanExecuteChanged();
            return;
        }

        ScheduleAutoSave();
    }

    private void ScheduleAutoSave()
    {
        _editorHasPendingChanges = true;

        if (_autoSaveTimer == null)
        {
            _autoSaveTimer = new DispatcherTimer { Interval = AutoSaveDelay };
            _autoSaveTimer.Tick += (_, _) =>
            {
                _autoSaveTimer!.Stop();
                FlushPendingAutoSave();
            };
        }

        _autoSaveTimer.Stop();
        _autoSaveTimer.Start();
    }

    /// <summary>Forces any pending editor changes to be written to disk now.</summary>
    public void FlushAutoSave() => FlushPendingAutoSave();

    /// <summary>Writes settings.json if the in-memory settings changed since the last flush.</summary>
    public bool FlushSettings()
    {
        var saved = _settings.SaveIfChanged();
        if (!saved)
            StatusMessage = L("StatusStorageNotSaved", _settings.SettingsPath);
        return saved;
    }

    /// <summary>
    /// Persists any pending editor changes against <see cref="_editingNode"/>.
    /// Safe to call when nothing is pending. Always call before switching
    /// SelectedNode away or before performing other operations that depend on
    /// on-disk state being current.
    /// </summary>
    private void FlushPendingAutoSave()
    {
        _autoSaveTimer?.Stop();

        var node = _editingNode;
        var editor = Editor;
        if (node?.Connection is null || editor is null)
        {
            _editorHasPendingChanges = false;
            return;
        }

        if (!_editorHasPendingChanges)
            return;

        try
        {
            editor.ApplyTo(node.Connection);
            ProtectConnectionScriptBindings(node.Connection);

            var folder = Path.GetDirectoryName(node.FullPath) ?? _store.RootPath;
            var newPath = _store.Save(node.Connection, folder, node.FullPath);

            if (!PathEquals(newPath, node.FullPath))
            {
                // The file was renamed because Name changed.
                var clipboardIndex = _clipboardEntries.FindIndex(entry => PathEquals(entry.Path, node.FullPath));
                if (clipboardIndex >= 0)
                    _clipboardEntries[clipboardIndex] = _clipboardEntries[clipboardIndex] with { Path = newPath };
                node.FullPath = newPath;
                node.Name = node.Connection.Name;
            }

            StatusMessage = L("StatusAutoSaved", node.Name);
            _editorHasPendingChanges = false;
        }
        catch (Exception ex)
        {
            StatusMessage = L("StatusAutoSaveFailed", ex.Message);
        }
    }

}
