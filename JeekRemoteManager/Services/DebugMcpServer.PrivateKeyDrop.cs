using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using JeekRemoteManager.Models;
using JeekRemoteManager.ViewModels;
using JeekRemoteManager.Views;
using JeekTools;

namespace JeekRemoteManager.Services;

internal static partial class DebugMcpServer
{
    private static async Task<JsonObject> PrivateKeyFileDropCheckAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "jrm-key-drop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var keyPath = Path.Combine(root, "测试 私钥");
        var secondPath = Path.Combine(root, "second.key");
        File.WriteAllText(keyPath, "not a real key; drop fixture only");
        File.WriteAllText(secondPath, "second fixture");
        var state = await OnUiAsync(() =>
        {
            var settings = new SettingsService(Path.Combine(root, "machine.json"), Path.Combine(root, "roaming.json"));
            settings.Settings.StorageLocation = StorageLocation.CustomDirectory;
            settings.Settings.CustomStoragePath = root;
            var store = new ConnectionStore(settings.ResolveConnectionsRoot());
            var connectionPath = store.Save(new Connection { Name = "target", PrivateKeyPath = "original-path" }, store.RootPath);
            var vm = new MainWindowViewModel(store, new ConnectionLauncher(), settings);
            var window = new MainWindow { DataContext = vm, Width = 1150, Height = 950, ShowActivated = false, ShowInTaskbar = false };
            window.Show();
            vm.SelectedNode = vm.Nodes.Single(node => node.Name == "target");
            window.FindControl<TabItem>("EditorTab")!.IsSelected = true;
            return (Vm: vm, Window: window, Store: store, ConnectionPath: connectionPath);
        });
        var checks = new Dictionary<string, bool>();
        void Check(string name, bool passed) => checks[name] = passed;
        try
        {
            await Task.Delay(100);
            var box = await OnUiAsync(() => state.Window.GetVisualDescendants().OfType<TextBox>()
                .Single(control => control.Name == "PrivateKeyPathBox"));
            var file = await await OnUiAsync(() => state.Window.StorageProvider.TryGetFileFromPathAsync(keyPath));
            var second = await await OnUiAsync(() => state.Window.StorageProvider.TryGetFileFromPathAsync(secondPath));
            var folder = await await OnUiAsync(() => state.Window.StorageProvider.TryGetFolderFromPathAsync(root));
            if (file is null || second is null || folder is null)
                return ToolText("FAIL: local test storage items are unavailable.", true);

            DataTransfer Files(params IStorageItem[] items)
            {
                var data = new DataTransfer();
                foreach (var item in items)
                    data.Add(DataTransferItem.CreateFile(item));
                return data;
            }
            DragEventArgs Raise(Avalonia.Interactivity.RoutedEvent<DragEventArgs> routedEvent, DataTransfer data)
            {
                var args = new DragEventArgs(routedEvent, data, box, new Point(5, 5), KeyModifiers.None)
                {
                    DragEffects = DragDropEffects.Copy | DragDropEffects.Move,
                };
                box.RaiseEvent(args);
                return args;
            }
            await OnUiAsync(() =>
            {
                var data = Files(file);
                data.Add(DataTransferItem.CreateText("ignore textual representation of the file"));
                var over = Raise(DragDrop.DragOverEvent, data);
                Check("copyCursorAndHighlight", over.Handled && over.DragEffects == DragDropEffects.Copy
                    && box.Classes.Contains("private-key-file-over"));
                var dropped = Raise(DragDrop.DropEvent, data);
                state.Vm.FlushAutoSave();
                Check("singleFileSaved", dropped.Handled && dropped.DragEffects == DragDropEffects.Copy
                    && state.Store.Load(state.ConnectionPath).PrivateKeyPath == keyPath);
                Check("pathRendered", box.Text == keyPath && !box.Classes.Contains("private-key-file-over"));
                foreach (var (name, invalid) in new[]
                {
                    ("multipleFilesRejected", Files(file, second)), ("folderRejected", Files(folder)),
                })
                {
                    invalid.Add(DataTransferItem.CreateText("must not replace the current path"));
                    var rejected = Raise(DragDrop.DropEvent, invalid);
                    Check(name, rejected.Handled && rejected.DragEffects == DragDropEffects.None
                        && box.Text == keyPath && state.Vm.Editor!.PrivateKeyPath == keyPath);
                }
                var text = new DataTransfer();
                text.Add(DataTransferItem.CreateText("plain text is not a file drop"));
                Check("textDropRejected", Raise(DragDrop.DropEvent, text).DragEffects == DragDropEffects.None && box.Text == keyPath);
                File.Delete(secondPath);
                Check("missingFileRejected", Raise(DragDrop.DropEvent, Files(second)).DragEffects == DragDropEffects.None && box.Text == keyPath);
                Raise(DragDrop.DragOverEvent, Files(file));
                Raise(DragDrop.DragLeaveEvent, Files(file));
                Check("leaveClearsHighlight", !box.Classes.Contains("private-key-file-over"));
                box.SetCurrentValue(TextBox.TextProperty, "manual-entry");
                Check("manualEditingPreserved", state.Vm.Editor!.PrivateKeyPath == "manual-entry");
                state.Vm.PickKeyFileAsync = () => Task.FromResult<string?>(keyPath);
                return true;
            });
            await await OnUiAsync(() => state.Vm.BrowseKeyCommand.ExecuteAsync(null));
            await OnUiAsync(() =>
            {
                state.Vm.FlushAutoSave();
                Check("browseAndBindingPreserved", box.Text == keyPath && state.Store.Load(state.ConnectionPath).PrivateKeyPath == keyPath);
                Check("sourceUntouched", File.ReadAllText(keyPath) == "not a real key; drop fixture only");
                return true;
            });
            var passed = checks.Values.All(value => value);
            return ToolText($"{(passed ? "PASS" : "FAIL")}: private key file drop\n"
                + string.Join("\n", checks.Select(pair => $"{pair.Key}={pair.Value}")), !passed);
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
