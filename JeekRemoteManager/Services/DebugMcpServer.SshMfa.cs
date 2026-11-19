using System.Text.Json.Nodes;
using Avalonia.Input;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using JeekRemoteManager.Models;
using JeekRemoteManager.ViewModels;
using JeekRemoteManager.Views;
using JeekTools;
using Renci.SshNet;

namespace JeekRemoteManager.Services;

internal static partial class DebugMcpServer
{
    private static async Task<JsonObject> SshMfaCheckAsync(JsonObject args)
    {
        if (!DebugInstanceContext.IsDebugBuild)
            return ToolText("Run this probe in a Debug instance.", true);

        var port = args["loopback_port"]?.GetValue<int>();
        if (port is < 1 or > 65535)
            return ToolText("Invalid loopback fixture port.", true);

        const string initialSeed = "JBSWY3DPEHPK3PXP";
        const string updatedSeed = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";
        var root = Path.Combine(Path.GetTempPath(), "jrm-mfa-probe-" + Guid.NewGuid().ToString("N"));
        var state = await OnUiAsync(() =>
        {
            var settings = new SettingsService(Path.Combine(root, "machine.json"), Path.Combine(root, "roaming.json"));
            settings.Settings.StorageLocation = StorageLocation.CustomDirectory;
            settings.Settings.CustomStoragePath = root;
            settings.Settings.ConnectionPanelWidth = 180;
            var store = new ConnectionStore(settings.ResolveConnectionsRoot());
            var files = new[] { "same-round", "two-round" }.Select(name =>
                store.Save(new Connection
                {
                    Name = name, Username = name, Host = "127.0.0.1", Port = port ?? 22,
                    EncryptedPassword = port.HasValue ? PasswordProtector.Encrypt("jrm-test-password") : "",
                }, store.RootPath)).ToArray();
            var vm = new MainWindowViewModel(store, new ConnectionLauncher(), settings);
            var window = new MainWindow { DataContext = vm, ShowInTaskbar = false, Width = 1100, Height = 900 };
            window.Show();
            vm.SelectedNode = null;
            window.FindControl<TabItem>("EditorTab")!.IsSelected = true;
            return (Window: window, Vm: vm, Store: store, Files: files);
        });

        static T Find<T>(Window window, string name) where T : Control =>
            window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
        static void Click(Window window, string name) =>
            Find<Button>(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Task<bool> SettleUiAsync() => Dispatcher.UIThread.InvokeAsync(() => true, DispatcherPriority.Background).GetTask();

        bool OpenFromConnectionPanel()
        {
            var panel = Find<Border>(state.Window, "TreePanel");
            var button = Find<Button>(state.Window, "ConnectionPanelMfaButton");
            var available = button.IsEffectivelyVisible && button.IsEnabled && button.Flyout is null
                && button.GetVisualAncestors().Contains(panel);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            return available;
        }

        var checks = new Dictionary<string, bool>();
        try
        {
            var profileId = await OnUiAsync(() =>
            {
                var withoutSelection = state.Vm.SelectedNode is null;
                checks["connectionPanelButtonAvailableWithoutSelection"] =
                    OpenFromConnectionPanel() && withoutSelection;
                var manager = state.Window.OwnedWindows.Single();
                Click(manager, "SshMfaAddButton");
                var editor = manager.OwnedWindows.Single();
                Find<TextBox>(editor, "SshMfaNameBox").Text = "Shared fixture";
                var seedBox = Find<TextBox>(editor, "SshMfaSeedBox");
                seedBox.Text = "123456";
                Click(editor, "SshMfaSaveButton");
                checks["invalidSeedRejected"] = Find<TextBlock>(editor, "SshMfaError").IsVisible;
                seedBox.Text = initialSeed;
                Click(editor, "SshMfaSaveButton");
                manager.Close();
                var profile = state.Store.SshMfaProfiles.Read().Single();
                checks["credentialEncrypted"] = MasterKeyService.IsPasswordBlob(profile.EncryptedCredential)
                    && PasswordProtector.Decrypt(profile.EncryptedCredential) == initialSeed;
                return profile.Id;
            });
            await OnUiAsync(() =>
            {
                state.Vm.SelectedNode = state.Vm.Nodes.Single(node => node.Name == "same-round");
                return true;
            });
            await SettleUiAsync();

            var saved = await OnUiAsync(() =>
            {
                void SelectCurrentProfile()
                {
                    var combo = Find<ComboBox>(state.Window, "SshMfaProfileCombo");
                    combo.SelectedItem = combo.Items.OfType<SshMfaProfileChoice>().Single(choice => choice.Id == profileId);
                    state.Vm.FlushAutoSave();
                }
                SelectCurrentProfile();
                state.Vm.SelectedNode = state.Vm.Nodes.Single(node => node.Name == "two-round");
                SelectCurrentProfile();

                var connections = state.Files.Select(state.Store.Load).ToArray();
                checks["bothConnectionsSavedOnlyTheReference"] = connections.All(c => c.SshMfaProfileId == profileId)
                    && state.Files.All(file => !File.ReadAllText(file).Contains(initialSeed, StringComparison.Ordinal));

                var original = state.Store.SshMfaProfiles.TryGetEncryptedCredential(profileId);
                var selectedNode = state.Vm.SelectedNode;
                checks["connectionPanelButtonKeepsSelection"] =
                    OpenFromConnectionPanel() && ReferenceEquals(state.Vm.SelectedNode, selectedNode);
                var manager = state.Window.OwnedWindows.Single();
                var list = Find<ListBox>(manager, "SshMfaProfileList");
                list.SelectedItem = list.Items.OfType<SshMfaProfileChoice>().Single();
                Click(manager, "SshMfaEditButton");
                var editor = manager.OwnedWindows.Single();
                checks["savedSeedIsNotPrefilled"] = string.IsNullOrEmpty(Find<TextBox>(editor, "SshMfaSeedBox").Text);
                Find<TextBox>(editor, "SshMfaNameBox").Text = "Renamed fixture";
                Find<ComboBox>(editor, "SshMfaInputMode").SelectedIndex = (int)SshMfaInputMode.MatchPrompt;
                Click(editor, "SshMfaSaveButton");
                checks["customTriggerRequiresText"] = editor.IsVisible && Find<TextBlock>(editor, "SshMfaError").IsVisible;
                Find<TextBox>(editor, "SshMfaInputPrompt").Text = "2nd Password:";
                Click(editor, "SshMfaSaveButton");
                checks["renameKeepsTheCredential"] = state.Store.SshMfaProfiles.TryGetEncryptedCredential(profileId) == original;
                return connections;
            });
            await SettleUiAsync();

            await OnUiAsync(() =>
            {
                var manager = state.Window.OwnedWindows.Single();
                var list = Find<ListBox>(manager, "SshMfaProfileList");
                var pointer = new Pointer(0, PointerType.Mouse, true);
                void DoubleTap(Control source)
                {
                    var pressed = new PointerPressedEventArgs(source, pointer, manager, default, 1,
                        new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
                        KeyModifiers.None, 2);
                    source.RaiseEvent(new TappedEventArgs(InputElement.DoubleTappedEvent, pressed));
                }

                DoubleTap(list);
                checks["doubleTapOnListBackgroundIsInert"] = manager.OwnedWindows.Count == 0;
                var rowText = list.GetVisualDescendants().OfType<TextBlock>()
                    .Single(text => text.Text == "Renamed fixture");
                DoubleTap(rowText);
                var editor = manager.OwnedWindows.Single();
                checks["doubleTapOnProfileOpensItsEditor"] = Find<TextBox>(editor, "SshMfaNameBox").Text == "Renamed fixture";
                checks["customTriggerReloads"] = Find<ComboBox>(editor, "SshMfaInputMode").SelectedIndex
                    == (int)SshMfaInputMode.MatchPrompt && Find<TextBox>(editor, "SshMfaInputPrompt").Text == "2nd Password:";
                Find<ComboBox>(editor, "SshMfaInputMode").SelectedIndex = (int)SshMfaInputMode.OnShellOpen;
                Find<TextBox>(editor, "SshMfaSeedBox").Text = updatedSeed;
                Click(editor, "SshMfaSaveButton");
                manager.Close();
                return true;
            });
            await SettleUiAsync();

            await OnUiAsync(() =>
            {
                var combo = Find<ComboBox>(state.Window, "SshMfaProfileCombo");
                checks["renamedProfileRemainsSelected"] = combo.SelectedItem is SshMfaProfileChoice selected
                    && selected.Id == profileId && selected.Name == "Renamed fixture";
                return true;
            });
            checks["bothExistingConnectionsResolveTheReplacement"] = saved.All(c =>
                PasswordProtector.Decrypt(c.SshMfaProfiles?.TryGetEncryptedCredential(profileId)) == updatedSeed);

            if (port.HasValue)
            {
                var options = new SshDialOptions(KnownHosts: new KnownHostsStore(Path.Combine(root, "known-hosts.json")));
                foreach (var connection in saved)
                {
                    try
                    {
                        var (client, tunnel) = await Task.Run(() => SshDialer.Connect(connection,
                            info => new SshClient(info), options, configure: info => info.Timeout = TimeSpan.FromSeconds(8)));
                        using (client)
                        using (tunnel)
                            checks[connection.Name + "LoopbackAuthentication"] = client.IsConnected;
                    }
                    catch (Exception ex)
                    {
                        return ToolText($"FAIL: {connection.Name} loopback authentication ({ex.GetType().Name}).", true);
                    }
                }
            }

            await OnUiAsync(() =>
            {
                Click(state.Window, "SshMfaManageButton");
                var manager = state.Window.OwnedWindows.Single();
                var list = Find<ListBox>(manager, "SshMfaProfileList");
                list.SelectedItem = list.Items.OfType<SshMfaProfileChoice>().Single();
                Click(manager, "SshMfaRemoveButton");
                Click(manager.OwnedWindows.Single(), "ConfirmYesButton");
                return true;
            });
            await SettleUiAsync();
            await OnUiAsync(() =>
            {
                state.Window.OwnedWindows.Single().Close();
                return true;
            });
            await SettleUiAsync();
            checks["removedProfileCannotResolve"] = state.Store.SshMfaProfiles.TryGetEncryptedCredential(profileId) is null;
            await OnUiAsync(() =>
            {
                var combo = Find<ComboBox>(state.Window, "SshMfaProfileCombo");
                checks["missingReferenceRemainsVisible"] = combo.SelectedItem is SshMfaProfileChoice missing && missing.Id == profileId;
                combo.SelectedItem = combo.Items.OfType<SshMfaProfileChoice>().Single(choice => choice.Id is null);
                state.Vm.FlushAutoSave();
                checks["referenceCanBeCleared"] = state.Store.Load(state.Files[1]).SshMfaProfileId is null;
                return true;
            });

            var passed = checks.Values.All(value => value);
            return ToolText($"{(passed ? "PASS" : "FAIL")}: shared SSH MFA\n"
                + string.Join("\n", checks.Select(check => $"{check.Key}={check.Value}")), !passed);
        }
        finally
        {
            await OnUiAsync(() =>
            {
                state.Vm.StopWatchingForDebug();
                state.Window.Close();
                state.Window.DataContext = null;
                return true;
            });
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
}
