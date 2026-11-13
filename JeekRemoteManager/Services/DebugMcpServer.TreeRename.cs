using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using JeekRemoteManager.Models;
using JeekRemoteManager.ViewModels;
using JeekRemoteManager.Views;
using JeekTools;

namespace JeekRemoteManager.Services;

internal static partial class DebugMcpServer
{
    private static async Task<System.Text.Json.Nodes.JsonObject> ConnectionTreeRenameCheckAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "jrm-tree-rename-" + Guid.NewGuid().ToString("N"));
        var state = await OnUiAsync(() =>
        {
            var settings = new SettingsService(Path.Combine(root, "machine.json"), Path.Combine(root, "roaming.json"));
            settings.Settings.StorageLocation = StorageLocation.CustomDirectory;
            settings.Settings.CustomStoragePath = root;
            var store = new ConnectionStore(settings.ResolveConnectionsRoot());
            var folder = store.CreateFolder(store.RootPath, "group");
            store.Save(new Connection { Name = "child", Host = "child.invalid" }, folder);
            store.Save(new Connection { Name = "target", Host = "target.invalid" }, store.RootPath);
            var vm = new MainWindowViewModel(store, new ConnectionLauncher(), settings);
            var window = new MainWindow { DataContext = vm, Width = 1150, Height = 950, ShowActivated = false, ShowInTaskbar = false };
            window.Show();
            vm.SelectedNode = vm.Nodes.Single(node => node.Name == "target");
            window.FindControl<TabItem>("EditorTab")!.IsSelected = true;
            return (Vm: vm, Window: window, Store: store);
        });
        var checks = new Dictionary<string, bool>();
        var diagnostics = new List<string>();
        TreeNodeViewModel Folder() => state.Vm.Nodes.Single(node => node.IsFolder && !node.IsRecent);
        TextBox NameEditor() => state.Window.GetVisualDescendants().OfType<TextBox>()
            .Single(box => box.Classes.Contains("tree-name-editor") && ReferenceEquals(box.DataContext, Folder()));
        void Check(string name, bool passed) => checks[name] = passed;
        void FocusConnectionEditor()
        {
            state.Vm.SelectedNode = state.Vm.Nodes.Single(node => node.Name == "target");
            state.Window.FindControl<TabItem>("EditorTab")!.IsSelected = true;
            var box = state.Window.GetVisualDescendants().OfType<TextBox>()
                .First(control => control.IsEffectivelyVisible && !control.Classes.Contains("tree-name-editor"));
            Check("editorFocusEstablished", box.Focus());
        }
        try
        {
            await Task.Delay(150);
            await OnUiAsync(() =>
            {
                FocusConnectionEditor();
                var editor = state.Vm.Editor;
                var folder = Folder();
                var expanded = folder.IsExpanded;
                state.Window.DebugDragTreeNode(folder, default, move: false, expander: true);
                Check("expanderPreservesEditor", ReferenceEquals(editor, state.Vm.Editor)
                    && state.Vm.SelectedNode?.Name == "target" && folder.IsExpanded != expanded);
                FocusConnectionEditor();
                return true;
            });
            await Task.Delay(80);
            await OnUiAsync(() =>
            {
                // No dispatcher turn between the click and F2: the click must focus immediately.
                state.Window.DebugDragTreeNode(Folder(), default, move: false);
                Check("rowSelectsFolder", ReferenceEquals(state.Vm.SelectedNode, Folder()));
                diagnostics.Add(state.Window.DebugRaiseFocusedTreeKey(Key.F2));
                Check("f2AfterEditorFocus", Folder().IsNameEditing);
                return true;
            });
            await Task.Delay(80);
            await OnUiAsync(() =>
            {
                Check("inlineEditorFocused", Folder().IsNameEditing && NameEditor().IsFocused);
                NameEditor().Text = "cancelled";
                state.Window.DebugRaiseFocusedTreeKey(Key.Escape);
                Check("escapeCancels", !Folder().IsNameEditing && Folder().Name == "group"
                    && Path.GetFileName(Directory.GetDirectories(state.Store.RootPath).Single()) == "group");
                return true;
            });
            await Task.Delay(80);
            foreach (var name in new[] { "GROUP", "Group", "renamed", "group" })
            {
                await OnUiAsync(() =>
                {
                    // Leave a previous action's deferred focus restore queued when F2 starts.
                    state.Vm.RequestFocusTreeNode?.Invoke(Folder());
                    state.Window.DebugRaiseFocusedTreeKey(Key.F2);
                    Check(name + "F2Starts", Folder().IsNameEditing);
                    return true;
                });
                await Task.Delay(80);
                await OnUiAsync(() =>
                {
                    Check(name + "QueuedFocusPreservesEditor", Folder().IsNameEditing && NameEditor().IsFocused);
                    NameEditor().Text = name;
                    state.Window.DebugRaiseFocusedTreeKey(Key.Enter);
                    Check(name + "Committed", !Folder().IsNameEditing && Folder().Name == name
                        && Path.GetFileName(Directory.GetDirectories(state.Store.RootPath).Single()) == name);
                    Check(name + "ChildPreserved", File.Exists(Path.Combine(Folder().FullPath, "child.json")));
                    return true;
                });
                await Task.Delay(100);
            }
            var passed = checks.Values.All(value => value);
            return ToolText($"{(passed ? "PASS" : "FAIL")}: connection tree rename\n"
                + string.Join("\n", checks.Select(pair => $"{pair.Key}={pair.Value}"))
                + "\n" + string.Join("\n", diagnostics), !passed);
        }
        finally
        {
            await OnUiAsync(() =>
            {
                state.Vm.FlushAutoSave();
                state.Vm.StopWatchingForDebug();
                state.Window.Close();
                state.Window.DataContext = null;
                return true;
            });
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }
}
