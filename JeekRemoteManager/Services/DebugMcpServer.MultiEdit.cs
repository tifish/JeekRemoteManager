using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using JeekRemoteManager.Models;
using JeekRemoteManager.ViewModels;
using JeekRemoteManager.Views;
using JeekTools;

namespace JeekRemoteManager.Services;

internal static partial class DebugMcpServer
{
    private static async Task<JsonObject> MultiConnectionEditorCheckAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "jrm-multi-edit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var keyFile = Path.Combine(root, "team.key");
        File.WriteAllText(keyFile, "dummy key fixture");

        var state = await OnUiAsync(() =>
        {
            var settings = new SettingsService(Path.Combine(root, "machine.json"), Path.Combine(root, "roaming.json"));
            settings.Settings.StorageLocation = StorageLocation.CustomDirectory;
            settings.Settings.CustomStoragePath = root;
            settings.Settings.ConnectionPanelWidth = 306;
            var store = new ConnectionStore(settings.ResolveConnectionsRoot());

            var jumpId = Guid.NewGuid().ToString("D");
            var ssh1Path = store.Save(new Connection
            {
                Name = "web-01",
                ShortName = "A",
                Type = ConnectionType.Ssh,
                Host = "10.0.0.1",
                Port = 22,
                Username = "ubuntu",
                EncryptedPassword = PasswordProtector.Encrypt("secret-one"),
                TerminalType = "xterm-256color",
                TerminalEncoding = "UTF-8",
                AutoOpenMonitorPanel = true,
                AutoOpenFileBrowserPanel = false,
                AutoLogSession = true,
                Notes = "first",
            }, store.RootPath);

            var ssh2Path = store.Save(new Connection
            {
                Name = "web-02",
                ShortName = "B",
                Type = ConnectionType.Ssh,
                Host = "10.0.0.2",
                Port = 22,
                Username = "ubuntu",
                EncryptedPassword = PasswordProtector.Encrypt("secret-two"),
                TerminalType = "xterm",
                TerminalEncoding = "UTF-8",
                AutoOpenMonitorPanel = false,
                AutoOpenFileBrowserPanel = false,
                AutoLogSession = true,
                Notes = "second",
            }, store.RootPath);

            store.Save(new Connection
            {
                ConnectionId = jumpId,
                Name = "bastion",
                Type = ConnectionType.Ssh,
                Host = "bastion.invalid",
                Port = 22,
                Username = "jump",
            }, Path.Combine(store.RootPath, "infra"));

            var rdpPath = store.Save(new Connection
            {
                Name = "win-01",
                Type = ConnectionType.Rdp,
                Host = "10.0.1.10",
                Port = 3389,
                Username = "ubuntu",
                Notes = "rdp-note",
            }, store.RootPath);

            store.CreateFolder(store.RootPath, "empty-a");
            store.CreateFolder(store.RootPath, "empty-b");

            var vm = new MainWindowViewModel(store, new ConnectionLauncher(), settings);
            var window = new MainWindow
            {
                DataContext = vm,
                Width = 760,
                Height = 500,
                ShowActivated = false,
                ShowInTaskbar = false,
                Title = "Multi connection editor probe",
            };
            window.Show();
            window.FindControl<TabItem>("EditorTab")!.IsSelected = true;
            return (Vm: vm, Store: store, Window: window, Ssh1Path: ssh1Path, Ssh2Path: ssh2Path, RdpPath: rdpPath, JumpId: jumpId);
        });

        var checks = new Dictionary<string, bool>();
        void Check(string name, bool passed) => checks[name] = passed;

        IEnumerable<TreeNodeViewModel> Flatten(IEnumerable<TreeNodeViewModel> nodes) =>
            nodes.Where(n => !n.IsRecent).SelectMany(n => new[] { n }.Concat(Flatten(n.Children)));
        TreeNodeViewModel Node(string name) => Flatten(state.Vm.Nodes).Single(n => n.Name == name);

        void SelectNodes(params TreeNodeViewModel[] nodes)
        {
            var tree = state.Window.FindControl<TreeView>("Tree")!;
            tree.SelectedItems.Clear();
            foreach (var n in nodes)
                tree.SelectedItems.Add(n);
            state.Vm.SetSelectedNodes(nodes);
        }

