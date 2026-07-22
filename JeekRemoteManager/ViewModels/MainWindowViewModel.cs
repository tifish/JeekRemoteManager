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
    private const long SelfWriteSuppressMs = 1000;

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

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConnectNewCommand))]
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
        ConnectNewCommand.NotifyCanExecuteChanged();
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
    public Func<StorageLocation, string?, string?, string?, bool, int, string?, Task<SettingsDialogResult?>>? PickSettingsAsync { get; set; }
    /// <summary>Opens a folder picker. Args: suggested start path, optional dialog title.</summary>
    public Func<string, string?, Task<string?>>? PickFolderAsync { get; set; }

    /// <summary>Opens an in-app SSH terminal for the connection (set by the view).
    /// The second argument is the connection's on-disk file path, carried so the
    /// terminal tab's context menu can act on the originating tree node.</summary>
    public Func<Connection, string?, Task>? OpenSshTerminalAsync { get; set; }

    /// <summary>Like <see cref="OpenSshTerminalAsync"/>, but always opens a fresh
    /// terminal tab instead of activating an existing one for the same connection.</summary>
    public Func<Connection, string?, Task>? OpenNewSshTerminalAsync { get; set; }

    /// <summary>Returns an SSH terminal tab for script execution, reusing an open
    /// terminal for the same connection file when possible.</summary>
    public Func<Connection, string?, Task<TerminalScriptSession?>>? EnsureSshTerminalAsync { get; set; }

    /// <summary>Like <see cref="EnsureSshTerminalAsync"/>, but does not bring the
    /// tab to the front — batch runs open many terminals while the user watches
    /// the aggregate panel.</summary>
    public Func<Connection, string?, Task<TerminalScriptSession?>>? EnsureSshTerminalQuietlyAsync { get; set; }

    /// <summary>Prompts the user to trust a first-seen SSH host key (set by the view).
    /// (host, port, keyType, sha256Fingerprint) =&gt; trust?. Blocks the calling thread,
    /// so it must be invoked off the UI thread (the SSH handshake runs in the background).</summary>
    public Func<string, int, string, string, bool>? ConfirmHostKeyTrust { get; set; }

    /// <summary>Set by the view to push a new font size to all open terminals.</summary>
    public Action<int>? ApplyTerminalFontSize { get; set; }

    /// <summary>Current terminal font size (points); the view sizes terminals with it.</summary>
    public int TerminalFontSize => _settings.Settings.TerminalFontSize;

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
    /// Launch mode for Claude/Codex (CLI / Windows Terminal / Desktop).
    /// Grok uses <see cref="AiGrokRunMode"/> so the two option sets do not overwrite each other.
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
    /// Launch mode for Grok (CLI / Windows Terminal only).
    /// Kept separate from <see cref="AiRunMode"/> because Grok has no Desktop option.
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
    public AgentCliRunMode GetAiRunModeForKind(AgentCliKind kind) =>
        kind == AgentCliKind.Grok ? AiGrokRunMode : AiRunMode;

    /// <summary>Persists the launch mode for the given agent kind into the correct settings slot.</summary>
    public void SetAiRunModeForKind(AgentCliKind kind, AgentCliRunMode mode)
    {
        if (kind == AgentCliKind.Grok)
            AiGrokRunMode = mode;
        else
            AiRunMode = mode;
    }

    /// <summary>Whether the selected agent CLI may invoke JRM remote command tools without
    /// its own per-call permission prompt.</summary>
    public bool AiAutoRun
    {
        get => _settings.Settings.AiAutoRun;
        set
        {
            if (_settings.Settings.AiAutoRun == value)
                return;
            _settings.Settings.AiAutoRun = value;
            _settings.SaveIfChanged();
        }
    }

    /// <summary>Whether JRM skips its additional confirmation for destructive remote commands.</summary>
    public bool AiAutoApproveDangerousCommands
    {
        get => _settings.Settings.AiAutoApproveDangerousCommands;
        set
        {
            if (_settings.Settings.AiAutoApproveDangerousCommands == value)
                return;
            _settings.Settings.AiAutoApproveDangerousCommands = value;
            _settings.SaveIfChanged();
        }
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

    /// <summary>Global remembered AI panel open state: toggling it in any tab is
    /// recorded here, and new SSH tabs open the panel automatically when true.</summary>
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
            ? ConnectionEditorViewModel.FromConnection(_editingNode.Connection!)
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

    // --- Watching the connections folder ---

    /// <summary>(Re)starts the file-system watcher for the active storage mode.</summary>
    private void StartWatchingCurrentStorage()
    {
        var watchPortableConfig = _settings.CurrentStorageLocation == StorageLocation.ProgramDirectory;
        var path = watchPortableConfig ? _settings.ResolveConfigRoot() : _store.RootPath;
        StartWatching(path, watchPortableConfig);
    }

    private void StartWatching(string path, bool watchPortableConfig)
    {
        StopWatching();
        _pendingWatchedPaths.Clear();

        if (!Directory.Exists(path))
            return;

        try
        {
            _watchingPortableConfig = watchPortableConfig;
            _watcher = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName
                             | NotifyFilters.DirectoryName
                             | NotifyFilters.LastWrite
                             | NotifyFilters.Size,
            };
            _watcher.Created += OnWatchedChange;
            _watcher.Deleted += OnWatchedChange;
            _watcher.Renamed += OnWatchedChange;
            _watcher.Changed += OnWatchedChange;
            _watcher.EnableRaisingEvents = true;
        }
        catch
        {
            // If watching can't be set up (e.g. unsupported path), the app still
            // works; it just won't pick up external changes automatically.
            _watcher = null;
        }
    }

    private void StopWatching()
    {
        if (_watcher is null)
            return;

        _watcher.EnableRaisingEvents = false;
        _watcher.Created -= OnWatchedChange;
        _watcher.Deleted -= OnWatchedChange;
        _watcher.Renamed -= OnWatchedChange;
        _watcher.Changed -= OnWatchedChange;
        _watcher.Dispose();
        _watcher = null;
        _pendingWatchedPaths.Clear();
        _watchingPortableConfig = false;
    }

    // Events arrive on a background thread; hop to the UI thread and debounce.
    private void OnWatchedChange(object? sender, FileSystemEventArgs e)
    {
        if (IsOwnWriteRecent())
            return;

        var changedPath = e.FullPath;
        var oldPath = e is RenamedEventArgs renamed ? renamed.OldFullPath : null;

        Dispatcher.UIThread.Post(() => ScheduleWatchReload(changedPath, oldPath));
    }

    private bool IsOwnWriteRecent()
    {
        var lastWrite = Math.Max(_store.LastWriteTick, _settings.LastWriteTick);
        return lastWrite > 0 && Environment.TickCount64 - lastWrite < SelfWriteSuppressMs;
    }

    private void ScheduleWatchReload(string changedPath, string? oldPath = null)
    {
        if (_watchingPortableConfig)
        {
            AddPendingWatchedPath(changedPath);
            if (!string.IsNullOrWhiteSpace(oldPath))
                AddPendingWatchedPath(oldPath);
        }

        if (_watchReloadTimer is null)
        {
            _watchReloadTimer = new DispatcherTimer();
            _watchReloadTimer.Tick += (_, _) =>
            {
                _watchReloadTimer!.Stop();
                var changedPaths = _pendingWatchedPaths.ToList();
                _pendingWatchedPaths.Clear();

                ReloadWatchedData(changedPaths);
            };
        }

        _watchReloadTimer.Interval = _watchingPortableConfig
            ? PortableConfigReloadDelay
            : ConnectionWatchReloadDelay;
        _watchReloadTimer.Stop();
        _watchReloadTimer.Start();
    }

    private void AddPendingWatchedPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            _pendingWatchedPaths.Add(Path.GetFullPath(path));
        }
        catch
        {
            _pendingWatchedPaths.Add(path);
        }
    }

    private void ReloadWatchedData(IReadOnlyCollection<string> changedPaths)
    {
        if (!_watchingPortableConfig)
        {
            // Don't clobber an in-progress edit; flush it first so the reload
            // reflects the user's latest changes too.
            FlushPendingAutoSave();
            ReloadTree();
            return;
        }

        var changes = ClassifyPortableConfigChanges(changedPaths);
        if (!changes.HasAnyChange)
            return;

        if (changes.SettingsChanged)
        {
            var previousInterval = _settings.Settings.UpdateCheckIntervalHours;
            _settings.ReloadRoamingSettings();
            ApplyLanguage(_settings.Settings.Language);
            ApplyTheme(_settings.Settings.Theme);
            if (previousInterval != _settings.Settings.UpdateCheckIntervalHours)
                _updateIntervalChanged.Cancel();
        }

        if (changes.ScriptsChanged)
            ReloadScripts();

        if (changes.ConnectionsChanged)
        {
            FlushPendingAutoSave();
            ClearClipboard();
            ReloadTree(_settings.Settings.LastSelectedConnectionPath);
            OnPropertyChanged(nameof(RootPath));
            OnPropertyChanged(nameof(TargetDescription));
        }
    }

    public static PortableConfigChangeSet ClassifyPortableConfigChanges(IEnumerable<string> changedPaths)
    {
        var settingsPath = SettingsService.ResolveSettingsPath(StorageLocation.ProgramDirectory);
        var connectionsRoot = SettingsService.ResolveConnectionsRoot(StorageLocation.ProgramDirectory);
        var scriptsRoot = SettingsService.ResolveScriptsRoot(StorageLocation.ProgramDirectory);

        var result = new PortableConfigChangeSet();
        foreach (var path in changedPaths)
        {
            if (PathEquals(path, settingsPath))
            {
                result.SettingsChanged = true;
            }
            else if (ConnectionStore.IsSameOrInside(connectionsRoot, path))
            {
                result.ConnectionsChanged = true;
            }
            else if (ConnectionStore.IsSameOrInside(scriptsRoot, path))
            {
                result.ScriptsChanged = true;
            }
        }

        return result;
    }

    public sealed class PortableConfigChangeSet
    {
        public bool SettingsChanged { get; set; }

        public bool ConnectionsChanged { get; set; }

        public bool ScriptsChanged { get; set; }

        public bool HasAnyChange => SettingsChanged || ConnectionsChanged || ScriptsChanged;
    }

    // --- Tree building ---

    private void ReloadTree(string? pathToSelect = null, bool requestFocus = true)
    {
        // Folder expand/collapse state is persisted in AppSettings.CollapsedFolderPaths
        // and applied as each folder node is built, so it survives both in-session
        // rebuilds and restarts. Drop stale entries for folders that no longer exist.
        PruneMissingCollapsedFolders();
        var previousSelection = SelectedNode?.FullPath;

        // Full rebuild already reflects the current Recent paths; drop any pending
        // delayed refresh so it does not fire against a replaced tree.
        _recentRebuildTimer?.Stop();

        Nodes.Clear();
        _recentGroup = null;

        var recentGroup = BuildRecentGroup();
        if (recentGroup != null)
        {
            _recentGroup = recentGroup;
            Nodes.Add(recentGroup);
        }

        foreach (var child in BuildChildren(_store.RootPath, parent: null))
            Nodes.Add(child);

        // Re-apply the "cut" dimming to the source nodes so it survives reloads.
        if (_clipboardIsCut)
        {
            foreach (var entry in _clipboardEntries)
            {
                var cutNode = FindNode(Nodes, entry.Path);
                if (cutNode != null)
                    cutNode.IsCut = true;
            }
        }

        var selectPath = pathToSelect ?? previousSelection;
        if (selectPath != null)
        {
            var node = FindNode(Nodes, selectPath);
            if (node != null)
            {
                ExpandAncestors(node); // reveal it
                SelectedNode = node;

                // When ReloadTree was triggered by a user action (paste, new, rename,
                // refresh), put keyboard focus on the tree so the new item is ready
                // to receive Enter/F2/Delete/Ctrl+C etc.
                if (pathToSelect != null && requestFocus)
                    RequestTreeFocus(node);
            }
            else
            {
                SelectedNode = null;
            }
        }
    }

    private void ReloadScripts()
    {
        ScriptSuites.Clear();
        foreach (var suite in _scriptStore.LoadAll())
            ScriptSuites.Add(suite);
    }

    private RemoteScriptSuite? FindScriptSuite(string suitePath) =>
        ScriptSuites.FirstOrDefault(s => string.Equals(s.RelativePath, suitePath, StringComparison.OrdinalIgnoreCase));

    private void ProtectConnectionScriptBindings(Connection connection)
    {
        for (var i = 0; i < connection.ScriptBindings.Count; i++)
        {
            var binding = connection.ScriptBindings[i];
            binding.Name = RemoteScriptSuiteNames.NormalizeBindingName(binding.Name);
            var suite = FindScriptSuite(binding.Name);
            if (suite is not null)
                connection.ScriptBindings[i] = RemoteScriptLauncher.ProtectSecretValues(suite, binding);
        }
    }

    public static int PruneMissingScriptBindings(
        IList<ConnectionScriptBinding> bindings,
        IEnumerable<RemoteScriptSuite> suites)
    {
        var validSuites = suites
            .Select(s => s.RelativePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var removed = 0;
        for (var i = bindings.Count - 1; i >= 0; i--)
        {
            bindings[i].Name = RemoteScriptSuiteNames.NormalizeBindingName(bindings[i].Name);
            if (!validSuites.Contains(bindings[i].Name))
            {
                bindings.RemoveAt(i);
                removed++;
            }
        }

        return removed;
    }

    private int PruneMissingScriptBindingsForCurrentConnection(Connection connection)
    {
        var removed = PruneMissingScriptBindings(connection.ScriptBindings, ScriptSuites);
        if (Editor is not null)
            PruneMissingScriptBindingViewModels(Editor.ScriptBindings, ScriptSuites);

        if (removed > 0)
        {
            if (ScriptPanel is not null
                && FindScriptSuite(ScriptPanel.Suite.RelativePath) is null
                && !ScriptPanel.IsRunning)
            {
                ScriptPanel = null;
            }

            ScheduleAutoSave();
            RunSelectedScriptBindingCommand.NotifyCanExecuteChanged();
        }

        return removed;
    }

    private static int PruneMissingScriptBindingViewModels(
        ObservableCollection<ConnectionScriptBindingViewModel> bindings,
        IEnumerable<RemoteScriptSuite> suites)
    {
        var validSuites = suites
            .Select(s => s.RelativePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var removed = 0;
        for (var i = bindings.Count - 1; i >= 0; i--)
        {
            bindings[i].Name = RemoteScriptSuiteNames.NormalizeBindingName(bindings[i].Name);
            if (!validSuites.Contains(bindings[i].Name))
            {
                bindings.RemoveAt(i);
                removed++;
            }
        }

        return removed;
    }

    /// <summary>
    /// Builds the synthetic "Recent" group from <see cref="AppSettings.RecentConnectionPaths"/>,
    /// pruning entries whose files no longer exist. Returns null when no usable
    /// entries remain (so the group doesn't appear empty).
    /// </summary>
    private TreeNodeViewModel? BuildRecentGroup()
    {
        var paths = _settings.Settings.RecentConnectionPaths;
        if (paths.Count == 0)
            return null;

        var children = new List<TreeNodeViewModel>();

        foreach (var path in paths.ToArray())
        {
            if (!File.Exists(path))
            {
                paths.RemoveAll(p => PathEquals(p, path));
                continue;
            }

            Connection connection;
            try
            {
                connection = _store.Load(path);
            }
            catch
            {
                paths.RemoveAll(p => PathEquals(p, path));
                continue;
            }

            children.Add(new TreeNodeViewModel(path, isFolder: false, connection)
            {
                IsRecent = true,
            });
        }

        if (children.Count == 0)
            return null;

        var group = new TreeNodeViewModel(RecentSentinelPath, isFolder: true)
        {
            IsRecent = true,
            Name = L("RecentGroup"),
        };
        group.IsExpanded = _settings.Settings.RecentExpanded;
        foreach (var c in children)
        {
            c.Parent = group;
            group.Children.Add(c);
        }

        // Persist the user's open/closed preference for the group.
        group.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(TreeNodeViewModel.IsExpanded))
                return;
            if (_settings.Settings.RecentExpanded == group.IsExpanded)
                return;
            _settings.Settings.RecentExpanded = group.IsExpanded;
        };

        return group;
    }

    private ObservableCollection<TreeNodeViewModel> BuildChildren(string folderPath, TreeNodeViewModel? parent)
    {
        var result = new ObservableCollection<TreeNodeViewModel>();

        foreach (var dir in _store.GetSubFolders(folderPath))
        {
            var node = new TreeNodeViewModel(dir, isFolder: true) { Parent = parent };
            foreach (var child in BuildChildren(dir, node))
                node.Children.Add(child);
            ApplyPersistedExpansion(node);
            result.Add(node);
        }

        foreach (var file in _store.GetConnectionFiles(folderPath))
        {
            Connection connection;
            try
            {
                connection = _store.Load(file);
            }
            catch
            {
                continue; // skip unreadable files
            }

            result.Add(new TreeNodeViewModel(file, isFolder: false, connection) { Parent = parent });
        }

        return result;
    }

    /// <summary>
    /// Applies the persisted expand/collapse state to a freshly-built folder node
    /// and subscribes so later toggles are written back to settings. Folders absent
    /// from <see cref="AppSettings.CollapsedFolderPaths"/> default to expanded.
    /// </summary>
    private void ApplyPersistedExpansion(TreeNodeViewModel node)
    {
        // Set the initial state BEFORE subscribing so this seeding doesn't get
        // mistaken for a user toggle and re-saved.
        node.IsExpanded = !IsFolderCollapsed(node.FullPath);
        node.PropertyChanged += OnFolderExpandedChanged;
    }

    private void OnFolderExpandedChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TreeNodeViewModel.IsExpanded))
            return;
        if (sender is not TreeNodeViewModel { IsFolder: true, IsRecent: false } node)
            return;
        UpdateFolderExpansionState(node);
    }

    private bool IsFolderCollapsed(string fullPath) =>
        _settings.Settings.CollapsedFolderPaths.Exists(p => PathEquals(p, fullPath));

    /// <summary>
    /// Records a toggled folder's expand/collapse state in settings when it changed.
    /// </summary>
    private void UpdateFolderExpansionState(TreeNodeViewModel node)
    {
        var collapsed = _settings.Settings.CollapsedFolderPaths;
        if (node.IsExpanded)
        {
            collapsed.RemoveAll(p => PathEquals(p, node.FullPath));
        }
        else if (!collapsed.Exists(p => PathEquals(p, node.FullPath)))
        {
            collapsed.Add(Path.GetFullPath(node.FullPath));
        }
    }

    /// <summary>Removes collapsed-folder entries whose directories no longer exist,
    /// so renamed/deleted folders don't accumulate stale state.</summary>
    private void PruneMissingCollapsedFolders()
    {
        var collapsed = _settings.Settings.CollapsedFolderPaths;
        if (collapsed.Count == 0)
            return;

        collapsed.RemoveAll(p =>
        {
            try { return !Directory.Exists(p); }
            catch { return true; }
        });
    }

    public void SaveLastSelectedConnection()
    {
        FlushPendingAutoSave();
        SaveLastSelectedConnectionPath(SelectedNode);
    }

    private static void ExpandAncestors(TreeNodeViewModel node)
    {
        for (var p = node.Parent; p != null; p = p.Parent)
            p.IsExpanded = true;
    }

    private static TreeNodeViewModel? FindNode(IEnumerable<TreeNodeViewModel> nodes, string fullPath)
    {
        foreach (var node in nodes)
        {
            if (node.IsRecent) continue; // shadow entries; never the canonical hit
            if (PathEquals(node.FullPath, fullPath))
                return node;

            var found = FindNode(node.Children, fullPath);
            if (found != null)
                return found;
        }

        return null;
    }

    private static bool PathEquals(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private void SaveLastSelectedConnectionPath(TreeNodeViewModel? node)
    {
        var path = node is { IsRecent: false, IsConnection: true } ? node.FullPath : null;
        if (NullablePathEquals(_settings.Settings.LastSelectedConnectionPath, path))
            return;

        _settings.Settings.LastSelectedConnectionPath = path;
    }

    private static bool NullablePathEquals(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
            return string.IsNullOrWhiteSpace(a) && string.IsNullOrWhiteSpace(b);

        return PathEquals(a, b);
    }

    private void DetachEditorIfEditingPath(string path)
    {
        if (_editingNode == null || !ConnectionStore.IsSameOrInside(path, _editingNode.FullPath))
            return;

        _autoSaveTimer?.Stop();
        if (Editor != null)
            Editor.PropertyChanged -= OnEditorPropertyChanged;
        _editingNode = null;
        Editor = null;
        _editorHasPendingChanges = false;
    }

    /// <summary>Folder that new/pasted items should go into, based on the selection.</summary>
    private string TargetFolder()
    {
        // The "Recent" group is a synthetic shadow and has no on-disk folder; fall
        // back to the root so new/paste don't try to write into a sentinel path.
        if (SelectedNode is null || SelectedNode.IsRecent)
            return _store.RootPath;

        return SelectedNode.IsFolder
            ? SelectedNode.FullPath
            : Path.GetDirectoryName(SelectedNode.FullPath) ?? _store.RootPath;
    }

    // --- Create commands ---

    [RelayCommand]
    private void NewFolder()
    {
        try
        {
            var parent = TargetFolder();
            var path = _store.CreateFolder(parent, L("NewFolderDefault"));

            // Reload with the new folder as the reveal target so the entry is
            // visible before switching it into inline rename mode.
            ReloadTree(path, requestFocus: false);

            var newFolder = FindNode(Nodes, path);
            if (newFolder is not null)
            {
                SelectedNode = newFolder;
                BeginNodeNameEdit(newFolder);
            }
            else
            {
                RequestTreeFocus(SelectedNode);
            }

            StatusMessage = L("StatusCreatedFolder", Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            StatusMessage = L("StatusCouldNotCreateFolder", ex.Message);
        }
    }

    [RelayCommand]
    private void NewSsh() => CreateConnection(ConnectionType.Ssh);

    [RelayCommand]
    private void NewRdp() => CreateConnection(ConnectionType.Rdp);

    [RelayCommand]
    private void NewWsl() => CreateConnection(ConnectionType.Wsl);

    private void CreateConnection(ConnectionType type)
    {
        try
        {
            var connection = new Connection
            {
                Type = type,
                Name = type switch
                {
                    ConnectionType.Rdp => L("NewRdpDefault"),
                    ConnectionType.Wsl => L("NewWslDefault"),
                    _ => L("NewSshDefault"),
                },
                Port = Connection.DefaultPort(type),
            };

            // Preselect the default distro so a new WSL connection works unedited.
            if (type == ConnectionType.Wsl)
                connection.WslDistro = WslDistroService.ListDistros().FirstOrDefault(d => d.IsDefault)?.Name ?? "";

            var path = _store.Save(connection, TargetFolder());
            ReloadTree(path);
            StatusMessage = L("StatusCreatedConnection", type.ToDisplayName());
        }
        catch (Exception ex)
        {
            StatusMessage = L("StatusCouldNotCreateConnection", ex.Message);
        }
    }

    // --- Edit / connect ---

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task Connect()
    {
        // Make sure unsaved edits land on disk before we read the connections.
        FlushPendingAutoSave();

        // With a multi-selection every selected connection launches; folders in
        // the selection are simply skipped.
        var targets = EffectiveSelection()
            .Where(n => n is { IsConnection: true, IsNameEditing: false, Connection: not null })
            .ToList();
        if (targets.Count == 0)
            return;

        var launchedRecentShadow = targets.Any(n => n.IsRecent);
        foreach (var node in targets)
            await LaunchAsync(node);

        if (launchedRecentShadow && SelectedNode is { IsRecent: true })
            SelectedNode = null;
    }

    private bool CanConnect() =>
        EffectiveSelection().Any(n => n is { IsConnection: true, IsNameEditing: false });

    /// <summary>Opens a fresh terminal session even when one is already open for
    /// the connection (the plain Connect command activates the existing tab).</summary>
    [RelayCommand(CanExecute = nameof(CanConnectNew))]
    private async Task ConnectNew()
    {
        FlushPendingAutoSave();

        if (SelectedNode is not { IsConnection: true, IsNameEditing: false, Connection: not null } node)
            return;

        var clearStaleRecentSelection = node.IsRecent;
        await LaunchAsync(node, forceNew: true);

        if (clearStaleRecentSelection && ReferenceEquals(SelectedNode, node))
            SelectedNode = null;
    }

    private bool CanConnectNew() =>
        SelectedNode is
        {
            IsConnection: true,
            IsNameEditing: false,
            Connection.Type: ConnectionType.Ssh or ConnectionType.Wsl,
        };

    // --- Recent group: reveal / remove / clear ---

    [RelayCommand(CanExecute = nameof(IsRecentConnectionContextMethod))]
    private void RevealInTree()
    {
        if (SelectedNode is not { IsRecent: true, IsConnection: true } shadow)
            return;

        var real = FindNode(Nodes, shadow.FullPath);
        if (real is null)
        {
            StatusMessage = L("StatusRevealMissing");
            return;
        }

        ExpandAncestors(real);
        SelectedNode = real;
        RequestTreeFocus(real);
    }

    [RelayCommand(CanExecute = nameof(IsRecentConnectionContextMethod))]
    private void RemoveFromRecent()
    {
        var shadows = EffectiveSelection()
            .Where(n => n is { IsRecent: true, IsConnection: true })
            .ToList();
        if (shadows.Count == 0)
            return;

        var list = _settings.Settings.RecentConnectionPaths;
        var before = list.Count;
        foreach (var shadow in shadows)
            list.RemoveAll(p => PathEquals(p, shadow.FullPath));
        if (list.Count == before)
            return;

        RebuildRecentGroupInPlace();
        SelectedNode = null;
        StatusMessage = shadows.Count == 1
            ? L("StatusRemovedFromRecent", shadows[0].Name)
            : L("StatusRemovedFromRecentMultiple", before - list.Count);
    }

    [RelayCommand]
    private void ClearRecent()
    {
        var list = _settings.Settings.RecentConnectionPaths;
        if (list.Count == 0)
            return;

        list.Clear();
        RebuildRecentGroupInPlace();
        if (SelectedNode is { IsRecent: true })
            SelectedNode = null;
        StatusMessage = L("StatusRecentCleared");
    }

    private bool IsRecentConnectionContextMethod() => IsRecentConnectionContext;

    /// <summary>
    /// Rebuilds the synthetic "Recent" group node from the current path list and
    /// swaps it into <see cref="Nodes"/> without touching the rest of the tree.
    /// Cancels any pending delayed rebuild so callers that need an immediate
    /// refresh (remove/clear/full reload) win over a later timer tick.
    /// </summary>
    private void RebuildRecentGroupInPlace()
    {
        _recentRebuildTimer?.Stop();

        var newGroup = BuildRecentGroup();
        if (_recentGroup != null)
        {
            var oldIndex = Nodes.IndexOf(_recentGroup);
            if (oldIndex >= 0)
                Nodes.RemoveAt(oldIndex);
        }
        _recentGroup = newGroup;
        if (newGroup != null)
            Nodes.Insert(0, newGroup);
    }

    /// <summary>
    /// Launches the given connection and records it at the head of the recent list.
    /// Shared by the Connect command (on real nodes) and the "Recent" group's
    /// one-click shortcut (on shadow nodes).
    /// </summary>
    private async Task LaunchAsync(TreeNodeViewModel node, bool forceNew = false)
    {
        if (node.Connection is null)
            return;

        var connection = node.Connection;

        try
        {
            // SSH and WSL always render in the in-app terminal (SSH.NET shell or
            // ConPTY) — there is no external-client path for them.
            if (connection.Type is ConnectionType.Ssh or ConnectionType.Wsl)
            {
                var open = forceNew ? OpenNewSshTerminalAsync : OpenSshTerminalAsync;
                if (open is null)
                    throw new InvalidOperationException("The in-app terminal is not available.");
                StatusMessage = L("StatusLaunching", connection.Type.ToDisplayName(), connection.TargetLabel);
                await open(connection, node.FullPath);
                RecordRecent(node.FullPath);
                return;
            }

            // RDP launches the OS client (mstsc).
            StatusMessage = L("StatusLaunching", connection.Type.ToDisplayName(), connection.Host);
            _launcher.Launch(connection);
            RecordRecent(node.FullPath);
        }
        catch (Exception ex)
        {
            StatusMessage = L("StatusFailedToLaunch", ex.Message);
        }
    }

    /// <summary>
    /// Moves <paramref name="path"/> to the front of the most-recently-used list
    /// and trims to <see cref="RecentMax"/>. The settings list is updated
    /// immediately; the synthetic Recent tree group is rebuilt after a short
    /// delay so clicking a Recent item does not jump under the cursor.
    /// </summary>
    private void RecordRecent(string path)
    {
        var list = _settings.Settings.RecentConnectionPaths;
        var existingIndex = -1;
        for (var i = 0; i < list.Count; i++)
        {
            if (!PathEquals(list[i], path))
                continue;
            existingIndex = i;
            break;
        }

        // Already most-recent: nothing to record or show.
        if (existingIndex == 0)
            return;

        if (existingIndex >= 0)
            list.RemoveAt(existingIndex);
        else
            list.RemoveAll(p => PathEquals(p, path));

        list.Insert(0, path);
        if (list.Count > RecentMax)
            list.RemoveRange(RecentMax, list.Count - RecentMax);

        ScheduleRecentGroupRebuild();
    }

    /// <summary>
    /// Debounces the Recent-group tree rebuild so rapid connects only refresh once,
    /// after the user has finished clicking.
    /// </summary>
    private void ScheduleRecentGroupRebuild()
    {
        if (_recentRebuildTimer is null)
        {
            _recentRebuildTimer = new DispatcherTimer();
            _recentRebuildTimer.Tick += (_, _) =>
            {
                _recentRebuildTimer!.Stop();
                RebuildRecentGroupInPlace();
            };
        }

        _recentRebuildTimer.Interval = RecentRebuildDelay;
        _recentRebuildTimer.Stop();
        _recentRebuildTimer.Start();
    }

    /// <summary>
    /// Applies any pending Recent-group rebuild immediately. Used by tests and by
    /// callers that need the tree to match settings without waiting for the delay.
    /// </summary>
    public void FlushPendingRecentRebuild()
    {
        if (_recentRebuildTimer is not { IsEnabled: true })
            return;

        _recentRebuildTimer.Stop();
        RebuildRecentGroupInPlace();
    }

    // --- Delete / rename ---

    [RelayCommand(CanExecute = nameof(CanModifySelection))]
    private async Task Delete()
    {
        // Drop any pending auto-save for what's about to be deleted.
        _autoSaveTimer?.Stop();

        var targets = RemoveNestedNodes(EffectiveSelection()
            .Where(n => n is { IsRecent: false, IsNameEditing: false })
            .ToList());
        if (targets.Count == 0)
            return;

        var what = targets.Count > 1
            ? L("DialogDeleteMultiplePrompt", targets.Count)
            : targets[0].IsFolder
                ? L("DialogDeleteFolderPrompt", targets[0].Name)
                : L("DialogDeleteConnectionPrompt", targets[0].Name);
        if (ConfirmAsync is not null)
        {
            var ok = await ConfirmAsync(L("DialogDeleteTitle"), what);
            if (!ok)
                return;
        }

        // Pick the next selection from the anchor's siblings, skipping anything
        // that is itself doomed (selected or inside a selected folder).
        var anchor = SelectedNode is { } selected && targets.Contains(selected) ? selected : targets[0];
        bool IsDoomed(TreeNodeViewModel candidate) => targets.Any(t =>
            ConnectionStore.IsSameOrInside(t.FullPath, candidate.FullPath));
        IList<TreeNodeViewModel> siblings = anchor.Parent is not null
            ? anchor.Parent.Children
            : Nodes.Where(candidate => !candidate.IsRecent).ToList();
        var anchorIndex = siblings.IndexOf(anchor);
        string? nextSelectionPath = null;
        for (var i = anchorIndex + 1; i < siblings.Count && nextSelectionPath is null; i++)
        {
            if (!IsDoomed(siblings[i]))
                nextSelectionPath = siblings[i].FullPath;
        }
        for (var i = anchorIndex - 1; i >= 0 && nextSelectionPath is null; i--)
        {
            if (!IsDoomed(siblings[i]))
                nextSelectionPath = siblings[i].FullPath;
        }
        nextSelectionPath ??= Path.GetDirectoryName(anchor.FullPath);

        var deletedCount = 0;
        string? firstError = null;
        foreach (var node in targets)
        {
            var deletedPath = node.FullPath;

            // Drop the editor binding to the doomed node BEFORE we touch disk or
            // reload the tree — otherwise the selection change triggered by the
            // reload would flush a stale auto-save and resurrect the deleted file.
            if (_editingNode != null &&
                ConnectionStore.IsSameOrInside(deletedPath, _editingNode.FullPath))
            {
                if (Editor != null)
                    Editor.PropertyChanged -= OnEditorPropertyChanged;
                _editingNode = null;
                Editor = null;
            }

            try
            {
                if (node.IsFolder)
                    _store.DeleteFolder(deletedPath);
                else
                    _store.DeleteFile(deletedPath);
                deletedCount++;
            }
            catch (Exception ex)
            {
                firstError ??= ex.Message;
                continue;
            }

            // Drop pending clipboard entries that pointed at the deleted item.
            RemoveClipboardEntriesUnder(deletedPath);
        }

        if (deletedCount == 0)
        {
            StatusMessage = L("StatusCouldNotDelete", firstError ?? "");
            return;
        }

        ReloadTree(nextSelectionPath);
        StatusMessage = firstError is not null
            ? L("StatusCouldNotDelete", firstError)
            : deletedCount == 1
                ? L("StatusDeleted", targets[0].Name)
                : L("StatusDeletedMultiple", deletedCount);
    }

    [RelayCommand(CanExecute = nameof(CanRenameSelection))]
    private void Rename()
    {
        if (SelectedNode is not { } node)
            return;

        BeginNodeNameEdit(node);
    }

    // Renaming only makes sense for a single node.
    private bool CanRenameSelection() => !HasMultiSelection && CanModifySelection();

    private void BeginNodeNameEdit(TreeNodeViewModel node)
    {
        if (node.IsRecent)
            return;

        FlushPendingAutoSave();

        if (_renamingNode is not null && !ReferenceEquals(_renamingNode, node))
            CancelNodeNameEdit(_renamingNode, requestFocus: false);

        _renamingNode = node;
        node.EditName = node.Name;
        node.IsNameEditing = true;
        NotifyTreeActionCanExecuteChanged();
        RequestFocusTreeNameEditor?.Invoke(node);
    }

    public void CommitNodeNameEdit(TreeNodeViewModel node, bool requestFocus = true)
    {
        if (!node.IsNameEditing)
            return;

        node.IsNameEditing = false;
        if (ReferenceEquals(_renamingNode, node))
            _renamingNode = null;
        NotifyTreeActionCanExecuteChanged();

        var newName = node.EditName.Trim();
        if (string.IsNullOrWhiteSpace(newName) || newName == node.Name)
        {
            node.EditName = node.Name;
            if (requestFocus)
                RequestTreeFocus(node);
            return;
        }

        var oldPath = node.FullPath;

        try
        {
            var newPath = RenameNode(node, newName);

            DetachEditorIfEditingPath(oldPath);
            ReloadTree(newPath, requestFocus);
            StatusMessage = L("StatusRenamed");
        }
        catch (Exception ex)
        {
            StatusMessage = L("StatusCouldNotRename", ex.Message);
            _renamingNode = node;
            node.IsNameEditing = true;
            NotifyTreeActionCanExecuteChanged();
            RequestFocusTreeNameEditor?.Invoke(node);
        }
    }

    public void CancelNodeNameEdit(TreeNodeViewModel node, bool requestFocus = true)
    {
        node.EditName = node.Name;
        node.IsNameEditing = false;
        if (ReferenceEquals(_renamingNode, node))
            _renamingNode = null;
        NotifyTreeActionCanExecuteChanged();

        if (requestFocus)
            RequestTreeFocus(node);
    }

    private string RenameNode(TreeNodeViewModel node, string newName)
    {
        if (node.IsFolder)
            return _store.RenameFolder(node.FullPath, newName);

        if (node.Connection is null)
            return node.FullPath;

        var oldName = node.Connection.Name;
        try
        {
            node.Connection.Name = newName;
            var folder = Path.GetDirectoryName(node.FullPath) ?? _store.RootPath;
            return _store.Save(node.Connection, folder, node.FullPath);
        }
        catch
        {
            node.Connection.Name = oldName;
            throw;
        }
    }

    private bool CanModifySelection()
    {
        var selection = EffectiveSelection();
        return selection.Count > 0
            && selection.All(n => n is { IsRecent: false, IsNameEditing: false });
    }

    // --- Copy / cut / paste ---

    /// <summary>Regular selected nodes that can go on the clipboard, with items
    /// nested inside a selected folder removed.</summary>
    private List<TreeNodeViewModel> ClipboardableSelection() =>
        RemoveNestedNodes(EffectiveSelection()
            .Where(n => n is { IsRecent: false, IsNameEditing: false })
            .ToList());

    [RelayCommand(CanExecute = nameof(CanModifySelection))]
    private void Copy()
    {
        var targets = ClipboardableSelection();
        if (targets.Count == 0)
            return;

        SetClipboard(targets, isCut: false);
        StatusMessage = targets.Count == 1
            ? L("StatusCopied", targets[0].Name)
            : L("StatusCopiedMultiple", targets.Count);
    }

    [RelayCommand(CanExecute = nameof(CanModifySelection))]
    private void Cut()
    {
        var targets = ClipboardableSelection();
        if (targets.Count == 0)
            return;

        SetClipboard(targets, isCut: true);
        foreach (var node in targets)
            node.IsCut = true;
        StatusMessage = targets.Count == 1
            ? L("StatusCut", targets[0].Name)
            : L("StatusCutMultiple", targets.Count);
    }

    private void SetClipboard(IReadOnlyList<TreeNodeViewModel> nodes, bool isCut)
    {
        ClearCutFlags(Nodes);
        _clipboardEntries.Clear();
        _clipboardEntries.AddRange(nodes.Select(n => new ClipboardEntry(n.FullPath, n.IsFolder)));
        _clipboardIsCut = isCut;
        HasClipboard = true;
    }

    [RelayCommand(CanExecute = nameof(CanPaste))]
    private void Paste()
    {
        if (_clipboardEntries.Count == 0)
            return;

        if (_clipboardIsCut)
            FlushPendingAutoSave();

        var target = TargetFolder();
        var newPaths = new List<string>();
        var movedEntries = new List<ClipboardEntry>();
        var missing = 0;
        var skipped = 0;
        string? firstError = null;

        foreach (var entry in _clipboardEntries.ToList())
        {
            var exists = entry.IsFolder ? Directory.Exists(entry.Path) : File.Exists(entry.Path);
            if (!exists)
            {
                missing++;
                continue;
            }

            // Guard both copy and move: pasting a folder into itself or one of
            // its own subfolders would recurse forever.
            if (entry.IsFolder && ConnectionStore.IsSameOrInside(entry.Path, target))
            {
                skipped++;
                continue;
            }

            try
            {
                var newPath = entry.IsFolder
                    ? _clipboardIsCut
                        ? _store.MoveFolderInto(entry.Path, target)
                        : _store.CopyFolderInto(entry.Path, target, includeSshScriptBindings: false)
                    : _clipboardIsCut
                        ? _store.MoveFileInto(entry.Path, target)
                        : _store.CopyFileInto(entry.Path, target, includeSshScriptBindings: false);

                // MoveXInto returns the original path unchanged when it's a no-op
                // (pasting back into the same folder). Keep the cut pending in that case.
                if (_clipboardIsCut && PathEquals(newPath, entry.Path))
                {
                    skipped++;
                    continue;
                }

                newPaths.Add(newPath);
                if (_clipboardIsCut)
                {
                    DetachEditorIfEditingPath(entry.Path);
                    movedEntries.Add(entry);
                }
            }
            catch (Exception ex)
            {
                firstError ??= ex.Message;
            }
        }

        // Entries whose source vanished are dropped; moved entries are consumed.
        foreach (var entry in movedEntries)
            _clipboardEntries.Remove(entry);
        if (missing > 0)
            _clipboardEntries.RemoveAll(entry =>
                !(entry.IsFolder ? Directory.Exists(entry.Path) : File.Exists(entry.Path)));
        if (_clipboardEntries.Count == 0 || (_clipboardIsCut && movedEntries.Count > 0))
            ClearClipboard();

        if (newPaths.Count == 0)
        {
            StatusMessage = firstError is not null
                ? L("StatusPasteFailed", firstError)
                : skipped > 0
                    ? L("StatusAlreadyInFolder")
                    : L("StatusClipboardGone");
            return;
        }

        ReloadTree(newPaths[^1]);
        StatusMessage = firstError is not null
            ? L("StatusPasteFailed", firstError)
            : movedEntries.Count > 0
                ? newPaths.Count == 1 ? L("StatusMoved") : L("StatusMovedMultiple", newPaths.Count)
                : newPaths.Count == 1 ? L("StatusPasted") : L("StatusPastedMultiple", newPaths.Count);
    }

    private bool CanPaste() => HasClipboard && SelectedNode is not { IsNameEditing: true };

    /// <summary>
    /// Moves tree nodes into <paramref name="targetFolder"/> (drag &amp; drop).
    /// Mirrors the cut+paste path: same guards, same reload-and-select behavior.
    /// </summary>
    public void MoveNodesTo(IReadOnlyList<TreeNodeViewModel> nodes, string targetFolder)
    {
        var targets = RemoveNestedNodes(nodes
            .Where(n => n is { IsRecent: false, IsNameEditing: false })
            .ToList());
        if (targets.Count == 0)
            return;

        FlushPendingAutoSave();

        var movedPaths = new List<string>();
        var skipped = 0;
        string? firstError = null;

        foreach (var node in targets)
        {
            var source = node.FullPath;
            var exists = node.IsFolder ? Directory.Exists(source) : File.Exists(source);
            if (!exists)
            {
                skipped++;
                continue;
            }

            // Moving a folder into itself or its own subtree would recurse forever.
            if (node.IsFolder && ConnectionStore.IsSameOrInside(source, targetFolder))
            {
                skipped++;
                continue;
            }

            try
            {
                var newPath = node.IsFolder
                    ? _store.MoveFolderInto(source, targetFolder)
                    : _store.MoveFileInto(source, targetFolder);

                // MoveXInto returns the original path unchanged when it's a no-op.
                if (PathEquals(newPath, source))
                {
                    skipped++;
                    continue;
                }

                // A pending copy/cut whose source just moved now points at a stale path.
                RemoveClipboardEntriesUnder(source);

                DetachEditorIfEditingPath(source);
                movedPaths.Add(newPath);
            }
            catch (Exception ex)
            {
                firstError ??= ex.Message;
            }
        }

        if (movedPaths.Count == 0)
        {
            StatusMessage = firstError is not null
                ? L("StatusMoveFailed", firstError)
                : skipped > 0
                    ? L("StatusAlreadyInFolder")
                    : StatusMessage;
            return;
        }

        ReloadTree(movedPaths[^1]);
        StatusMessage = firstError is not null
            ? L("StatusMoveFailed", firstError)
            : movedPaths.Count == 1
                ? L("StatusMoved")
                : L("StatusMovedMultiple", movedPaths.Count);
    }

    private void RequestTreeFocus(TreeNodeViewModel? node)
    {
        if (node is not null && RequestFocusTreeNode is not null)
            RequestFocusTreeNode(node);
        else
            RequestFocusTree?.Invoke();
    }

    private void NotifyTreeActionCanExecuteChanged()
    {
        ConnectCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        RenameCommand.NotifyCanExecuteChanged();
        CopyCommand.NotifyCanExecuteChanged();
        CutCommand.NotifyCanExecuteChanged();
        PasteCommand.NotifyCanExecuteChanged();
        ClearSelectionCommand.NotifyCanExecuteChanged();
    }

    private void ClearClipboard()
    {
        ClearCutFlags(Nodes);
        _clipboardEntries.Clear();
        _clipboardIsCut = false;
        HasClipboard = false;
    }

    /// <summary>Drops clipboard entries that point at <paramref name="path"/> or
    /// anything inside it; clears the clipboard entirely when none remain.</summary>
    private void RemoveClipboardEntriesUnder(string path)
    {
        if (_clipboardEntries.RemoveAll(entry =>
                ConnectionStore.IsSameOrInside(path, entry.Path)) > 0
            && _clipboardEntries.Count == 0)
        {
            ClearClipboard();
        }
    }

    private static void ClearCutFlags(IEnumerable<TreeNodeViewModel> nodes)
    {
        foreach (var node in nodes)
        {
            node.IsCut = false;
            ClearCutFlags(node.Children);
        }
    }

    // --- SSH private key picker ---

    [RelayCommand(CanExecute = nameof(CanBrowseKey))]
    private async Task BrowseKey()
    {
        if (Editor is null || PickKeyFileAsync is null)
            return;

        var path = await PickKeyFileAsync();
        if (!string.IsNullOrEmpty(path))
            Editor.PrivateKeyPath = path;
    }

    // The Browse button is only visible in the SSH section, so gating on a
    // non-null editor is enough (and avoids a stale-disabled button when the
    // editor's Type is switched to SSH without re-selecting the node).
    private bool CanBrowseKey() => Editor is not null;

    // --- SSH script actions ---

    [RelayCommand(CanExecute = nameof(CanRunSelectedScriptBinding))]
    private void RunSelectedScriptBinding()
    {
        var choices = PrepareScriptSuiteChoicesForSelectedConnection();
        if (choices.Count == 1)
            OpenScriptSuiteChoice(choices[0]);
        else if (choices.Count > 1)
            StatusMessage = L("StatusChooseScriptSuite");
    }

    public IReadOnlyList<ScriptSuiteChoiceViewModel> PrepareScriptSuiteChoicesForSelectedConnection()
    {
        if (SelectedNode is not { IsConnection: true, Connection: not null } node)
            return Array.Empty<ScriptSuiteChoiceViewModel>();

        FlushPendingAutoSave();

        if (node.Connection.Type is not (ConnectionType.Ssh or ConnectionType.Wsl))
        {
            StatusMessage = L("StatusScriptOnlySsh");
            return Array.Empty<ScriptSuiteChoiceViewModel>();
        }

        ReloadScripts();
        var removed = PruneMissingScriptBindingsForCurrentConnection(node.Connection);
        if (ScriptSuites.Count == 0)
        {
            StatusMessage = L("StatusNoScripts", $"{_scriptStore.BuiltInRootPath}; {_scriptStore.RootPath}");
            return Array.Empty<ScriptSuiteChoiceViewModel>();
        }

        if (removed > 0)
            StatusMessage = L("StatusMissingScriptBindingsRemoved", removed);
        _scriptContext = new ScriptExecutionContext(node, terminal: null);
        return BuildScriptSuiteChoices(node.Connection);
    }

    /// <summary>
    /// Selects the tree node behind an open terminal tab (matched by its on-disk
    /// path) and prepares its script-suite choices, so the existing selection-based
    /// script flow targets that connection. Returns an empty list when the node is
    /// gone or the connection has no usable scripts.
    /// </summary>
    public IReadOnlyList<ScriptSuiteChoiceViewModel> PrepareScriptSuiteChoicesForTerminal(TerminalScriptSession terminal)
    {
        var node = string.IsNullOrEmpty(terminal.SourcePath) ? null : FindNode(Nodes, terminal.SourcePath);
        if (node is null)
        {
            StatusMessage = L("StatusTerminalConnectionMissing");
            return Array.Empty<ScriptSuiteChoiceViewModel>();
        }

        ExpandAncestors(node);
        SelectedNode = node;
        var choices = PrepareScriptSuiteChoicesForSelectedConnection();
        if (choices.Count > 0)
            _scriptContext = new ScriptExecutionContext(node, terminal);
        return choices;
    }

    public Task CopyPublicKeyToServerAsync(Connection connection) =>
        CopyPublicKeyToServerAsync(
            connection,
            publicKeyText => PublicKeyInstaller.InstallAsync(connection, publicKeyText, ConfirmHostKeyTrust));

    /// <summary>
    /// Installs the local public key on the given connection's host
    /// (the ssh-copy-id equivalent), confirming first and reporting the outcome
    /// via the status bar.
    /// </summary>
    public async Task CopyPublicKeyToServerAsync(
        Connection connection,
        Func<string, Task<PublicKeyInstallResult>> installAsync)
    {
        if (connection.Type != ConnectionType.Ssh)
        {
            StatusMessage = L("StatusScriptOnlySsh");
            return;
        }

        var publicKeyPath = PublicKeyInstaller.FindLocalPublicKey(connection);
        if (publicKeyPath is null)
        {
            StatusMessage = L("StatusNoPublicKey");
            return;
        }

        string publicKeyText;
        try
        {
            publicKeyText = PublicKeyInstaller.ReadPublicKey(publicKeyPath);
        }
        catch (Exception ex)
        {
            StatusMessage = L("StatusPublicKeyFailed", ex.Message);
            return;
        }

        var target = string.IsNullOrWhiteSpace(connection.Name) ? connection.Host : connection.Name;
        if (ConfirmAsync is not null)
        {
            var ok = await ConfirmAsync(
                L("DialogCopyPublicKeyTitle"),
                L("DialogCopyPublicKeyMessage", publicKeyPath, target));
            if (!ok)
                return;
        }

        StatusMessage = L("StatusCopyingPublicKey", target);
        try
        {
            var result = await installAsync(publicKeyText);
            StatusMessage = result.AlreadyPresent
                ? L("StatusPublicKeyAlreadyPresent", target)
                : L("StatusPublicKeyInstalled", target);
        }
        catch (Exception ex)
        {
            StatusMessage = L("StatusPublicKeyFailed", ex.Message);
        }
    }

    public void OpenScriptSuiteChoice(ScriptSuiteChoiceViewModel? choice)
    {
        if (choice is null)
            return;
        var context = _scriptContext;
        var node = context?.Node;
        if (node is not { IsConnection: true, Connection: not null })
            return;

        var suite = choice.Suite;
        var binding = node.Connection.ScriptBindings.LastOrDefault(b =>
            string.Equals(
                RemoteScriptSuiteNames.NormalizeBindingName(b.Name),
                suite.RelativePath,
                StringComparison.OrdinalIgnoreCase));
        if (binding is not null)
            binding.Name = suite.RelativePath;
        binding = binding is null
            ? new ConnectionScriptBinding { Name = suite.RelativePath }
            : RemoteScriptLauncher.UnprotectSecretValues(suite, binding);

        ScriptPanel = new ScriptSuitePanelViewModel(suite, binding, () => _ = SaveScriptPanelBinding());
        RunScriptFileCommand.NotifyCanExecuteChanged();
        StatusMessage = L("StatusScriptSuiteOpened", suite.Name);
    }

    private bool CanRunSelectedScriptBinding() => IsShellConnectionContext;

    private IReadOnlyList<ScriptSuiteChoiceViewModel> BuildScriptSuiteChoices(Connection connection) =>
        SortScriptSuiteChoices(ScriptSuites, connection.ScriptBindings);

    public static IReadOnlyList<ScriptSuiteChoiceViewModel> SortScriptSuiteChoices(
        IEnumerable<RemoteScriptSuite> suites,
        IEnumerable<ConnectionScriptBinding> bindings)
    {
        var boundSuites = new HashSet<string>(
            bindings
                .Where(b => !string.IsNullOrWhiteSpace(b.Name))
                .Select(b => RemoteScriptSuiteNames.NormalizeBindingName(b.Name)),
            StringComparer.OrdinalIgnoreCase);

        return suites
            .Select(suite => new ScriptSuiteChoiceViewModel(suite, boundSuites.Contains(suite.RelativePath)))
            .OrderByDescending(choice => choice.HasParameters)
            .ThenByDescending(choice => choice.Suite.Source == RemoteScriptSuiteSource.User)
            .ThenBy(choice => choice.Suite.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // Concurrent executions are allowed so a running script in one terminal tab
    // does not disable the script buttons of every other tab; CanRunScriptFile
    // still greys out the buttons of the tab whose terminal is busy.
    [RelayCommand(CanExecute = nameof(CanRunScriptFile), AllowConcurrentExecutions = true)]
    private async Task RunScriptFile(RemoteScriptFile? scriptFile)
    {
        var context = _scriptContext;
        if (scriptFile is null
            || ScriptPanel is null
            || context?.Node is not { IsConnection: true, Connection: not null } node)
            return;

        if (ScriptPanel.IsRunning || context.Terminal is { IsScriptRunning: true })
        {
            StatusMessage = L("StatusScriptAlreadyRunning");
            return;
        }

        var binding = SaveScriptPanelBinding(flushImmediately: true);
        if (binding is null)
            return;

        var panel = ScriptPanel;
        var displayName = $"{panel.SuiteName}/{scriptFile.DisplayName}";
        panel.ClearExecutionResult();
        panel.StatusText = L("ScriptExecutionRunning");
        panel.IsRunning = true;
        RunScriptFileCommand.NotifyCanExecuteChanged();
        StatusMessage = L("StatusScriptRunning", displayName, node.Connection.Name);

        try
        {
            var terminal = context.Terminal;
            if (terminal is null)
            {
                if (EnsureSshTerminalAsync is null)
                    throw new InvalidOperationException(Localizer.Get("StatusScriptTerminalUnavailable"));

                panel.StatusText = L("ScriptExecutionWaitingTerminal");
                terminal = await EnsureSshTerminalAsync(node.Connection, node.FullPath);
                context.Terminal = terminal
                    ?? throw new InvalidOperationException(Localizer.Get("StatusScriptTerminalUnavailable"));
            }

            terminal.Activate();
            if (terminal.IsScriptRunning)
            {
                panel.StatusText = L("StatusScriptAlreadyRunning");
                StatusMessage = L("StatusScriptAlreadyRunning");
                return;
            }

            terminal.HideScriptPanel();
            await terminal.WaitUntilConnectedAsync();
            panel.StatusText = L("ScriptExecutionRunningInTerminal");

            var result = await terminal.RunScriptAsync(
                panel.Suite,
                scriptFile,
                binding);

            var duration = FormatScriptDuration(result.FinishedAt - result.StartedAt);
            panel.StatusText = result.ExitCode == 0
                ? L("ScriptExecutionSucceeded", duration)
                : L("ScriptExecutionFailed", result.ExitCode, duration);
            panel.SetExecutionResult(result.ExitCode == 0);
            StatusMessage = result.ExitCode == 0
                ? L("StatusScriptSucceeded", displayName)
                : L("StatusScriptFailed", displayName, result.ExitCode);
        }
        catch (Exception ex)
        {
            panel.StatusText = L("ScriptExecutionStartFailed", ex.Message);
            panel.SetExecutionResult(false);
            StatusMessage = L("StatusScriptLaunchFailed", ex.Message);
        }
        finally
        {
            panel.IsRunning = false;
            RunScriptFileCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanRunScriptFile(RemoteScriptFile? scriptFile) =>
        ScriptPanel is not { IsRunning: true }
        && _scriptContext?.Terminal is not { IsScriptRunning: true };

    // --- Batch script run over the tree multi-selection ---

    // Caps how many servers connect and run at once; the rest queue up.
    private const int BatchScriptMaxConcurrency = 8;

    // Targets captured when the script-suite chooser opens, consumed by
    // OpenBatchScriptSuiteChoice. Deduplicated by connection file path.
    private List<TreeNodeViewModel> _batchScriptNodes = new();

    /// <summary>
    /// Collects the script-capable connections of the current multi-selection and
    /// returns the suite choices for them (a suite counts as "bound" when any
    /// target has saved parameters for it). Empty when fewer than two distinct
    /// connections qualify.
    /// </summary>
    public IReadOnlyList<ScriptSuiteChoiceViewModel> PrepareBatchScriptSuiteChoices()
    {
        FlushPendingAutoSave();

        var targets = new List<TreeNodeViewModel>();
        foreach (var node in EffectiveSelection())
        {
            if (node is not
                {
                    IsConnection: true,
                    IsNameEditing: false,
                    Connection.Type: ConnectionType.Ssh or ConnectionType.Wsl,
                })
            {
                continue;
            }

            // A Recent shadow and its real node share the connection file.
            if (targets.Any(t => PathEquals(t.FullPath, node.FullPath)))
                continue;

            targets.Add(node);
        }

        if (targets.Count < 2)
            return Array.Empty<ScriptSuiteChoiceViewModel>();

        ReloadScripts();
        if (ScriptSuites.Count == 0)
        {
            StatusMessage = L("StatusNoScripts", $"{_scriptStore.BuiltInRootPath}; {_scriptStore.RootPath}");
            return Array.Empty<ScriptSuiteChoiceViewModel>();
        }

        _batchScriptNodes = targets;
        return SortScriptSuiteChoices(
            ScriptSuites,
            targets.SelectMany(t => t.Connection!.ScriptBindings));
    }

    /// <summary>Opens the batch panel for the chosen suite on the targets captured
    /// by <see cref="PrepareBatchScriptSuiteChoices"/>.</summary>
    public void OpenBatchScriptSuiteChoice(ScriptSuiteChoiceViewModel? choice)
    {
        if (choice is null || _batchScriptNodes.Count == 0)
            return;

        var targets = new ObservableCollection<BatchScriptTargetViewModel>(
            _batchScriptNodes.Select(n => new BatchScriptTargetViewModel(n.Connection!, n.FullPath)));

        BatchScriptPanelViewModel? panel = null;
        panel = new BatchScriptPanelViewModel(
            choice.Suite,
            targets,
            scriptFile => RunBatchScriptFileAsync(panel!, scriptFile),
            () =>
            {
                if (ReferenceEquals(BatchPanel, panel))
                    BatchPanel = null;
            });
        BatchPanel = panel;
        StatusMessage = L("StatusScriptSuiteOpened", choice.Suite.Name);
    }

    private async Task RunBatchScriptFileAsync(BatchScriptPanelViewModel panel, RemoteScriptFile scriptFile)
    {
        if (panel.IsRunning)
            return;

        var ensureTerminal = EnsureSshTerminalQuietlyAsync ?? EnsureSshTerminalAsync;
        if (ensureTerminal is null)
        {
            panel.StatusText = Localizer.Get("StatusScriptTerminalUnavailable");
            return;
        }

        var displayName = $"{panel.SuiteName}/{scriptFile.DisplayName}";
        using var cts = new CancellationTokenSource();
        panel.Cts = cts;
        panel.IsRunning = true;
        panel.BeginRun();
        StatusMessage = L("StatusBatchScriptRunning", displayName, panel.Targets.Count);

        try
        {
            var unifiedParams = panel.GetUnifiedParameterValues();
            using var slots = new SemaphoreSlim(BatchScriptMaxConcurrency);
            await Task.WhenAll(panel.Targets.Select(target =>
                RunBatchTargetAsync(panel, target, scriptFile, unifiedParams, ensureTerminal, slots, cts.Token)));

            var succeeded = panel.Targets.Count(t => t.State == BatchScriptTargetState.Succeeded);
            var failed = panel.Targets.Count - succeeded;
            panel.StatusText = L("BatchStatusSummary", succeeded, failed);
            StatusMessage = failed == 0
                ? L("StatusBatchScriptSucceeded", displayName, succeeded)
                : L("StatusBatchScriptFailed", displayName, failed);
        }
        finally
        {
            panel.IsRunning = false;
            panel.Cts = null;
        }
    }

    private async Task RunBatchTargetAsync(
        BatchScriptPanelViewModel panel,
        BatchScriptTargetViewModel target,
        RemoteScriptFile scriptFile,
        IReadOnlyList<KeyValuePair<string, string>> unifiedParams,
        Func<Connection, string?, Task<TerminalScriptSession?>> ensureTerminal,
        SemaphoreSlim slots,
        CancellationToken cancellationToken)
    {
        try
        {
            // Each server runs with its own saved parameters, overridden by the
            // "unified" values from the panel; a target whose merged binding does
            // not validate is skipped instead of failing the batch.
            var binding = ResolveBatchScriptBinding(panel.Suite, target.Connection);
            if (unifiedParams.Count > 0 && ApplyUnifiedScriptParams(binding, unifiedParams))
                PersistBatchScriptBinding(target, panel.Suite, binding);
            var errors = RemoteScriptLauncher.ValidateBinding(panel.Suite, binding);
            if (errors.Count > 0)
            {
                target.SetState(BatchScriptTargetState.Skipped, L("BatchTargetSkipped", errors[0]));
                return;
            }

            await slots.WaitAsync(cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                target.SetState(BatchScriptTargetState.Connecting, L("BatchTargetConnecting"));

                var terminal = await ensureTerminal(target.Connection, target.SourcePath)
                    ?? throw new InvalidOperationException(Localizer.Get("StatusScriptTerminalUnavailable"));
                target.Terminal = terminal;

                if (terminal.IsScriptRunning)
                {
                    target.SetState(BatchScriptTargetState.Skipped, Localizer.Get("StatusScriptAlreadyRunning"));
                    return;
                }

                await terminal.WaitUntilConnectedAsync(cancellationToken);
                target.SetState(BatchScriptTargetState.Running, L("BatchTargetRunning"));

                var result = await terminal.RunScriptAsync(panel.Suite, scriptFile, binding, cancellationToken);
                var duration = FormatScriptDuration(result.FinishedAt - result.StartedAt);
                if (result.ExitCode == 0)
                    target.SetState(BatchScriptTargetState.Succeeded, L("BatchTargetSucceeded", duration));
                else
                    target.SetState(BatchScriptTargetState.Failed, L("BatchTargetFailed", result.ExitCode, duration));
            }
            finally
            {
                slots.Release();
            }
        }
        catch (OperationCanceledException)
        {
            target.SetState(BatchScriptTargetState.Canceled, L("BatchTargetCanceled"));
        }
        catch (Exception ex)
        {
            target.SetState(BatchScriptTargetState.Failed, ex.Message);
        }
        finally
        {
            panel.ReportTargetFinished();
        }
    }

    /// <summary>The connection's saved parameters for the suite (cloned, secrets
    /// left protected — payload building decrypts them), or an empty binding that
    /// falls back to the suite's defaults.</summary>
    private static ConnectionScriptBinding ResolveBatchScriptBinding(RemoteScriptSuite suite, Connection connection) =>
        BatchScriptPanelViewModel.FindSavedBinding(connection, suite)
        ?? new ConnectionScriptBinding { Name = suite.RelativePath };

    /// <summary>Overrides the binding's parameters with the panel's unified values.
    /// Returns true when any value actually differed from what was stored (stored
    /// secrets are compared decrypted), so unchanged runs skip the file save.</summary>
    private static bool ApplyUnifiedScriptParams(
        ConnectionScriptBinding binding,
        IReadOnlyList<KeyValuePair<string, string>> unifiedParams)
    {
        var changed = false;
        foreach (var (name, value) in unifiedParams)
        {
            var existing = binding.Params.FirstOrDefault(p =>
                string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

            var current = existing?.Value;
            if (current is not null
                && MasterKeyService.IsPasswordBlob(current)
                && PasswordProtector.TryDecrypt(current, out var clear))
            {
                current = clear;
            }

            if (!string.Equals(current, value, StringComparison.Ordinal))
                changed = true;

            if (existing is null)
                binding.Params.Add(new ConnectionScriptParameterValue { Name = name, Value = value });
            else
                existing.Value = value;
        }

        return changed;
    }

    /// <summary>
    /// Writes the merged binding back to the target's connection file so unified
    /// parameter values persist for later single-server and batch runs.
    /// </summary>
    private void PersistBatchScriptBinding(
        BatchScriptTargetViewModel target,
        RemoteScriptSuite suite,
        ConnectionScriptBinding binding)
    {
        var node = FindNode(Nodes, target.SourcePath);
        var connection = node?.Connection ?? target.Connection;

        var protectedBinding = RemoteScriptLauncher.ProtectSecretValues(suite, binding);
        UpsertScriptBinding(connection.ScriptBindings, protectedBinding);

        if (node is not null)
        {
            // The connection may be open in the editor; keep its bindings view in
            // sync and let the editor's auto-save own the file write.
            if (ReferenceEquals(_editingNode, node) && Editor is not null)
            {
                UpsertScriptBinding(Editor.ScriptBindings, protectedBinding);
                _editorHasPendingChanges = true;
                FlushPendingAutoSave();
            }
            else
            {
                SaveScriptContextConnection(node);
            }

            return;
        }

        try
        {
            ProtectConnectionScriptBindings(connection);
            var folder = Path.GetDirectoryName(target.SourcePath) ?? _store.RootPath;
            _store.Save(connection, folder, target.SourcePath);
        }
        catch (Exception ex)
        {
            StatusMessage = L("StatusAutoSaveFailed", ex.Message);
        }
    }

    private ConnectionScriptBinding? SaveScriptPanelBinding(bool flushImmediately = false)
    {
        if (ScriptPanel is null
            || _scriptContext?.Node is not { IsConnection: true, Connection: not null } node)
            return null;

        var currentBinding = ScriptPanel.ToBinding();
        var existingBinding = node.Connection.ScriptBindings.LastOrDefault(b =>
            string.Equals(b.Name, currentBinding.Name, StringComparison.OrdinalIgnoreCase));

        if (existingBinding is not null
            && ScriptBindingsEquivalent(ScriptPanel.Suite, currentBinding, existingBinding))
        {
            return existingBinding;
        }

        if (existingBinding is null && !HasMeaningfulScriptParams(ScriptPanel.Suite, currentBinding))
            return currentBinding;

        var protectedBinding = RemoteScriptLauncher.ProtectSecretValues(ScriptPanel.Suite, currentBinding);
        if (ReferenceEquals(_editingNode, node) && Editor is not null)
            UpsertScriptBinding(Editor.ScriptBindings, protectedBinding);
        UpsertScriptBinding(node.Connection.ScriptBindings, protectedBinding);

        if (!ReferenceEquals(_editingNode, node))
        {
            SaveScriptContextConnection(node);
        }
        else
        {
            _editorHasPendingChanges = true;
            if (flushImmediately)
                FlushPendingAutoSave();
            else
                ScheduleAutoSave();
        }

        RunSelectedScriptBindingCommand.NotifyCanExecuteChanged();
        return protectedBinding;
    }

    private void SaveScriptContextConnection(TreeNodeViewModel node)
    {
        if (node.Connection is null)
            return;

        try
        {
            ProtectConnectionScriptBindings(node.Connection);
            var folder = Path.GetDirectoryName(node.FullPath) ?? _store.RootPath;
            var newPath = _store.Save(node.Connection, folder, node.FullPath);
            if (!PathEquals(newPath, node.FullPath))
            {
                node.FullPath = newPath;
                node.Name = node.Connection.Name;
            }
        }
        catch (Exception ex)
        {
            StatusMessage = L("StatusAutoSaveFailed", ex.Message);
        }
    }

    private static bool ScriptBindingsEquivalent(
        RemoteScriptSuite suite,
        ConnectionScriptBinding currentBinding,
        ConnectionScriptBinding storedBinding)
    {
        var current = ComparableScriptParams(suite, currentBinding);
        var stored = ComparableScriptParams(
            suite,
            RemoteScriptLauncher.UnprotectSecretValues(suite, storedBinding));

        if (current.Count != stored.Count)
            return false;

        foreach (var (name, value) in current)
        {
            if (!stored.TryGetValue(name, out var storedValue)
                || !string.Equals(value, storedValue, StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private static Dictionary<string, string> ComparableScriptParams(
        RemoteScriptSuite suite,
        ConnectionScriptBinding binding)
    {
        var values = binding.Params.ToDictionary(v => v.Name, v => v.Value, StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var parameter in suite.Parameters)
        {
            var value = values.TryGetValue(parameter.Name, out var storedValue)
                ? storedValue
                : GetDefaultScriptParamValue(parameter);
            result[parameter.Name] = NormalizeComparableScriptParam(parameter, value);
        }

        return result;
    }

    private static bool HasMeaningfulScriptParams(RemoteScriptSuite suite, ConnectionScriptBinding binding) =>
        ComparableScriptParams(suite, binding).Any(item => !IsDefaultScriptParamValue(suite, item.Key, item.Value));

    private static bool IsDefaultScriptParamValue(RemoteScriptSuite suite, string name, string value)
    {
        var parameter = suite.Parameters.FirstOrDefault(p =>
            string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        return parameter is not null
            && string.Equals(value, NormalizeComparableScriptParam(parameter, GetDefaultScriptParamValue(parameter)),
                StringComparison.Ordinal);
    }

    private static string NormalizeComparableScriptParam(RemoteScriptParameter parameter, string value)
    {
        if (parameter.Type == RemoteScriptParameterType.Bool)
        {
            return value.Trim().ToLowerInvariant() switch
            {
                "true" or "1" or "yes" or "y" => "true",
                _ => "false",
            };
        }

        if (parameter.Type == RemoteScriptParameterType.Enum)
        {
            return parameter.EnumOptions.FirstOrDefault(o =>
                string.Equals(o, value, StringComparison.OrdinalIgnoreCase)) ?? value;
        }

        return value;
    }

    private static string GetDefaultScriptParamValue(RemoteScriptParameter parameter) =>
        parameter.Type == RemoteScriptParameterType.Bool && string.IsNullOrEmpty(parameter.DefaultValue)
            ? "false"
            : parameter.DefaultValue;

    private static void UpsertScriptBinding(
        ObservableCollection<ConnectionScriptBindingViewModel> bindings,
        ConnectionScriptBinding binding)
    {
        for (var i = bindings.Count - 1; i >= 0; i--)
        {
            if (string.Equals(bindings[i].Name, binding.Name, StringComparison.OrdinalIgnoreCase))
                bindings.RemoveAt(i);
        }

        bindings.Add(ConnectionScriptBindingViewModel.FromModel(binding));
    }

    private static void UpsertScriptBinding(
        IList<ConnectionScriptBinding> bindings,
        ConnectionScriptBinding binding)
    {
        for (var i = bindings.Count - 1; i >= 0; i--)
        {
            if (string.Equals(bindings[i].Name, binding.Name, StringComparison.OrdinalIgnoreCase))
                bindings.RemoveAt(i);
        }

        bindings.Add(RemoteScriptLauncher.CloneBinding(binding));
    }

    private static void RemoveScriptBinding(
        ObservableCollection<ConnectionScriptBindingViewModel> bindings,
        string name)
    {
        for (var i = bindings.Count - 1; i >= 0; i--)
        {
            if (string.Equals(bindings[i].Name, name, StringComparison.OrdinalIgnoreCase))
                bindings.RemoveAt(i);
        }
    }

    private static void RemoveScriptBinding(
        IList<ConnectionScriptBinding> bindings,
        string name)
    {
        for (var i = bindings.Count - 1; i >= 0; i--)
        {
            if (string.Equals(bindings[i].Name, name, StringComparison.OrdinalIgnoreCase))
                bindings.RemoveAt(i);
        }
    }

    [RelayCommand]
    private async Task CopyScriptOutput()
    {
        if (Clipboard is null || ScriptPanel is null)
            return;

        await Clipboard.SetTextAsync(ScriptPanel.Output);
        StatusMessage = L("StatusScriptOutputCopied");
    }

    [RelayCommand]
    private void ClearScriptParameters()
    {
        if (ScriptPanel is null
            || _scriptContext?.Node is not { IsConnection: true, Connection: not null } node)
            return;

        if (ScriptPanel.IsRunning)
        {
            StatusMessage = L("StatusScriptStillRunning");
            return;
        }

        var suitePath = ScriptPanel.Suite.RelativePath;
        ScriptPanel.ClearParameters();
        if (ReferenceEquals(_editingNode, node) && Editor is not null)
            RemoveScriptBinding(Editor.ScriptBindings, suitePath);
        RemoveScriptBinding(node.Connection.ScriptBindings, suitePath);
        if (ReferenceEquals(_editingNode, node))
        {
            ScheduleAutoSave();
        }
        else
        {
            SaveScriptContextConnection(node);
        }
        RunSelectedScriptBindingCommand.NotifyCanExecuteChanged();
        StatusMessage = L("StatusScriptParametersCleared", ScriptPanel.SuiteName);
    }

    [RelayCommand]
    private void ClearScriptOutput()
    {
        if (ScriptPanel is null)
            return;

        ScriptPanel.Output = "";
    }

    [RelayCommand]
    private void CloseScriptExecution()
    {
        if (ScriptPanel is { IsRunning: true })
        {
            StatusMessage = L("StatusScriptStillRunning");
            return;
        }

        ScriptPanel = null;
        _scriptContext = null;
    }

    private static string FormatScriptDuration(TimeSpan value)
    {
        if (value.TotalHours >= 1)
            return value.ToString(@"h\:mm\:ss");
        if (value.TotalMinutes >= 1)
            return value.ToString(@"m\:ss");
        return value.TotalSeconds < 1 ? "<1s" : $"{value.TotalSeconds:0.#}s";
    }

    // --- Misc ---

    [RelayCommand]
    private void Refresh() => ReloadTree(SelectedNode?.FullPath);

    /// <summary>Clears the tree selection so subsequent new/paste operations target the root.</summary>
    // Disabled while renaming so the tree's Escape key binding doesn't swallow
    // the key before the name editor can cancel the edit.
    [RelayCommand(CanExecute = nameof(CanClearSelection))]
    private void ClearSelection() => SelectedNode = null;

    private bool CanClearSelection() => _renamingNode is null;

    [RelayCommand]
    private void OpenStorageFolder()
    {
        try
        {
            Directory.CreateDirectory(_store.RootPath);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _store.RootPath,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            StatusMessage = L("StatusOpenFolderFailed", ex.Message);
        }
    }

    // Cancelled when the user changes the periodic-check interval, so the loop
    // wakes from its long sleep and picks up the new cadence immediately.
    private CancellationTokenSource _updateIntervalChanged = new();

    // Guards against overlapping downloads when the periodic silent check and
    // the manual menu command race each other. Only touched on the UI thread.
    private bool _updateDownloadInProgress;

    private static string FormatUpdateDownloadProgress(UpdateDownloadProgress p)
    {
        var speed = $"{p.BytesPerSecond / (1024 * 1024):0.0} MB/s";
        if (p.TotalBytes is > 0)
        {
            var percent = Math.Min(100, (int)(p.ReceivedBytes * 100 / p.TotalBytes.Value));
            return $"{percent}% ({FileBrowserViewModel.FormatSize(p.ReceivedBytes)} / " +
                   $"{FileBrowserViewModel.FormatSize(p.TotalBytes.Value)}, {speed})";
        }

        return $"{FileBrowserViewModel.FormatSize(p.ReceivedBytes)}, {speed}";
    }

    /// <summary>
    /// Background task driving auto-updates: an optional silent check shortly
    /// after launch (per <see cref="AppSettings.CheckUpdateOnStartup"/>) and an
    /// optional periodic check (per <see cref="AppSettings.UpdateCheckIntervalHours"/>).
    /// Both gates are re-read each iteration so settings changes take effect
    /// without a restart. Failures are swallowed.
    /// </summary>
    public async Task RunBackgroundUpdateChecksAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

            if (_settings.Settings.CheckUpdateOnStartup)
                await CheckOnceSilentlyAsync().ConfigureAwait(false);

            while (true)
            {
                var hours = _settings.Settings.UpdateCheckIntervalHours;
                // Idle (1h) re-poll when periodic is disabled, so enabling it
                // from Settings starts taking effect within the hour.
                var delay = hours > 0 ? TimeSpan.FromHours(hours) : TimeSpan.FromHours(1);

                var waker = _updateIntervalChanged;
                try
                {
                    await Task.Delay(delay, waker.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Interval changed mid-wait; loop around to read the new value.
                    _updateIntervalChanged = new CancellationTokenSource();
                    continue;
                }

                if (_settings.Settings.UpdateCheckIntervalHours > 0)
                    await CheckOnceSilentlyAsync().ConfigureAwait(false);
            }
        }
        catch
        {
            // Best-effort: never let an update-check error tear the app down.
        }
    }

    private async Task CheckOnceSilentlyAsync()
    {
        try
        {
            var outcome = await AutoUpdateService.HasUpdateAsync().ConfigureAwait(false);
            if (outcome != UpdateCheckOutcome.Available)
                return;

            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                await PromptUpdateAsync(silentIfUpToDate: true);
            });
        }
        catch
        {
            // Best-effort.
        }
    }

    [RelayCommand]
    private async Task CheckForUpdates()
    {
        StatusMessage = L("StatusCheckingUpdates");
        var outcome = await AutoUpdateService.HasUpdateAsync();
        await PromptUpdateAsync(silentIfUpToDate: false, outcome);
    }

    private void ApplyLanguage(string? language)
    {
        // Empty / null means "follow system": clear the stored preference and
        // resolve from the current OS culture (falling back to en if unsupported).
        if (string.IsNullOrEmpty(language))
        {
            _settings.Settings.Language = null;

            var system = System.Globalization.CultureInfo.CurrentCulture.TwoLetterISOLanguageName;
            Localizer.Language = Localizer.Languages.Contains(system) ? system : "en";
            return;
        }

        if (!Localizer.Languages.Contains(language))
            return;

        Localizer.Language = language;
        _settings.Settings.Language = language;
    }

    private void ApplyTheme(string? theme)
    {
        // Empty / null means "follow system": clear the stored preference and
        // let Avalonia resolve the variant from the OS theme.
        _settings.Settings.Theme = string.IsNullOrEmpty(theme) ? null : theme;

        if (Avalonia.Application.Current is { } app)
            app.RequestedThemeVariant = App.ThemeVariantFor(_settings.Settings.Theme);
    }

    private async Task PromptUpdateAsync(bool silentIfUpToDate, UpdateCheckOutcome? known = null)
    {
        var outcome = known ?? await AutoUpdateService.HasUpdateAsync();
        switch (outcome)
        {
            case UpdateCheckOutcome.Available:
                if (ConfirmAsync is null || _updateDownloadInProgress)
                    return;
                var ok = await ConfirmAsync(
                    L("DialogUpdateAvailableTitle"),
                    L("DialogUpdateAvailableMessage",
                        AutoUpdateService.LocalCommitCount,
                        AutoUpdateService.RemoteCommitCount));
                if (!ok)
                {
                    StatusMessage = L("StatusUpdatePostponed");
                    return;
                }

                // Download and stage the package while the app stays fully
                // usable; a failed download just leaves a status message.
                string? stagedDir;
                _updateDownloadInProgress = true;
                try
                {
                    var progress = new Progress<UpdateDownloadProgress>(p =>
                        StatusMessage = L("StatusDownloadingUpdate", FormatUpdateDownloadProgress(p)));
                    StatusMessage = L("StatusDownloadingUpdate", "");
                    stagedDir = await AutoUpdateService.DownloadAndStageAsync(progress: progress);
                }
                finally
                {
                    _updateDownloadInProgress = false;
                }

                if (stagedDir is null)
                {
                    StatusMessage = L("StatusUpdateDownloadFailed", AutoUpdateService.FailureReason ?? "");
                    return;
                }

                // The download may have taken a while; re-confirm before the
                // restart so we never tear down live sessions unannounced.
                var restart = await ConfirmAsync(
                    L("DialogUpdateReadyTitle"),
                    L("DialogUpdateReadyMessage"));
                if (!restart)
                {
                    StatusMessage = L("StatusUpdatePostponed");
                    return;
                }

                FlushPendingAutoSave();
                FlushSettings();

                if (!AutoUpdateService.LaunchInstall(stagedDir))
                {
                    StatusMessage = L("StatusUpdateLauncherFail");
                    return;
                }

                // Hand off to the PowerShell updater: it waits for our exit,
                // replaces the install, then relaunches the app.
                if (Avalonia.Application.Current?.ApplicationLifetime
                    is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
                {
                    desktop.Shutdown();
                }
                break;

            case UpdateCheckOutcome.UpToDate:
                if (!silentIfUpToDate)
                    StatusMessage = L("StatusUpToDate", AutoUpdateService.LocalCommitCount);
                break;

            case UpdateCheckOutcome.Failed:
                if (!silentIfUpToDate)
                    StatusMessage = L("StatusUpdateFailed", AutoUpdateService.FailureReason ?? "");
                break;
        }
    }

    [RelayCommand]
    private async Task ImportFinalShell()
    {
        if (PickFolderAsync is null)
            return;

        FlushPendingAutoSave();

        var defaultHint = @"C:\Library\Software\Net\RemoteControl\FinalShell\conn";
        var picked = await PickFolderAsync(defaultHint, L("DialogPickFinalShellTitle"));
        if (string.IsNullOrEmpty(picked))
            return;

        try
        {
            var importer = new FinalShellImporter(_store);
            var result = importer.Import(picked);
            ReloadTree();
            StatusMessage = L("StatusImported", result.Imported, result.Folders, result.Skipped);
        }
        catch (Exception ex)
        {
            StatusMessage = L("StatusImportFailed", ex.Message);
        }
    }

    [RelayCommand]
    private async Task ImportSecureCrt()
    {
        if (PickFolderAsync is null)
            return;

        FlushPendingAutoSave();

        var defaultHint = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VanDyke", "Config", "Sessions");
        if (!Directory.Exists(defaultHint))
            defaultHint = @"C:\Library\Software\Net\RemoteControl\SecureCRT\Data\settings\config\Sessions";

        var picked = await PickFolderAsync(defaultHint, L("DialogPickSecureCrtTitle"));
        if (string.IsNullOrEmpty(picked))
            return;

        try
        {
            var result = ImportSecureCrtFromDirectory(picked);
            StatusMessage = FormatSessionImportResult(
                result.Imported, result.Folders, result.Skipped, result.PasswordsImported);
        }
        catch (Exception ex)
        {
            StatusMessage = L("StatusImportFailed", ex.Message);
        }
    }

    [RelayCommand]
    private async Task ImportXshell()
    {
        if (PickFolderAsync is null)
            return;

        FlushPendingAutoSave();

        var defaultHint = FindDefaultXshellSessionsPath()
                          ?? Path.Combine(
                              Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                              "NetSarang Computer");

        var picked = await PickFolderAsync(defaultHint, L("DialogPickXshellTitle"));
        if (string.IsNullOrEmpty(picked))
            return;

        try
        {
            var result = ImportXshellFromDirectory(picked);
            StatusMessage = FormatSessionImportResult(
                result.Imported, result.Folders, result.Skipped, result.PasswordsImported);
        }
        catch (Exception ex)
        {
            StatusMessage = L("StatusImportFailed", ex.Message);
        }
    }

    /// <summary>
    /// Imports SecureCRT sessions from a Sessions directory. Used by the UI
    /// command and by the Debug MCP for automated testing.
    /// </summary>
    public SecureCrtImporter.ImportResult ImportSecureCrtFromDirectory(string sessionsRoot)
    {
        FlushPendingAutoSave();
        var result = new SecureCrtImporter(_store).Import(sessionsRoot);
        ReloadTree();
        return result;
    }

    /// <summary>
    /// Imports Xshell sessions from a Sessions directory. Used by the UI
    /// command and by the Debug MCP for automated testing.
    /// </summary>
    public XshellImporter.ImportResult ImportXshellFromDirectory(string sessionsRoot)
    {
        FlushPendingAutoSave();
        var result = new XshellImporter(_store).Import(sessionsRoot);
        ReloadTree();
        return result;
    }

    private string FormatSessionImportResult(
        int imported, int folders, int skipped, int passwordsImported)
    {
        var message = L("StatusImportedConnections", imported, folders, skipped);
        if (imported <= 0)
            return message;

        message += " " + (passwordsImported > 0
            ? L("StatusImportedPasswords", passwordsImported)
            : L("StatusImportedNoPasswords"));
        return message;
    }

    private static string? FindDefaultXshellSessionsPath()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var root = Path.Combine(documents, "NetSarang Computer");
        if (!Directory.Exists(root))
            return null;

        // Prefer the highest versioned Xshell\Sessions folder.
        string? best = null;
        var bestVersion = -1.0;
        foreach (var versionDir in Directory.GetDirectories(root))
        {
            var sessions = Path.Combine(versionDir, "Xshell", "Sessions");
            if (!Directory.Exists(sessions))
                continue;

            var name = Path.GetFileName(versionDir);
            if (!double.TryParse(name, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var version))
                version = 0;

            if (best is null || version >= bestVersion)
            {
                best = sessions;
                bestVersion = version;
            }
        }

        return best;
    }

    [RelayCommand]
    private async Task OpenSettings()
    {
        // Flush in case the user was mid-edit when changing storage location.
        FlushPendingAutoSave();

        if (PickSettingsAsync is null)
            return;

        var current = _settings.CurrentStorageLocation;
        var currentCustomPath = _settings.Settings.CustomStoragePath;
        var result = await PickSettingsAsync(
            current,
            currentCustomPath,
            _settings.Settings.Language,
            _settings.Settings.Theme,
            _settings.Settings.CheckUpdateOnStartup,
            _settings.Settings.UpdateCheckIntervalHours,
            _settings.Settings.FileBrowserEditorPath);
        if (result is null)
            return;

        // Apply the remote-editing editor; takes effect on the next F4 open.
        var editorPath = string.IsNullOrWhiteSpace(result.FileBrowserEditorPath)
            ? null
            : result.FileBrowserEditorPath.Trim();
        if (editorPath != _settings.Settings.FileBrowserEditorPath)
        {
            _settings.Settings.FileBrowserEditorPath = editorPath;
            _settings.SaveIfChanged();
        }
        // Apply the language choice (no-op if unchanged); takes effect immediately.
        if (result.Language != _settings.Settings.Language)
            ApplyLanguage(result.Language);

        // Apply the theme choice (no-op if unchanged); takes effect immediately.
        if (result.Theme != _settings.Settings.Theme)
            ApplyTheme(result.Theme);

        // Apply auto-update preferences. A changed interval wakes the periodic
        // loop so the new cadence kicks in without a restart.
        var intervalChanged = result.UpdateCheckIntervalHours != _settings.Settings.UpdateCheckIntervalHours;
        if (result.CheckUpdateOnStartup != _settings.Settings.CheckUpdateOnStartup
            || intervalChanged)
        {
            _settings.Settings.CheckUpdateOnStartup = result.CheckUpdateOnStartup;
            _settings.Settings.UpdateCheckIntervalHours = result.UpdateCheckIntervalHours;
            if (intervalChanged)
                _updateIntervalChanged.Cancel();
        }

        // A no-op when neither the location nor (for a custom location) its path changed.
        if (result.StorageLocation == current
            && (result.StorageLocation != StorageLocation.CustomDirectory
                || string.Equals(result.CustomStoragePath, currentCustomPath, StringComparison.OrdinalIgnoreCase)))
            return;

        var oldConfigRoot = _settings.ResolveConfigRoot();
        var newRoot = SettingsService.ResolveConnectionsRoot(result.StorageLocation, result.CustomStoragePath);
        var newScriptsRoot = SettingsService.ResolveScriptsRoot(result.StorageLocation, result.CustomStoragePath);
        var newConfigRoot = SettingsService.ResolveConfigRoot(result.StorageLocation, result.CustomStoragePath);

        var moveConfigData = ConfirmAsync is null
            || await ConfirmAsync(
                L("DialogMoveConfigTitle"),
                L("DialogMoveConfigMessage", oldConfigRoot, newConfigRoot));

        if (moveConfigData)
        {
            try
            {
                StopWatching();
                SettingsService.MoveConfigRoot(oldConfigRoot, newConfigRoot);
            }
            catch (Exception ex)
            {
                StatusMessage = L("StatusStorageCopyFailed", ex.Message);
                return;
            }
        }
        else
        {
            StopWatching();
        }

        _settings.Settings.StorageLocation = result.StorageLocation;
        _settings.Settings.CustomStoragePath = result.CustomStoragePath;
        var settingsSaved = _settings.SaveIfChanged();
        DebugInstanceContext.SetConfigRoot(_settings.ResolveConfigRoot());
        DebugMcpServer.RefreshDiscovery();
        _store.SetRoot(newRoot);
        _scriptStore.SetRoot(newScriptsRoot);
        ReloadScripts();
        StartWatchingCurrentStorage();
        ClearClipboard();
        ReloadTree();
        OnPropertyChanged(nameof(RootPath));
        OnPropertyChanged(nameof(TargetDescription));

        if (!settingsSaved)
            StatusMessage = L("StatusStorageNotSaved", _settings.SettingsPath);
        else if (HasData(newConfigRoot))
            StatusMessage = L("StatusStorageLocationWithData", result.StorageLocation, newConfigRoot);
        else
            StatusMessage = L("StatusStorageLocationOnly", result.StorageLocation, newConfigRoot);
    }

    /// <summary>
    /// Switches the master password by re-encrypting every stored secret on every
    /// connection — the login password, the private-key passphrase, and any
    /// script-binding secret parameters — each decrypted with the current master
    /// password and re-encrypted as a fresh jrm1 blob under <paramref name="newPassword"/>.
    /// If any secret on a connection cannot be decrypted, the whole change aborts
    /// before anything is written, so we never strand data. The new password
    /// replaces the cached one only after the sweep succeeds.
    /// </summary>
    public void ChangeMasterPassword(string newPassword)
    {
        try
        {
            FlushPendingAutoSave();

            var current = MasterKeyService.Current
                          ?? throw new InvalidOperationException("Master password not initialised.");

            var pending = new List<(
                string File,
                Connection Connection,
                string? ClearPassword,
                string? ClearPassphrase,
                List<(ConnectionScriptParameterValue Param, string Clear)> ScriptSecrets)>();
            var unreadable = 0;

            foreach (var file in _store.AllConnectionFiles())
            {
                try
                {
                    var c = _store.Load(file);

                    string? clearPassword = null;
                    string? clearPassphrase = null;
                    var scriptSecrets = new List<(ConnectionScriptParameterValue Param, string Clear)>();
                    var failed = false;

                    // Every master-password-encrypted secret on the connection must
                    // decrypt with the CURRENT master password; if any one can't, skip
                    // the whole connection so we never clobber it under a new password.
                    if (!string.IsNullOrEmpty(c.EncryptedPassword))
                    {
                        if (current.TryDecryptPassword(c.EncryptedPassword, out var clear))
                            clearPassword = clear;
                        else
                            failed = true;
                    }

                    if (!failed && !string.IsNullOrEmpty(c.EncryptedPrivateKeyPassphrase))
                    {
                        if (current.TryDecryptPassword(c.EncryptedPrivateKeyPassphrase, out var clearPp))
                            clearPassphrase = clearPp;
                        else
                            failed = true;
                    }

                    // Script-binding secret parameters are jrm1 blobs too (detected by
                    // prefix, no suite definition needed).
                    if (!failed)
                    {
                        foreach (var param in c.ScriptBindings.SelectMany(b => b.Params))
                        {
                            if (string.IsNullOrEmpty(param.Value) || !MasterKeyService.IsPasswordBlob(param.Value))
                                continue;
                            if (current.TryDecryptPassword(param.Value, out var clearSecret))
                                scriptSecrets.Add((param, clearSecret));
                            else
                            {
                                failed = true;
                                break;
                            }
                        }
                    }

                    if (failed)
                    {
                        unreadable++;
                        continue;
                    }

                    // Nothing encrypted on this connection -> nothing to migrate.
                    if (clearPassword is null && clearPassphrase is null && scriptSecrets.Count == 0)
                        continue;

                    pending.Add((file, c, clearPassword, clearPassphrase, scriptSecrets));
                }
                catch
                {
                    unreadable++;
                }
            }

            if (unreadable > 0)
                throw new InvalidOperationException(L("MasterChangeUnreadablePasswords", unreadable));

            foreach (var item in pending)
            {
                if (item.ClearPassword is not null)
                    item.Connection.EncryptedPassword =
                        MasterKeyService.EncryptWithPassword(newPassword, item.ClearPassword);
                if (item.ClearPassphrase is not null)
                    item.Connection.EncryptedPrivateKeyPassphrase =
                        MasterKeyService.EncryptWithPassword(newPassword, item.ClearPassphrase);
                foreach (var (param, clear) in item.ScriptSecrets)
                    param.Value = MasterKeyService.EncryptWithPassword(newPassword, clear);
                _store.SaveInPlace(item.Connection, item.File);
            }

            current.SetPassword(newPassword);
            StatusMessage = L("StatusMasterChanged");
        }
        catch (Exception ex)
        {
            StatusMessage = L("StatusMasterChangeFailed", ex.Message);
        }
    }

    private static bool HasData(string folder) =>
        Directory.Exists(folder) &&
        (Directory.GetFiles(folder, "*" + ConnectionStore.FileExtension).Length > 0 ||
         Directory.GetDirectories(folder).Length > 0);
}
