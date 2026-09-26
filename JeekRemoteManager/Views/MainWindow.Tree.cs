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

/// <summary>Connection tree interaction: keyboard navigation, focus restore, inline rename, drag and drop.</summary>
public partial class MainWindow
{
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
            // Ctrl+Enter opens another shell session; plain Enter reuses an open tab.
            var command = e.KeyModifiers.HasFlag(KeyModifiers.Control)
                ? (System.Windows.Input.ICommand)vm.ConnectNewSessionCommand
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
            // Ctrl+double-click opens another shell session; a plain double-click
            // reuses an open tab.
            var command = e.KeyModifiers.HasFlag(KeyModifiers.Control)
                ? (System.Windows.Input.ICommand)vm.ConnectNewSessionCommand
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
}
