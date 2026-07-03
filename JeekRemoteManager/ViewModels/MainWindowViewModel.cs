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

namespace JeekRemoteManager.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly ConnectionStore _store;
    private readonly ConnectionLauncher _launcher;
    private readonly SettingsService _settings;
    private readonly RemoteScriptStore _scriptStore;
    private ScriptExecutionContext? _scriptContext;

    // Internal (in-app) clipboard for copy/cut/paste of nodes.
    private string? _clipboardPath;
    private bool _clipboardIsFolder;
    private bool _clipboardIsCut;

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
    private TreeNodeViewModel? _recentGroup;

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
    private TreeNodeViewModel? _selectedNode;

    /// <summary>True when the next SelectedNode assignment is a right-click target
    /// for the context menu and should not trigger the one-click Recent shortcut.</summary>
    public bool SuppressRecentAutoLaunch { get; set; }

    /// <summary>True when the selection is the synthetic "Recent" group folder.</summary>
    public bool IsRecentGroupContext => SelectedNode is { IsRecent: true, IsFolder: true };

    /// <summary>True when the selection is a connection shadow under the "Recent" group.</summary>
    public bool IsRecentConnectionContext => SelectedNode is { IsRecent: true, IsConnection: true };

    /// <summary>True when the selection is on a regular (non-Recent) node or empty area.</summary>
    public bool IsRegularContext => SelectedNode is null || !SelectedNode.IsRecent;

    /// <summary>True when the selection is a regular (non-Recent) connection that can be edited.</summary>
    public bool IsRegularConnectionContext => SelectedNode is { IsRecent: false, IsConnection: true };

    public bool IsSshConnectionContext =>
        SelectedNode is { IsConnection: true, Connection: { Type: ConnectionType.Ssh } };

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

    public bool ShowConnectionEditor => Editor is not null;

    public bool ShowPlaceholder => Editor is null;

    // Wired up by the view so the VM can reach platform services without a
    // hard dependency on the window.
    public IClipboard? Clipboard { get; set; }
    public Func<string, string, Task<bool>>? ConfirmAsync { get; set; }
    public Func<string, string, string, Task<string?>>? PromptAsync { get; set; }
    public Func<Task<string?>>? PickKeyFileAsync { get; set; }
    public Func<StorageLocation, string?, string?, string?, bool, int, Task<SettingsDialogResult?>>? PickSettingsAsync { get; set; }
    public Func<string, Task<string?>>? PickFolderAsync { get; set; }

    /// <summary>Opens an in-app SSH terminal for the connection (set by the view).
    /// The second argument is the connection's on-disk file path, carried so the
    /// terminal tab's context menu can act on the originating tree node.</summary>
    public Func<Connection, string?, Task>? OpenSshTerminalAsync { get; set; }

    /// <summary>Returns an SSH terminal tab for script execution, reusing an open
    /// terminal for the same connection file when possible.</summary>
    public Func<Connection, string?, Task<TerminalScriptSession?>>? EnsureSshTerminalAsync { get; set; }

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

    /// <summary>Persisted AI panel options (provider, per-provider model/effort, checkboxes),
    /// shared across terminal tabs and remembered across runs.</summary>
    public AiPanelOptions AiPanelOptions
    {
        get => new(
            _settings.Settings.AiProvider,
            new Dictionary<string, AiProviderChoice>(_settings.Settings.AiProviderChoices),
            _settings.Settings.AiAutoRun,
            _settings.Settings.AiShowCommandOutput,
            _settings.Settings.AiAgentMode);
        set
        {
            _settings.Settings.AiProvider = value.Provider;
            _settings.Settings.AiProviderChoices = value.ProviderChoices.ToDictionary(
                pair => pair.Key,
                pair => new AiProviderChoice { Model = pair.Value.Model, Effort = pair.Value.Effort });
            _settings.Settings.AiAutoRun = value.AutoRun;
            _settings.Settings.AiShowCommandOutput = value.ShowCommandOutput;
            _settings.Settings.AiAgentMode = value.AgentMode;
            _settings.SaveIfChanged();
        }
    }

    /// <summary>User-defined AI API providers, persisted with the roaming settings.</summary>
    public List<CustomAiProvider> CustomAiProviders =>
        _settings.Settings.CustomAiProviders.Select(p => p.Clone()).ToList();

    /// <summary>Raised after <see cref="SetCustomAiProviders"/> persists a new list, so
    /// every open terminal tab can rebuild its AI panel's provider picker.</summary>
    public event Action? CustomAiProvidersChanged;

    public void SetCustomAiProviders(List<CustomAiProvider> providers)
    {
        _settings.Settings.CustomAiProviders = providers;
        _settings.SaveIfChanged();
        CustomAiProvidersChanged?.Invoke();
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
        // immediately, then clear the (now-stale) selection — RecordRecent
        // rebuilds the group so the just-clicked VM instance is gone anyway, and
        // clearing lets a subsequent click on the same entry re-fire this path.
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
                          or nameof(ConnectionEditorViewModel.IsRdp))
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
                if (_clipboardPath != null && PathEquals(_clipboardPath, node.FullPath))
                    _clipboardPath = newPath;
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

        // Re-apply the "cut" dimming to the source node so it survives reloads.
        if (_clipboardIsCut && _clipboardPath != null)
        {
            var cutNode = FindNode(Nodes, _clipboardPath);
            if (cutNode != null)
                cutNode.IsCut = true;
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

            // Reload with the new folder as the reveal target — this expands the
            // parent (ancestor of the new folder) so the new entry is visible.
            ReloadTree(path);

            // Then move selection back to the parent so successive "New folder"
            // clicks keep creating siblings at this level instead of nesting
            // into the just-created (empty) folder. FindNode returns null when
            // parent == root, which leaves SelectedNode null — correct behavior
            // for "keep targeting root".
            SelectedNode = FindNode(Nodes, parent);
            RequestTreeFocus(SelectedNode);

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

    private void CreateConnection(ConnectionType type)
    {
        try
        {
            var connection = new Connection
            {
                Type = type,
                Name = type == ConnectionType.Rdp ? L("NewRdpDefault") : L("NewSshDefault"),
                Port = Connection.DefaultPort(type),
            };

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
    private Task Connect()
    {
        // Make sure unsaved edits land on disk before we read the connection.
        FlushPendingAutoSave();

        if (SelectedNode is not { IsConnection: true, IsNameEditing: false, Connection: not null } node)
            return Task.CompletedTask;

        return LaunchAsync(node);
    }

    private bool CanConnect() => SelectedNode is { IsConnection: true, IsNameEditing: false };

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
        if (SelectedNode is not { IsRecent: true, IsConnection: true } shadow)
            return;

        var removedName = shadow.Name;
        var list = _settings.Settings.RecentConnectionPaths;
        var before = list.Count;
        list.RemoveAll(p => PathEquals(p, shadow.FullPath));
        if (list.Count == before)
            return;

        RebuildRecentGroupInPlace();
        SelectedNode = null;
        StatusMessage = L("StatusRemovedFromRecent", removedName);
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
    /// </summary>
    private void RebuildRecentGroupInPlace()
    {
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
    private async Task LaunchAsync(TreeNodeViewModel node)
    {
        if (node.Connection is null)
            return;

        var connection = node.Connection;

        try
        {
            // SSH always renders in the in-app terminal (SSH.NET) with programmatic
            // auth — there is no external-client path for it.
            if (connection.Type == ConnectionType.Ssh)
            {
                if (OpenSshTerminalAsync is null)
                    throw new InvalidOperationException("The in-app SSH terminal is not available.");
                StatusMessage = L("StatusLaunching", connection.Type.ToDisplayName(), connection.Host);
                await OpenSshTerminalAsync(connection, node.FullPath);
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
    /// Moves <paramref name="path"/> to the front of the most-recently-used list,
    /// trims to <see cref="RecentMax"/>, and refreshes the synthetic group.
    /// </summary>
    private void RecordRecent(string path)
    {
        var list = _settings.Settings.RecentConnectionPaths;
        list.RemoveAll(p => PathEquals(p, path));
        list.Insert(0, path);
        if (list.Count > RecentMax)
            list.RemoveRange(RecentMax, list.Count - RecentMax);

        // Rebuild just the recent group in place so the user's tree selection
        // (typically the just-launched node) survives untouched.
        RebuildRecentGroupInPlace();
    }

    // --- Delete / rename ---

    [RelayCommand(CanExecute = nameof(CanModifySelection))]
    private async Task Delete()
    {
        // Drop any pending auto-save for what's about to be deleted.
        _autoSaveTimer?.Stop();

        if (SelectedNode is not { } node)
            return;

        var what = node.IsFolder
            ? L("DialogDeleteFolderPrompt", node.Name)
            : L("DialogDeleteConnectionPrompt", node.Name);
        if (ConfirmAsync is not null)
        {
            var ok = await ConfirmAsync(L("DialogDeleteTitle"), what);
            if (!ok)
                return;
        }

        var deletedPath = node.FullPath;
        var parent = Path.GetDirectoryName(deletedPath);

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
        }
        catch (Exception ex)
        {
            StatusMessage = L("StatusCouldNotDelete", ex.Message);
            return;
        }

        // Drop a pending clipboard entry that pointed at the deleted item.
        if (_clipboardPath != null && ConnectionStore.IsSameOrInside(deletedPath, _clipboardPath))
            ClearClipboard();

        ReloadTree();
        SelectedNode = parent is not null ? FindNode(Nodes, parent) : null;
        StatusMessage = L("StatusDeleted", node.Name);
    }

    [RelayCommand(CanExecute = nameof(CanModifySelection))]
    private void Rename()
    {
        if (SelectedNode is not { } node)
            return;

        BeginNodeNameEdit(node);
    }

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

    private bool CanModifySelection() => SelectedNode is { IsRecent: false, IsNameEditing: false };

    // --- Copy / cut / paste ---

    [RelayCommand(CanExecute = nameof(CanModifySelection))]
    private void Copy()
    {
        if (SelectedNode is not { } node)
            return;

        SetClipboard(node, isCut: false);
        StatusMessage = L("StatusCopied", node.Name);
    }

    [RelayCommand(CanExecute = nameof(CanModifySelection))]
    private void Cut()
    {
        if (SelectedNode is not { } node)
            return;

        SetClipboard(node, isCut: true);
        node.IsCut = true;
        StatusMessage = L("StatusCut", node.Name);
    }

    private void SetClipboard(TreeNodeViewModel node, bool isCut)
    {
        ClearCutFlags(Nodes);
        _clipboardPath = node.FullPath;
        _clipboardIsFolder = node.IsFolder;
        _clipboardIsCut = isCut;
        HasClipboard = true;
    }

    [RelayCommand(CanExecute = nameof(CanPaste))]
    private void Paste()
    {
        if (_clipboardPath is null)
            return;

        if (_clipboardIsCut)
            FlushPendingAutoSave();

        if (_clipboardPath is null)
            return;

        var source = _clipboardPath;
        var target = TargetFolder();

        var exists = _clipboardIsFolder ? Directory.Exists(source) : File.Exists(source);
        if (!exists)
        {
            ClearClipboard();
            StatusMessage = L("StatusClipboardGone");
            return;
        }

        try
        {
            string newPath;
            if (_clipboardIsFolder)
            {
                // Guard both copy and move: pasting a folder into itself or one of
                // its own subfolders would recurse forever.
                if (ConnectionStore.IsSameOrInside(source, target))
                {
                    StatusMessage = L("StatusPasteIntoSelf");
                    return;
                }

                newPath = _clipboardIsCut
                    ? _store.MoveFolderInto(source, target)
                    : _store.CopyFolderInto(source, target);
            }
            else
            {
                newPath = _clipboardIsCut
                    ? _store.MoveFileInto(source, target)
                    : _store.CopyFileInto(source, target);
            }

            if (_clipboardIsCut)
            {
                // MoveXInto returns the original path unchanged when it's a no-op
                // (pasting back into the same folder). Keep the cut pending in that case.
                if (PathEquals(newPath, source))
                {
                    StatusMessage = L("StatusAlreadyInFolder");
                    return;
                }

                DetachEditorIfEditingPath(source);
                ClearClipboard(); // a real move consumes the cut
                ReloadTree(newPath);
                StatusMessage = L("StatusMoved");
            }
            else
            {
                ReloadTree(newPath);
                StatusMessage = L("StatusPasted");
            }
        }
        catch (Exception ex)
        {
            StatusMessage = L("StatusPasteFailed", ex.Message);
        }
    }

    private bool CanPaste() => HasClipboard && SelectedNode is not { IsNameEditing: true };

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
    }

    private void ClearClipboard()
    {
        ClearCutFlags(Nodes);
        _clipboardPath = null;
        _clipboardIsFolder = false;
        _clipboardIsCut = false;
        HasClipboard = false;
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

        if (node.Connection.Type != ConnectionType.Ssh)
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
        StatusMessage = L("StatusScriptSuiteOpened", suite.Name);
    }

    private bool CanRunSelectedScriptBinding() =>
        SelectedNode is { IsConnection: true, Connection: { Type: ConnectionType.Ssh } };

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

    [RelayCommand]
    private async Task RunScriptFile(RemoteScriptFile? scriptFile)
    {
        var context = _scriptContext;
        if (scriptFile is null
            || ScriptPanel is null
            || context?.Node is not { IsConnection: true, Connection: not null } node)
            return;

        if (ScriptPanel.IsRunning)
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
    [RelayCommand]
    private void ClearSelection() => SelectedNode = null;

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
                if (ConfirmAsync is null)
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

                FlushPendingAutoSave();
                FlushSettings();

                if (!AutoUpdateService.LaunchUpdate())
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
        var picked = await PickFolderAsync(defaultHint);
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
            _settings.Settings.UpdateCheckIntervalHours);
        if (result is null)
            return;
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

            // Custom AI provider API keys live in the roaming settings as jrm1 blobs;
            // they migrate in the same all-or-nothing sweep. Legacy plaintext keys are
            // not master-password-bound and pass through untouched.
            var aiKeys = new List<(CustomAiProvider Provider, string Clear)>();
            foreach (var provider in _settings.Settings.CustomAiProviders)
            {
                if (string.IsNullOrEmpty(provider.ApiKey) || !MasterKeyService.IsPasswordBlob(provider.ApiKey))
                    continue;
                if (current.TryDecryptPassword(provider.ApiKey, out var clearKey))
                    aiKeys.Add((provider, clearKey));
                else
                    unreadable++;
            }

            if (unreadable > 0)
                throw new InvalidOperationException(L("MasterChangeUnreadablePasswords", unreadable));

            foreach (var (provider, clearKey) in aiKeys)
                provider.ApiKey = MasterKeyService.EncryptWithPassword(newPassword, clearKey);
            if (aiKeys.Count > 0)
                _settings.SaveIfChanged();

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
