using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Jeek.Avalonia.Localization;
using JeekRemoteManager.Services;
using JeekRemoteManager.ViewModels;

namespace JeekRemoteManager.Views;

public partial class MainWindow
{
    private bool TryFindJumpHostDropTarget(Point position, out Border zone, out ConnectionEditorViewModel editor)
    {
        zone = null!;
        editor = null!;
        var hit = this.InputHitTest(position) as Visual;
        var target = hit?.GetVisualAncestors().Prepend(hit).OfType<Border>()
            .FirstOrDefault(border => Equals(border.Tag, "jump-host-drop"));
        if (target?.DataContext is not ConnectionEditorViewModel targetEditor || !target.IsEffectivelyVisible)
            return false;
        zone = target;
        editor = targetEditor;
        return true;
    }

    private string ValidateJumpHostNodes(ConnectionEditorViewModel editor, IReadOnlyList<TreeNodeViewModel> sources, out string path)
    {
        path = "";
        if (sources.Count != 1)
            return Localizer.Get("JumpHostDropSingle");
        if (sources[0] is not { IsRecent: false, Connection.IsSsh: true } source
            || DataContext is not MainWindowViewModel vm)
            return Localizer.Get("JumpHostDropSshOnly");
        var relative = Path.GetRelativePath(vm.RootPath, source.FullPath);
        if (!relative.EndsWith(ConnectionStore.FileExtension, System.StringComparison.OrdinalIgnoreCase))
            return Localizer.Get("JumpHostDropSshOnly");
        path = relative[..^ConnectionStore.FileExtension.Length].Replace('\\', '/');
        return editor.ValidateJumpHostDrop(path);
    }

    private bool TryDropJumpHost(IReadOnlyList<TreeNodeViewModel> sources, Point position)
    {
        if (!TryFindJumpHostDropTarget(position, out _, out var editor))
            return false;
        var error = ValidateJumpHostNodes(editor, sources, out var path);
        if (error.Length > 0)
            editor.ReportJumpHostDropError(error);
        else
            editor.TrySetJumpHostFromTree(path);
        return true;
    }

    private void SetJumpHostDropTarget(Border? zone)
    {
        if (ReferenceEquals(_jumpHostDropTarget, zone))
            return;
        if (_jumpHostDropTarget is { } previous)
        {
            previous.Classes.Remove("drop-valid");
            previous.Classes.Remove("drop-invalid");
            previous.Cursor = Cursor.Default;
        }
        _jumpHostDropTarget = zone;
    }

    private void OnClearJumpHostClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ConnectionEditorViewModel editor })
        {
            editor.ReportJumpHostDropError("");
            editor.JumpHost = "";
        }
    }

    /// <summary>Drives the real routed pointer handlers in an isolated Debug MCP window.</summary>
    internal (bool EditorPreserved, bool ValidHighlight, bool InvalidHighlight) DebugDragTreeNode(
        TreeNodeViewModel source, Point destination, bool move = true, bool expander = false)
    {
        var item = Tree.GetVisualDescendants().OfType<TreeViewItem>()
            .First(control => ReferenceEquals(control.DataContext, source));
        var eventSource = expander ? (Control)item.GetVisualDescendants().OfType<ToggleButton>().First() : item;
        var start = eventSource.TranslatePoint(new Point(12, eventSource.Bounds.Height / 2), this)!.Value;
        using var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
        var pressed = new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed);
        var editor = (DataContext as MainWindowViewModel)?.Editor;
        eventSource.RaiseEvent(new PointerPressedEventArgs(eventSource, pointer, this, start, 1, pressed, KeyModifiers.None, 1));
        var preserved = ReferenceEquals((DataContext as MainWindowViewModel)?.Editor, editor);
        if (move)
            Tree.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, Tree, pointer, this,
                destination, 2, pressed, KeyModifiers.None));
        var valid = _jumpHostDropTarget?.Classes.Contains("drop-valid") == true;
        var invalid = _jumpHostDropTarget?.Classes.Contains("drop-invalid") == true;
        Tree.RaiseEvent(new PointerReleasedEventArgs(Tree, pointer, this, move ? destination : start, 3,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased),
            KeyModifiers.None, MouseButton.Left));
        return (preserved, valid, invalid);
    }

    internal Point DebugTreeRootDropPoint => Tree.TranslatePoint(new Point(24, Tree.Bounds.Height - 12), this)!.Value;
}
