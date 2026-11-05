using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using JeekRemoteManager.Models;
using JeekRemoteManager.ViewModels;
using JeekRemoteManager.Views;
using Renci.SshNet;

namespace JeekRemoteManager.Services;

internal static partial class DebugMcpServer
{
    private static async Task<JsonObject> JumpHostEditorCheckAsync()
    {
        var state = await OnUiAsync(() =>
        {
            var editor = ConnectionEditorViewModel.FromConnection(new Connection
            {
                ConnectionId = "target", JumpHost = "vps/inner",
            });
            var saved = new Dictionary<string, Connection>
            {
                ["target"] = new() { ConnectionId = "target" },
                ["vps/outer"] = new() { ConnectionId = "outer" },
                ["vps/inner"] = new() { ConnectionId = "inner", JumpHost = "vps/outer" },
                ["desktop"] = new() { Type = ConnectionType.Rdp },
            };
            editor.SetJumpHostConnections(saved);
            var main = (MainWindow)ResolveRoot("MainWindow")!;
            var template = main.GetLogicalDescendants().OfType<ContentControl>()
                .SelectMany(control => control.DataTemplates).First(item => item.Match(editor));
            var view = template.Build(editor)!;
            view.DataContext = editor;
            var window = new Window
            {
                Content = view, Width = 850, Height = 700,
                ShowInTaskbar = false, ShowActivated = false, Title = "Jump host editor probe",
            };
            window.Show();
            return (Editor: editor, Saved: saved, View: view, Window: window);
        });
        try
        {
            var selectors = await OnUiAsync(() => state.View.GetLogicalDescendants().OfType<AutoCompleteBox>()
                .Where(box => box.Name is "JumpHostBox" or "VncTunnelBox").ToArray());
            var initialized = await ActThenReadAsync(() => { }, () => selectors.Length == 2
                && selectors.All(box => box.Text == "vps/inner"
                    && ReferenceEquals(box.ItemsSource, state.Editor.AvailableJumpHosts))
                && state.Editor.AvailableJumpHosts.SequenceEqual(new[] { "vps/inner", "vps/outer" }));
            var selected = await ActThenReadAsync(
                () => selectors.Single(box => box.Name == "JumpHostBox").SelectedItem = "vps/outer",
                () => state.Editor.JumpHost == "vps/outer" && !state.Editor.HasJumpHostValidationMessage);
            var invalid = await ActThenReadAsync(
                () => selectors.Single(box => box.Name == "JumpHostBox").Text = "missing/path",
                () => state.Editor.JumpHost == "missing/path" && state.Editor.HasJumpHostValidationMessage);
            var refreshed = await ActThenReadAsync(() =>
            {
                state.Saved["new/jump"] = new Connection { ConnectionId = "new" };
                state.Editor.SetJumpHostConnections(state.Saved);
            }, () => state.Editor.JumpHost == "missing/path" && selectors.All(box => box.Text == "missing/path"));
            var next = ConnectionEditorViewModel.FromConnection(new Connection { JumpHost = "vps/inner", Type = ConnectionType.Vnc });
            next.SetJumpHostConnections(state.Saved);
            var rebound = await ActThenReadAsync(() => state.View.DataContext = next,
                () => next.JumpHost == "vps/inner" && selectors.All(box => box.Text == "vps/inner"
                    && ReferenceEquals(box.ItemsSource, next.AvailableJumpHosts)));
            var cleared = await ActThenReadAsync(
                () => selectors.Single(box => box.Name == "VncTunnelBox").Text = "",
                () => next.JumpHost == "" && !next.HasJumpHostValidationMessage);
            var passed = initialized && selected && invalid && refreshed && rebound && cleared;
            return ToolText($"{(passed ? "PASS" : "FAIL")}: jump host editor\ninitialized={initialized}\nselected={selected}"
                + $"\ninvalidPath={invalid}\nrefreshPreservedText={refreshed}\neditorRebound={rebound}\ncleared={cleared}", !passed);
        }
        finally
        {
            await OnUiAsync(() => { state.Window.Close(); state.Window.Content = null; return true; });
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
