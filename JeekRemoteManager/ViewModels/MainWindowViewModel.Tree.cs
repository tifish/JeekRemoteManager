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

/// <summary>Building the connection tree: reloads, the Recent group, expansion state and lookups.</summary>
public partial class MainWindowViewModel
{
    // --- Tree building ---

    /// <summary>Reloads the tree from disk after an external change (e.g. the product MCP
    /// surface creating or updating a connection file).</summary>
    public void ReloadTreeFromDisk(string? pathToSelect = null) =>
        ReloadTree(pathToSelect, requestFocus: false);

    /// <summary>Counts full tree rebuilds, so the Debug MCP can catch a write that
    /// reloads twice because the file watcher did not recognise it as ours.</summary>
    internal long TreeReloadCountForDebug { get; private set; }

    /// <summary>Watcher debounces that found the disk already matching the tree (the
    /// app's own writes), exposed for the Debug MCP.</summary>
    internal long WatcherReloadsSkippedForDebug { get; private set; }

    /// <summary>
    /// True when the connections folder no longer matches what the tree was built from.
    /// The fingerprint is metadata-only and runs off the UI thread; an unknown fingerprint
    /// (a read raced a change, a write failed) always counts as a difference.
    /// </summary>
    private async Task<bool> TreeDiffersFromDiskAsync()
    {
        if (_store.KnownSignature is null)
            return true;

        string current;
        try
        {
            current = await Task.Run(_store.ComputeSignature).ConfigureAwait(true);
        }
        catch
        {
            return true;
        }

        if (current != _store.KnownSignature)
            return true;

        WatcherReloadsSkippedForDebug++;
        return false;
    }

    /// <summary>Debug MCP only: whether the tree currently shows a node for this path.</summary>
    internal bool DebugTreeContains(string fullPath) => FindNode(Nodes, fullPath) is not null;

    private async Task ReloadTreeIfChangedAsync()
    {
        if (await TreeDiffersFromDiskAsync())
            await ReloadTreeAsync();
    }

    /// <summary>
    /// Ticket handed out when a reload starts. Background reads are started
    /// fire-and-forget by the file watcher and take wildly different times on a network
    /// or file-synced folder, so a read applies its snapshot only while it is still the
    /// most recently *started* one. Ordering by start rather than by completion is what
    /// matters: whichever read began last saw the newest state on disk, and letting any
    /// other one win puts deleted or renamed nodes back until the next refresh.
    ///
    /// Only ever touched on the UI thread — every reload starts there, and the ticket is
    /// taken before the first await.
    /// </summary>
    private long _treeReloadRequestId;

    /// <summary>Background reads whose snapshot was dropped because a newer reload had
    /// started. Exposed so the Debug MCP can prove the ordering guard actually fires.</summary>
    internal long StaleTreeReloadsDiscardedForDebug { get; private set; }

    /// <summary>Debug MCP only: stands in for the off-thread tree read, so a probe can
    /// control how long each read takes and therefore what order they finish in.</summary>
    internal Func<ConnectionFolderSnapshot>? TreeReadOverrideForDebug { get; set; }

    /// <summary>Debug MCP only: starts a background reload exactly as the file watcher does.</summary>
    internal Task DebugReloadTreeInBackgroundAsync() => ReloadTreeAsync(requestFocus: false);

    /// <summary>
    /// Reads the tree off the UI thread, then rebuilds the nodes on it. Used wherever the
    /// reload is not the direct result of a user action — startup and file-watcher
    /// changes — because reading every connection inline freezes the window, and the
    /// connections folder is often on a network or file-synced drive.
    /// </summary>
    private async Task ReloadTreeAsync(string? pathToSelect = null, bool requestFocus = true)
    {
        var requestId = ++_treeReloadRequestId;
        var read = TreeReadOverrideForDebug ?? _store.ReadTree;
        ConnectionFolderSnapshot snapshot;
        try
        {
            snapshot = await Task.Run(read).ConfigureAwait(true);
        }
        catch
        {
            // The root went away mid-read (storage location change, unmounted share).
            // The synchronous path handles a missing root by producing an empty tree.
            if (requestId == _treeReloadRequestId)
                ReloadTree(pathToSelect, requestFocus);
            else
                StaleTreeReloadsDiscardedForDebug++;
            return;
        }

        if (requestId != _treeReloadRequestId)
        {
            StaleTreeReloadsDiscardedForDebug++;
            return;
        }

        ReloadTree(pathToSelect, requestFocus, snapshot);
    }

    private void ReloadTree(
        string? pathToSelect = null,
        bool requestFocus = true,
        ConnectionFolderSnapshot? snapshot = null)
    {
        TreeReloadCountForDebug++;
        // Supersede any background read still in flight: this one reads now, so its
        // view of the folder is at least as new as anything already running.
        _treeReloadRequestId++;
        snapshot ??= _store.ReadTree();
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

        foreach (var child in BuildChildren(snapshot, parent: null))
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

    /// <summary>Reloads user and built-in scripts after an external or MCP write.</summary>
    public void ReloadScriptsFromDisk() => ReloadScripts();

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
            _settings.SaveIfChanged();
        };

        return group;
    }

    /// <summary>Turns an already-read folder snapshot into tree nodes. Pure in-memory
    /// work, so it stays on the UI thread where the nodes are bound.</summary>
    private ObservableCollection<TreeNodeViewModel> BuildChildren(
        ConnectionFolderSnapshot folder,
        TreeNodeViewModel? parent)
    {
        var result = new ObservableCollection<TreeNodeViewModel>();

        foreach (var directory in folder.Folders)
        {
            var node = new TreeNodeViewModel(directory.Path, isFolder: true) { Parent = parent };
            foreach (var child in BuildChildren(directory, node))
                node.Children.Add(child);
            ApplyPersistedExpansion(node);
            result.Add(node);
        }

        foreach (var (file, connection) in folder.Connections)
            result.Add(new TreeNodeViewModel(file, isFolder: false, connection) { Parent = parent });

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

    /// <summary>
    /// Reveals and selects a tree node by file path, which opens its editor. Used by the
    /// product MCP surface to hand a task back to the user in the GUI (setting a password)
    /// instead of accepting the secret over the tool channel.
    /// </summary>
    public bool SelectNodeByPath(string fullPath)
    {
        if (FindNode(Nodes, fullPath) is not { } node)
            return false;

        ExpandAncestors(node);
        SelectedNode = node;
        return true;
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
}
