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

/// <summary>Tree commands: create, connect, recent, delete, rename, copy/cut/paste and key picking.</summary>
public partial class MainWindowViewModel
{
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

    [RelayCommand]
    private void NewVnc() => CreateConnection(ConnectionType.Vnc);

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
                    ConnectionType.Vnc => L("NewVncDefault"),
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

    /// <summary>Opens another shell session, reusing an authenticated TCP transport
    /// when possible. Plain Connect activates an existing tab instead.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenNewSession))]
    private async Task ConnectNewSession()
    {
        FlushPendingAutoSave();

        if (SelectedNode is not { IsConnection: true, IsNameEditing: false, Connection: not null } node)
            return;

        var clearStaleRecentSelection = node.IsRecent;
        await LaunchAsync(node, ConnectionLaunchMode.NewSession);

        if (clearStaleRecentSelection && ReferenceEquals(SelectedNode, node))
            SelectedNode = null;
    }

    /// <summary>Opens another tab and always dials a distinct TCP connection.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenNewTcpConnection))]
    private async Task ConnectNewTcpConnection()
    {
        FlushPendingAutoSave();

        if (SelectedNode is not { IsConnection: true, IsNameEditing: false, Connection: not null } node)
            return;

        var clearStaleRecentSelection = node.IsRecent;
        await LaunchAsync(node, ConnectionLaunchMode.NewTcpConnection);

        if (clearStaleRecentSelection && ReferenceEquals(SelectedNode, node))
            SelectedNode = null;
    }

    private bool CanOpenNewSession() =>
        SelectedNode is
        {
            IsConnection: true,
            IsNameEditing: false,
            Connection.Type: ConnectionType.Ssh or ConnectionType.Wsl,
        };

    private bool CanOpenNewTcpConnection() =>
        SelectedNode is
        {
            IsConnection: true,
            IsNameEditing: false,
            Connection.Type: ConnectionType.Ssh,
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

        _settings.SaveIfChanged();
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
        _settings.SaveIfChanged();
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
    private async Task LaunchAsync(
        TreeNodeViewModel node,
        ConnectionLaunchMode mode = ConnectionLaunchMode.Connect)
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
                var open = mode switch
                {
                    ConnectionLaunchMode.NewSession => OpenNewSshSessionAsync,
                    ConnectionLaunchMode.NewTcpConnection => OpenNewSshTcpConnectionAsync,
                    _ => OpenSshTerminalAsync,
                };
                if (open is null)
                    throw new InvalidOperationException("The in-app terminal is not available.");
                StatusMessage = L("StatusLaunching", connection.Type.ToDisplayName(), connection.TargetLabel);
                await open(connection, node.FullPath);
                RecordRecent(node.FullPath);
                return;
            }

            if (await LaunchExternalAsync(connection))
                RecordRecent(node.FullPath);
        }
        catch (Exception ex)
        {
            StatusMessage = L("StatusFailedToLaunch", ex.Message);
        }
    }

    /// <summary>
    /// Opens an RDP (mstsc) or VNC (TigerVNC) connection in its external client. Returns
    /// false when the user declined to install a missing VNC viewer or its install failed.
    /// </summary>
    public async Task<bool> LaunchExternalAsync(Connection connection)
    {
        if (connection.IsVnc)
            return await LaunchVncAsync(connection);

        StatusMessage = L("StatusLaunching", connection.Type.ToDisplayName(), connection.Host);
        _launcher.Launch(connection);
        return true;
    }

    /// <summary>For callers with no status handling of their own (product MCP): failures land in the status bar.</summary>
    internal async Task LaunchExternalReportingErrorsAsync(Connection connection)
    {
        try
        {
            await LaunchExternalAsync(connection);
        }
        catch (Exception ex)
        {
            StatusMessage = L("StatusFailedToLaunch", ex.Message);
        }
    }

    private async Task<bool> LaunchVncAsync(Connection connection)
    {
        // Report an unusable connection before offering to install anything for it.
        if (string.IsNullOrWhiteSpace(connection.Host))
            throw new InvalidOperationException("The VNC connection has no host.");

        var viewer = VncViewer.Locate() ?? await InstallVncViewerAsync();
        if (viewer is null)
            return false;

        StatusMessage = L("StatusLaunching", connection.Type.ToDisplayName(), connection.Host);
        await VncViewer.LaunchAsync(
            connection,
            viewer,
            new SshDialOptions(OnMismatch: ConfirmHostKeyReplacement, PromptUser: PromptUser),
            _store.TryLoadByTreePath);
        return true;
    }

    private Task<string?>? _vncViewerInstall;

    /// <summary>
    /// Asks before installing TigerVNC through winget, then waits for it. Launches that
    /// arrive while one install is pending share it rather than asking again.
    /// </summary>
    private async Task<string?> InstallVncViewerAsync()
    {
        if (_vncViewerInstall is { } pending)
            return await pending;

        var install = InstallVncViewerCoreAsync();
        _vncViewerInstall = install;
        try
        {
            return await install;
        }
        finally
        {
            if (ReferenceEquals(_vncViewerInstall, install))
                _vncViewerInstall = null;
        }
    }

    private async Task<string?> InstallVncViewerCoreAsync()
    {
        // Installing software is the user's call: without a way to ask, do not install.
        if (ConfirmAsync is null
            || !await ConfirmAsync(
                L("DialogInstallVncViewerTitle"),
                L("DialogInstallVncViewerMessage", VncViewer.InstallCommand)))
        {
            StatusMessage = L("StatusVncViewerMissing");
            return null;
        }

        StatusMessage = L("StatusInstallingVncViewer");
        try
        {
            var viewer = await VncViewer.InstallAsync();
            StatusMessage = L("StatusVncViewerInstalled", viewer);
            return viewer;
        }
        catch (Exception ex)
        {
            StatusMessage = L("StatusVncViewerInstallFailed", ex.Message);
            return null;
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

        // Persist immediately; waiting for window close loses the list when the
        // process is killed or the OS shuts down.
        _settings.SaveIfChanged();
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
        ConnectNewSessionCommand.NotifyCanExecuteChanged();
        ConnectNewTcpConnectionCommand.NotifyCanExecuteChanged();
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
}