        try
        {
            await Task.Delay(150);

            // 1. Multi-select two SSH connections and verify UI presentation + scrollbar hygiene at 760x500.
            await OnUiAsync(() =>
            {
                var web1 = Node("web-01");
                var web2 = Node("web-02");
                SelectNodes(web1, web2);
                state.Window.UpdateLayout();
                return true;
            });

            await Dispatcher.UIThread.InvokeAsync(() => true, DispatcherPriority.Background);

            await OnUiAsync(() =>
            {
                var vm = state.Vm;
                var multi = vm.MultiEditor;
                Check("multiEditorVisible", vm.ShowMultiConnectionEditor && !vm.ShowConnectionEditor && !vm.ShowPlaceholder && vm.CanEditSelection);
                Check("initialUniformAndMixed", multi is not null
                    && multi.IsAllSsh
                    && multi.Username == "ubuntu"
                    && multi.PortText == "22"
                    && multi.Host == ""
                    && multi.AutoOpenMonitorPanel is null
                    && multi.AutoOpenMonitorPanelThreeState
                    && multi.AutoLogSession == true
                    && !multi.HasPendingChanges
                    && !vm.ApplyMultiEditCommand.CanExecute(null));

                var view = state.Window.GetVisualDescendants().OfType<MultiConnectionEditorView>().FirstOrDefault();
                var scroll = view?.FindControl<ScrollViewer>("MultiEditorScrollViewer");
                var hBar = scroll?.GetVisualDescendants().OfType<ScrollBar>()
                    .FirstOrDefault(bar => bar.Orientation == Orientation.Horizontal);
                Check("noHorizontalScrollAt760x500", scroll is not null
                    && scroll.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled
                    && hBar?.IsVisible != true);

                // Reset reverts uncommitted edits.
                multi!.Username = "temp-user";
                Check("dirtyAfterEdit", multi.IsUsernameModified && multi.ModifiedFieldCount == 1 && vm.ApplyMultiEditCommand.CanExecute(null));
                vm.ResetMultiEditCommand.Execute(null);
                Check("resetRestoresBaseline", multi.Username == "ubuntu" && !multi.HasPendingChanges && !vm.ApplyMultiEditCommand.CanExecute(null));
                return true;
            });

            // 2. Test private-key file drop + jump-host tree drag + delta edits + Apply.
            var keyStorageFile = await await OnUiAsync(() => state.Window.StorageProvider.TryGetFileFromPathAsync(keyFile));
            await OnUiAsync(() =>
            {
                var multi = state.Vm.MultiEditor!;
                var keyBox = state.Window.GetVisualDescendants().OfType<TextBox>()
                    .Single(box => box.Name == "MultiPrivateKeyPathBox");
                if (keyStorageFile is not null)
                {
                    var data = new DataTransfer();
                    data.Add(DataTransferItem.CreateFile(keyStorageFile));
                    var dropArgs = new DragEventArgs(DragDrop.DropEvent, data, keyBox, new Point(5, 5), KeyModifiers.None)
                    {
                        DragEffects = DragDropEffects.Copy,
                    };
                    keyBox.RaiseEvent(dropArgs);
                    Check("privateKeyDropSetsPath", dropArgs.Handled && multi.PrivateKeyPath == keyFile && multi.IsPrivateKeyPathModified);
                }
                else
                {
                    Check("privateKeyDropSetsPath", false);
                }

                // Bring jump-host drop zone into view before dragging from tree.
                var jumpZone = state.Window.GetVisualDescendants().OfType<Border>()
                    .First(b => Equals(b.Tag, "jump-host-drop") && b.IsEffectivelyVisible);
                jumpZone.BringIntoView();
                state.Window.UpdateLayout();
                return true;
            });

            await Task.Delay(100);

            var (reloadsBeforeApply, adoptedBeforeApply) = await OnUiAsync<(long, long)>(() =>
            {
                var vm = state.Vm;
                var multi = vm.MultiEditor!;
                var jumpZone = state.Window.GetVisualDescendants().OfType<Border>()
                    .First(b => Equals(b.Tag, "jump-host-drop") && b.IsEffectivelyVisible);
                var dropPoint = jumpZone.TranslatePoint(jumpZone.Bounds.Center, state.Window)!.Value;

                var dragValid = state.Window.DebugDragTreeNode(Node("bastion"), dropPoint);
                Check("jumpHostDragApplies", dragValid.EditorPreserved && dragValid.ValidHighlight
                    && multi.JumpHostConnectionId == state.JumpId && multi.IsJumpHostModified);

                // Dragging one of the selected targets (self-reference) must be rejected and preserve bastion.
                var dragSelf = state.Window.DebugDragTreeNode(Node("web-01"), dropPoint);
                Check("jumpHostSelfRejected", dragSelf.InvalidHighlight
                    && multi.JumpHostConnectionId == state.JumpId && multi.HasJumpHostValidationMessage);

                // Edit username, port, clear mixed Notes, set mixed AutoOpenMonitorPanel -> true, set new password.
                multi.Username = "deploy";
                multi.PortText = "2222";
                multi.ShortName = "PROD";
                multi.ClearNotes();
                multi.AutoOpenMonitorPanel = true;
                multi.Password = "rotated-secret";

                var before = (vm.TreeReloadCountForDebug, state.Store.OwnEditsAdoptedForDebug);
                vm.ApplyMultiEditCommand.Execute(null);
                return before;
            });

            // Wait past the file watcher debounce to prove SaveEdits adopted the fingerprint and kept selection.
            await Task.Delay(900);

            await OnUiAsync(() =>
            {
                var vm = state.Vm;
                var reloaded1 = state.Store.Load(state.Ssh1Path);
                var reloaded2 = state.Store.Load(state.Ssh2Path);

                Check("deltaAppliedToDisk",
                    reloaded1.Username == "deploy" && reloaded2.Username == "deploy"
                    && reloaded1.Port == 2222 && reloaded2.Port == 2222
                    && reloaded1.ShortName == "PROD" && reloaded2.ShortName == "PROD"
                    && reloaded1.Notes == "" && reloaded2.Notes == ""
                    && reloaded1.AutoOpenMonitorPanel && reloaded2.AutoOpenMonitorPanel
                    && reloaded1.PrivateKeyPath == keyFile && reloaded2.PrivateKeyPath == keyFile
                    && reloaded1.JumpHostConnectionId == state.JumpId && reloaded2.JumpHostConnectionId == state.JumpId);

                Check("untouchedFieldsPreserved",
                    reloaded1.Host == "10.0.0.1" && reloaded2.Host == "10.0.0.2"
                    && reloaded1.TerminalType == "xterm-256color" && reloaded2.TerminalType == "xterm"
                    && reloaded1.Name == "web-01" && reloaded2.Name == "web-02");

                Check("passwordsRotatedIndependently",
                    !string.IsNullOrEmpty(reloaded1.EncryptedPassword)
                    && !string.IsNullOrEmpty(reloaded2.EncryptedPassword)
                    && reloaded1.EncryptedPassword != reloaded2.EncryptedPassword
                    && PasswordProtector.TryDecrypt(reloaded1.EncryptedPassword, out var p1) && p1 == "rotated-secret"
                    && PasswordProtector.TryDecrypt(reloaded2.EncryptedPassword, out var p2) && p2 == "rotated-secret");

                Check("treeShortNameUpdated", Node("web-01").ShortName == "PROD" && Node("web-02").ShortName == "PROD");
                Check("watcherSkippedReloadAndSelectionKept",
                    vm.TreeReloadCountForDebug == reloadsBeforeApply
                    && state.Store.OwnEditsAdoptedForDebug > adoptedBeforeApply
                    && vm.ShowMultiConnectionEditor
                    && vm.MultiEditor is { HasPendingChanges: false });

                // 3. Mixed-type selection (SSH + RDP): shows common fields, hides protocol cards.
                SelectNodes(Node("web-01"), Node("win-01"));
                var mixed = vm.MultiEditor;
                Check("mixedTypesCommonOnly", vm.ShowMultiConnectionEditor && mixed is not null
                    && !mixed.IsAllSsh && !mixed.IsAllRdp && mixed.HasHostPort && mixed.HasPassword);

                // 4. Folder-only selection: shows placeholder instead of multi-editor.
                SelectNodes(Node("empty-a"), Node("empty-b"));
                Check("folderOnlyShowsPlaceholder", !vm.ShowMultiConnectionEditor && vm.ShowPlaceholder && !vm.CanEditSelection);
                return true;
            });

            var passed = checks.Values.All(v => v);
            return ToolText(
                $"{(passed ? "PASS" : "FAIL")}: multi-connection editor\n"
                + string.Join("\n", checks.Select(pair => $"{pair.Key}={pair.Value}")),
                isError: !passed);
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
