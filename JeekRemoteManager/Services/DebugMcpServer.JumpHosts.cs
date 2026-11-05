using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using JeekRemoteManager.Models;
using JeekRemoteManager.ViewModels;
using JeekRemoteManager.Views;
using Renci.SshNet;
using JeekTools;

namespace JeekRemoteManager.Services;

internal static partial class DebugMcpServer
{
    private static async Task<JsonObject> JumpHostEditorCheckAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "jrm-jump-drag-" + Guid.NewGuid().ToString("N"));
        var state = await OnUiAsync(() =>
        {
            var settings = new SettingsService(Path.Combine(root, "machine.json"), Path.Combine(root, "roaming.json"));
            settings.Settings.StorageLocation = StorageLocation.CustomDirectory;
            settings.Settings.CustomStoragePath = root;
            var store = new ConnectionStore(settings.ResolveConnectionsRoot());
            foreach (var connection in new[]
            {
                new Connection { Name = "target", Host = "target.invalid", ConnectionId = "target" },
                new Connection { Name = "jump", Host = "jump.invalid", Username = "probe", ConnectionId = "jump" },
                new Connection { Name = "cycle", Host = "cycle.invalid", JumpHost = "target", ConnectionId = "cycle" },
                new Connection { Name = "desktop", Type = ConnectionType.Rdp },
                new Connection { Name = "vnc", Type = ConnectionType.Vnc },
            })
                store.Save(connection, connection.Name == "jump" ? Path.Combine(store.RootPath, "hosts") : store.RootPath);
            var vm = new MainWindowViewModel(store, new ConnectionLauncher(), settings);
            var window = new MainWindow
            {
                DataContext = vm, Width = 1150, Height = 950, ShowActivated = false,
                ShowInTaskbar = false, Title = "Jump host drag probe",
            };
            window.Show();
            vm.SelectedNode = vm.Nodes.Single(node => node.Name == "target");
            window.FindControl<TabItem>("EditorTab")!.IsSelected = true;
            return (Vm: vm, Store: store, Window: window);
        });
        var checks = new Dictionary<string, bool>();
        Border Zone() => state.Window.GetVisualDescendants().OfType<Border>()
            .First(border => Equals(border.Tag, "jump-host-drop") && border.IsEffectivelyVisible);
        Point DropPoint() => Zone().TranslatePoint(Zone().Bounds.Center, state.Window)!.Value;
        IEnumerable<TreeNodeViewModel> Flatten(IEnumerable<TreeNodeViewModel> nodes) =>
            nodes.Where(node => !node.IsRecent).SelectMany(node => new[] { node }.Concat(Flatten(node.Children)));
        TreeNodeViewModel Node(string name) => Flatten(state.Vm.Nodes).Single(node => node.Name == name);
        void Check(string name, bool passed) => checks[name] = passed;
        try
        {
            await Task.Delay(150);
            await OnUiAsync(() => { Zone().BringIntoView(); return true; });
            await Task.Delay(100);
            await OnUiAsync(() =>
            {
                var editor = state.Vm.Editor!;
                var folder = Node("hosts");
                var expanded = folder.IsExpanded;
                state.Window.DebugDragTreeNode(folder, default, move: false, expander: true);
                state.Window.DebugDragTreeNode(folder, default, move: false, expander: true);
                Check("folderExpanderPreservesEditor", ReferenceEquals(editor, state.Vm.Editor) && folder.IsExpanded == expanded);
                var dragged = state.Window.DebugDragTreeNode(Node("jump"), DropPoint());
                Check("editorPreservedOnPress", dragged.EditorPreserved && ReferenceEquals(editor, state.Vm.Editor));
                Check("sshDrop", dragged.ValidHighlight && editor.JumpHost == "hosts/jump" && editor.JumpHostDisplay.Contains("probe@jump.invalid"));
                foreach (var name in new[] { "target", "desktop", "cycle" })
                {
                    var rejected = state.Window.DebugDragTreeNode(Node(name), DropPoint());
                    Check(name + "Rejected", rejected.InvalidHighlight && editor.JumpHost == "hosts/jump" && editor.HasJumpHostValidationMessage);
                }
                var clear = Zone().GetVisualParent()!.GetVisualDescendants().OfType<Button>().Single();
                clear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check("clearToDirect", editor.JumpHost == "" && !editor.HasJumpHostValidationMessage);
                state.Window.DebugDragTreeNode(Node("jump"), new Point(state.Window.Bounds.Width - 5, 5));
                Check("outsideDropPreservesSelection", ReferenceEquals(editor, state.Vm.Editor) && editor.JumpHost == "");
                state.Window.DebugDragTreeNode(Node("jump"), default, move: false);
                Check("plainClickSelects", state.Vm.SelectedNode == Node("jump"));
                Check("plainClickFocuses", state.Window.GetVisualDescendants().OfType<TreeViewItem>()
                    .Any(item => ReferenceEquals(item.DataContext, Node("jump")) && item.IsFocused));
                state.Vm.SelectedNode = Node("vnc");
                return true;
            });
            await Task.Delay(100);
            await OnUiAsync(() => { Zone().BringIntoView(); return true; });
            await Task.Delay(100);
            await OnUiAsync(() =>
            {
                var dropped = state.Window.DebugDragTreeNode(Node("jump"), DropPoint());
                Check("vncDrop", dropped.ValidHighlight && state.Vm.Editor!.JumpHost == "hosts/jump");
                state.Vm.FlushAutoSave();
                Check("noFilesMoved", new[] { "target", "hosts/jump", "desktop", "cycle", "vnc" }
                    .All(name => File.Exists(Path.Combine(state.Store.RootPath, name + ConnectionStore.FileExtension))));
                state.Window.DebugDragTreeNode(Node("jump"), state.Window.DebugTreeRootDropPoint);
                Check("treeMoveStillWorks", File.Exists(Path.Combine(state.Store.RootPath, "jump.json"))
                    && !File.Exists(Path.Combine(state.Store.RootPath, "hosts", "jump.json")));
                return true;
            });
            var passed = checks.Values.All(value => value);
            return ToolText($"{(passed ? "PASS" : "FAIL")}: tree drag to jump host\n"
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

    private static Task<JsonObject> SshJumpChainCheckAsync(JsonObject args) => Task.Run(() =>
    {
        var host = ArgString(args, "host") ?? "127.0.0.1";
        var port = ArgInt(args, "port") ?? 2222;
        var username = ArgString(args, "username") ?? "jrmtest";
        var keyPath = ArgString(args, "privateKeyPath") ?? "";
        var innerHost = ArgString(args, "innerHost") ?? host;
        var targetHost = ArgString(args, "targetHost") ?? host;
        var knownHostsPath = Path.Combine(Path.GetTempPath(), "jrm-jump-check-" + Guid.NewGuid().ToString("N") + ".json");
        var knownHosts = new KnownHostsStore(knownHostsPath);
        Connection Make(string id, string jump = "") => new()
        {
            ConnectionId = id, Name = id, Host = host, Port = port, Username = username,
            PrivateKeyPath = keyPath, JumpHost = jump,
        };
        var outer = Make("outer");
        var inner = Make("inner", "outer");
        var target = Make("target", "inner");
        inner.Host = innerHost;
        target.Host = targetHost;
        Connection? Resolve(string path) => path switch { "outer" => outer, "inner" => inner, _ => null };
        var options = new SshDialOptions(KnownHosts: knownHosts);
        try
        {
            var (client, tunnel) = SshDialer.Connect(target, info => new SshClient(info), options, Resolve);
            var ports = tunnel!.LocalPortsForDebug.ToArray();
            string output;
            using (tunnel)
            using (client)
            using (var command = client.CreateCommand("echo via-jump-chain"))
            {
                command.CommandTimeout = TimeSpan.FromSeconds(10);
                output = command.Execute().Trim();
            }
            var listenersClosed = ports.All(p => !IsListening(p));
            var (sftp, sftpTunnel) = SshDialer.Connect(target, info => new SftpClient(info), options, Resolve);
            bool sftpConnected;
            using (sftpTunnel)
            using (sftp)
                sftpConnected = sftp.IsConnected && sftp.WorkingDirectory.Length > 0;
            var realHostKeys = knownHosts.All().Count == new[] { host, innerHost, targetHost }.Distinct(StringComparer.OrdinalIgnoreCase).Count()
                && new[] { host, innerHost, targetHost }.All(h => knownHosts.TryGet(h, port, out _));
            var changedKeyRejected = false;
            // Separate endpoints let the fixture also prove that an intermediate key is checked
            // under its own real host, rather than a local tunnel address or the final target.
            if (!string.Equals(host, innerHost, StringComparison.OrdinalIgnoreCase))
            {
                knownHosts.Trust(innerHost, port, "deliberately-mismatched-test-key");
                var mismatchHost = "";
                try
                {
                    var (unexpected, unexpectedTunnel) = SshDialer.Connect(target, info => new SshClient(info),
                        options with { OnMismatch = (h, p, _, _, _) => { mismatchHost = h; return false; } }, Resolve);
                    unexpected.Dispose();
                    unexpectedTunnel?.Dispose();
                }
                catch (Renci.SshNet.Common.SshConnectionException)
                {
                    changedKeyRejected = mismatchHost == innerHost;
                }
            }
            var passed = output == "via-jump-chain" && ports.Length == 2 && listenersClosed && sftpConnected && realHostKeys
                && (host == innerHost || changedKeyRejected);
            return ToolText($"{(passed ? "PASS" : "FAIL")}: two-hop SSH and SFTP\nhops={ports.Length}\noutput={output}"
                + $"\nlistenersClosed={listenersClosed}\nsftpConnected={sftpConnected}\nrealHostKeys={realHostKeys}"
                + $"\nintermediateKeyRejected={(host == innerHost ? "not-tested-same-host" : changedKeyRejected.ToString())}", !passed);
        }
        catch (Exception ex)
        {
            return ToolText($"FAIL: two-hop SSH and SFTP: {ex.GetType().Name}: {ex.Message}", true);
        }
        finally
        {
            try { File.Delete(knownHostsPath); } catch { /* ignore */ }
        }
    });
}
