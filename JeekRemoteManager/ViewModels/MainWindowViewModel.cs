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
    private readonly RemoteScriptLauncher _scriptLauncher;

    // Internal (in-app) clipboard for copy/cut/paste of nodes.
    private string? _clipboardPath;
    private bool _clipboardIsFolder;
    private bool _clipboardIsCut;

    // Auto-save: which node the current editor is bound to, plus a debounce timer.
    private TreeNodeViewModel? _editingNode;
    private DispatcherTimer? _autoSaveTimer;
    private static readonly TimeSpan AutoSaveDelay = TimeSpan.FromMilliseconds(600);

    // Watches the connections folder so external changes show up live. Reloads
    // are debounced, and changes the app made itself are ignored.
    private FileSystemWatcher? _watcher;
    private DispatcherTimer? _watchReloadTimer;
    private static readonly TimeSpan WatchReloadDelay = TimeSpan.FromMilliseconds(400);
    private const long SelfWriteSuppressMs = 1000;

    // Synthetic node showing the last-used connections at the top of the tree.
    private const int RecentMax = 10;
    private const string RecentSentinelPath = "<recent>";
    private TreeNodeViewModel? _recentGroup;

    public MainWindowViewModel(
        ConnectionStore store,
        ConnectionLauncher launcher,
        SettingsService settings,
        RemoteScriptStore? scriptStore = null,
        RemoteScriptLauncher? scriptLauncher = null)
    {
        _store = store;
        _launcher = launcher;
        _settings = settings;
        _scriptStore = scriptStore ?? new RemoteScriptStore();
        _scriptLauncher = scriptLauncher ?? new RemoteScriptLauncher();
        _store.SetRoot(_settings.ResolveConnectionsRoot());
        _scriptStore.SetRoot(_settings.ResolveScriptsRoot());
        ReloadScripts();
        ReloadTree(_settings.Settings.LastSelectedConnectionPath);
        StartWatching(_store.RootPath);

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

    public bool ShowConnectionEditor => Editor is not null && !HasScriptExecution;

    public bool ShowPlaceholder => Editor is null && !HasScriptExecution;

    // Wired up by the view so the VM can reach platform services without a
    // hard dependency on the window.
    public IClipboard? Clipboard { get; set; }
    public Func<string, string, Task<bool>>? ConfirmAsync { get; set; }
    public Func<string, string, string, Task<string?>>? PromptAsync { get; set; }
    public Func<Task<string?>>? PickKeyFileAsync { get; set; }
    public Func<StorageLocation, string?, string?, bool, int, Task<SettingsDialogResult?>>? PickSettingsAsync { get; set; }
    public Func<string, Task<string?>>? PickFolderAsync { get; set; }

    /// <summary>Asks the view to put keyboard focus on the tree so it receives shortcuts.</summary>
    public Action? RequestFocusTree { get; set; }

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
        _settings.Save();
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
        }
        catch (Exception ex)
        {
            StatusMessage = L("StatusAutoSaveFailed", ex.Message);
        }
    }

    // --- Watching the connections folder ---

    /// <summary>(Re)starts the file-system watcher on the given root folder.</summary>
    private void StartWatching(string path)
    {
        StopWatching();

        if (!Directory.Exists(path))
            return;

        try
        {
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
    }

    // Events arrive on a background thread; hop to the UI thread and debounce.
    private void OnWatchedChange(object? sender, FileSystemEventArgs e) =>
        Dispatcher.UIThread.Post(ScheduleWatchReload);

    private void ScheduleWatchReload()
    {
        if (_watchReloadTimer is null)
        {
            _watchReloadTimer = new DispatcherTimer { Interval = WatchReloadDelay };
            _watchReloadTimer.Tick += (_, _) =>
            {
                _watchReloadTimer!.Stop();

                // Ignore the burst of events caused by the app's own writes.
                if (Environment.TickCount64 - _store.LastWriteTick < SelfWriteSuppressMs)
                    return;

                // Don't clobber an in-progress edit; flush it first so the reload
                // reflects the user's latest changes too.
                FlushPendingAutoSave();
                ReloadTree();
            };
        }

        _watchReloadTimer.Stop();
        _watchReloadTimer.Start();
    }

    // --- Tree building ---

    private void ReloadTree(string? pathToSelect = null)
    {
        // Preserve expand/collapse state and the selection across the rebuild.
        var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CaptureExpanded(Nodes, expanded);
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

        RestoreExpanded(Nodes, expanded);

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
                if (pathToSelect != null)
                    RequestFocusTree?.Invoke();
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
        var pruned = false;

        foreach (var path in paths.ToArray())
        {
            if (!File.Exists(path))
            {
                paths.RemoveAll(p => PathEquals(p, path));
                pruned = true;
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
                pruned = true;
                continue;
            }

            children.Add(new TreeNodeViewModel(path, isFolder: false, connection)
            {
                IsRecent = true,
            });
        }

        if (pruned)
            _settings.Save();

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
            _settings.Save();
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

    private static void CaptureExpanded(IEnumerable<TreeNodeViewModel> nodes, HashSet<string> expanded)
    {
        foreach (var node in nodes)
        {
            if (node.IsRecent) continue; // tracked via AppSettings.RecentExpanded
            if (node.IsFolder && node.IsExpanded)
                expanded.Add(Path.GetFullPath(node.FullPath));
            CaptureExpanded(node.Children, expanded);
        }
    }

    private static void RestoreExpanded(IEnumerable<TreeNodeViewModel> nodes, HashSet<string> expanded)
    {
        foreach (var node in nodes)
        {
            if (node.IsRecent) continue;
            if (node.IsFolder)
                node.IsExpanded = expanded.Contains(Path.GetFullPath(node.FullPath));
            RestoreExpanded(node.Children, expanded);
        }
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

    public void SaveLastSelectedConnection()
    {
        FlushPendingAutoSave();
        SaveLastSelectedConnectionPath(SelectedNode);
    }

    private void SaveLastSelectedConnectionPath(TreeNodeViewModel? node)
    {
        var path = node is { IsRecent: false, IsConnection: true } ? node.FullPath : null;
        if (NullablePathEquals(_settings.Settings.LastSelectedConnectionPath, path))
            return;

        _settings.Settings.LastSelectedConnectionPath = path;
        _settings.Save();
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
            RequestFocusTree?.Invoke();

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
            StatusMessage = L("StatusCreatedConnection", type);
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

        if (SelectedNode is not { IsConnection: true, Connection: not null } node)
            return Task.CompletedTask;

        return LaunchAsync(node);
    }

    private bool CanConnect() => SelectedNode is { IsConnection: true };

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
        RequestFocusTree?.Invoke();
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

        _settings.Save();
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
        _settings.Save();
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
            // ssh.exe cannot take a password on the command line, so copy it to
            // the clipboard as a convenience for pasting at the prompt. The
            // clipboard is auto-cleared after a short delay to limit exposure.
            if (connection.Type == ConnectionType.Ssh && Clipboard is not null)
            {
                var password = PasswordProtector.Decrypt(connection.EncryptedPassword);
                if (!string.IsNullOrEmpty(password))
                {
                    await Clipboard.SetTextAsync(password);
                    ScheduleClipboardClear(password);
                    StatusMessage = L("StatusLaunchingSshWithClipboard", connection.Host);
                }
                else
                {
                    StatusMessage = L("StatusLaunchingSsh", connection.Host);
                }
            }
            else
            {
                StatusMessage = L("StatusLaunching", connection.Type, connection.Host);
            }

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
        _settings.Save();

        // Rebuild just the recent group in place so the user's tree selection
        // (typically the just-launched node) survives untouched.
        RebuildRecentGroupInPlace();
    }

    /// <summary>Clears the OS clipboard after a delay, but only if it still holds the secret we put there.</summary>
    private void ScheduleClipboardClear(string secret)
    {
        var clipboard = Clipboard;
        if (clipboard is null)
            return;

        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                try
                {
                    var current = await clipboard.TryGetTextAsync();
                    if (current == secret)
                        await clipboard.ClearAsync();
                }
                catch
                {
                    // Best-effort; ignore clipboard failures.
                }
            });
        });
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
    private async Task Rename()
    {
        if (SelectedNode is not { } node || PromptAsync is null)
            return;

        FlushPendingAutoSave();

        var newName = await PromptAsync(L("DialogRenameTitle"), L("DialogRenamePrompt"), node.Name);
        if (string.IsNullOrWhiteSpace(newName) || newName == node.Name)
            return;

        var oldPath = node.FullPath;

        try
        {
            string newPath;
            if (node.IsFolder)
            {
                newPath = _store.RenameFolder(node.FullPath, newName);
            }
            else if (node.Connection is not null)
            {
                node.Connection.Name = newName;
                var folder = Path.GetDirectoryName(node.FullPath) ?? _store.RootPath;
                newPath = _store.Save(node.Connection, folder, node.FullPath);
            }
            else
            {
                return;
            }

            DetachEditorIfEditingPath(oldPath);
            ReloadTree(newPath);
            StatusMessage = L("StatusRenamed");
        }
        catch (Exception ex)
        {
            StatusMessage = L("StatusCouldNotRename", ex.Message);
        }
    }

    private bool CanModifySelection() => SelectedNode is { IsRecent: false };

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

    private bool CanPaste() => HasClipboard;

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
        return BuildScriptSuiteChoices(node.Connection);
    }

    public void OpenScriptSuiteChoice(ScriptSuiteChoiceViewModel? choice)
    {
        if (choice is null)
            return;
        if (SelectedNode is not { IsConnection: true, Connection: not null } node)
            return;

        var suite = choice.Suite;
        var binding = node.Connection.ScriptBindings.LastOrDefault(b =>
            string.Equals(b.Name, suite.RelativePath, StringComparison.OrdinalIgnoreCase));
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
                .Select(b => b.Name),
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
        if (scriptFile is null
            || ScriptPanel is null
            || SelectedNode is not { IsConnection: true, Connection: not null } node)
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
        panel.Output = "";
        panel.ClearExecutionResult();
        panel.StatusText = L("ScriptExecutionRunning");
        panel.IsRunning = true;
        StatusMessage = L("StatusScriptRunning", displayName, node.Connection.Name);

        try
        {
            var result = await _scriptLauncher.RunAsync(
                node.Connection,
                panel.Suite,
                scriptFile,
                binding,
                text => Dispatcher.UIThread.Post(() => panel.AppendOutput(text)));

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
            || Editor is null
            || SelectedNode is not { IsConnection: true, Connection: not null } node)
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
        UpsertScriptBinding(Editor.ScriptBindings, protectedBinding);
        UpsertScriptBinding(node.Connection.ScriptBindings, protectedBinding);
        if (flushImmediately)
            FlushPendingAutoSave();
        else
            ScheduleAutoSave();
        RunSelectedScriptBindingCommand.NotifyCanExecuteChanged();
        return protectedBinding;
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
            values.TryGetValue(parameter.Name, out var value);
            result[parameter.Name] = NormalizeComparableScriptParam(parameter, value ?? "");
        }

        return result;
    }

    private static bool HasMeaningfulScriptParams(RemoteScriptSuite suite, ConnectionScriptBinding binding) =>
        ComparableScriptParams(suite, binding).Any(item => !IsDefaultScriptParamValue(suite, item.Key, item.Value));

    private static bool IsDefaultScriptParamValue(RemoteScriptSuite suite, string name, string value)
    {
        var parameter = suite.Parameters.FirstOrDefault(p =>
            string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        return parameter?.Type == RemoteScriptParameterType.Bool
            ? !string.Equals(value, "true", StringComparison.Ordinal)
            : string.IsNullOrEmpty(value);
    }

    private static string NormalizeComparableScriptParam(RemoteScriptParameter parameter, string value)
    {
        if (parameter.Type != RemoteScriptParameterType.Bool)
            return value;

        return value.Trim().ToLowerInvariant() switch
        {
            "true" or "1" or "yes" or "y" => "true",
            _ => "false",
        };
    }

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
            || Editor is null
            || SelectedNode is not { IsConnection: true, Connection: not null } node)
            return;

        if (ScriptPanel.IsRunning)
        {
            StatusMessage = L("StatusScriptStillRunning");
            return;
        }

        var suitePath = ScriptPanel.Suite.RelativePath;
        ScriptPanel.ClearParameters();
        RemoveScriptBinding(Editor.ScriptBindings, suitePath);
        RemoveScriptBinding(node.Connection.ScriptBindings, suitePath);
        ScheduleAutoSave();
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
            _settings.Save();

            var system = System.Globalization.CultureInfo.CurrentCulture.TwoLetterISOLanguageName;
            Localizer.Language = Localizer.Languages.Contains(system) ? system : "en";
            return;
        }

        if (!Localizer.Languages.Contains(language))
            return;

        Localizer.Language = language;
        _settings.Settings.Language = language;
        _settings.Save();
    }

    private void ApplyTheme(string? theme)
    {
        // Empty / null means "follow system": clear the stored preference and
        // let Avalonia resolve the variant from the OS theme.
        _settings.Settings.Theme = string.IsNullOrEmpty(theme) ? null : theme;
        _settings.Save();

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

        var current = _settings.Settings.StorageLocation;
        var result = await PickSettingsAsync(
            current,
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
            _settings.Save();
            if (intervalChanged)
                _updateIntervalChanged.Cancel();
        }

        if (result.StorageLocation == current)
            return;

        var oldRoot = _store.RootPath;
        var newRoot = SettingsService.ResolveConnectionsRoot(result.StorageLocation);
        var oldScriptsRoot = _scriptStore.RootPath;
        var newScriptsRoot = SettingsService.ResolveScriptsRoot(result.StorageLocation);

        // Make sure the target is actually writable (e.g. ProgramDirectory under
        // %ProgramFiles% is not, for a standard user) before committing.
        if (!TryEnsureWritable(newRoot) || !TryEnsureWritable(newScriptsRoot))
        {
            StatusMessage = L("StatusNotWritable", newRoot);
            return;
        }

        if (ConfirmAsync is not null)
        {
            var ok = await ConfirmAsync(
                L("DialogCopyDataTitle"),
                L("DialogCopyDataMessage", oldRoot, newRoot));
            if (!ok)
                return;
        }

        try
        {
            _store.CopyTreeContents(oldRoot, newRoot);
            _scriptStore.CopyTreeContents(oldScriptsRoot, newScriptsRoot);
        }
        catch (Exception ex)
        {
            StatusMessage = L("StatusStorageCopyFailed", ex.Message);
            return;
        }

        _settings.Settings.StorageLocation = result.StorageLocation;
        var saved = _settings.Save();
        _store.SetRoot(newRoot);
        _scriptStore.SetRoot(newScriptsRoot);
        ReloadScripts();
        StartWatching(newRoot);
        ClearClipboard();
        ReloadTree();
        OnPropertyChanged(nameof(RootPath));
        OnPropertyChanged(nameof(TargetDescription));

        if (!saved)
            StatusMessage = L("StatusStorageNotSaved", _settings.SettingsPath);
        else if (HasData(newRoot))
            StatusMessage = L("StatusStorageLocationWithData", result.StorageLocation, newRoot);
        else
            StatusMessage = L("StatusStorageLocationOnly", result.StorageLocation, newRoot);
    }

    /// <summary>
    /// Switches the master password by re-encrypting every connection file: each
    /// password is decrypted with the current master password and re-encrypted
    /// as a fresh self-contained jrm1 blob under <paramref name="newPassword"/>.
    /// Connections we cannot decrypt are left untouched. The new password
    /// replaces the cached one only after the sweep, so an interruption never
    /// leaves us with files that no password in memory can read.
    /// </summary>
    public void ChangeMasterPassword(string newPassword)
    {
        try
        {
            FlushPendingAutoSave();

            var current = MasterKeyService.Current
                          ?? throw new InvalidOperationException("Master password not initialised.");

            var pending = new List<(string File, Connection Connection, string ClearPassword)>();
            var unreadable = 0;

            foreach (var file in _store.AllConnectionFiles())
            {
                try
                {
                    var c = _store.Load(file);
                    if (string.IsNullOrEmpty(c.EncryptedPassword))
                        continue;

                    if (!current.TryDecryptPassword(c.EncryptedPassword, out var clear))
                    {
                        unreadable++;
                        continue;
                    }

                    pending.Add((file, c, clear));
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
                item.Connection.EncryptedPassword =
                    MasterKeyService.EncryptWithPassword(newPassword, item.ClearPassword);
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

    private static bool TryEnsureWritable(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var probe = Path.Combine(folder, ".write_test_" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
