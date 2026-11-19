using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using JeekRemoteManager.Views;
using SkiaSharp;
using ZXing;
using ZXing.Common;

namespace JeekRemoteManager.Services;

internal static partial class DebugMcpServer
{
    private static async Task<JsonObject> SshMfaQrCheckAsync()
    {
        if (!DebugInstanceContext.IsDebugBuild)
            return ToolText("Run this probe in a Debug instance.", true);

        // Synthetic Google Authenticator export with three TOTP accounts, one HOTP,
        // one unsupported algorithm, and the metadata for page 2 of 3.
        const string batch = "CjgKFDEyMzQ1Njc4OTAxMjM0NTY3ODkwEhFqdW1wQGV4YW1wbGUudGVzdBoHRml4dHVyZSABKAEwAgonCglvYXRoLW9u"
            + "bHkSC0hPVFAgc2FtcGxlGgdGaXh0dXJlIAEoATABCjkKIDEyMzQ1Njc4OTAxMjM0NTY3ODkwMTIzNDU2Nzg5MDEyEgZE"
            + "ZXBsb3kaB0ZpeHR1cmUgAigCMAIKIAoDYmFkEgpNRDUgc2FtcGxlGgdGaXh0dXJlIAQoATACCigKC2FiY2QxMjM0eHl6"
            + "EgpVbnNlbGVjdGVkGgdGaXh0dXJlIAEoATACEAEYAyABKNVM";
        var writer = new ZXing.SkiaSharp.BarcodeWriter
        {
            Format = BarcodeFormat.QR_CODE,
            Options = new EncodingOptions { Width = 480, Height = 480 },
        };
        using var bitmap = writer.Write("otpauth-migration://offline?data=" + Uri.EscapeDataString(batch));
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        var image = encoded.ToArray();
        var root = Path.Combine(Path.GetTempPath(), "jrm-mfa-qr-probe-" + Guid.NewGuid().ToString("N"));
        var store = new SshMfaProfileStore(root);
        var state = await OnUiAsync(() =>
        {
            var owner = new Window { ShowInTaskbar = false, Width = 480, Height = 300 };
            var dialog = SshMfaDialog.CreateEditor(store, null,
                () => Task.FromResult<Stream?>(new MemoryStream(image, writable: false)));
            owner.Show();
            return (Owner: owner, Dialog: dialog, Result: dialog.ShowDialog<Guid?>(owner));
        });

        T Find<T>(string name) where T : Control =>
            state.Dialog.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
        void Click(string name) => Find<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        try
        {
            await OnUiAsync(() => { Click("SshMfaOpenQrButton"); return true; });
            var ready = false;
            for (var attempt = 0; attempt < 100 && !ready; attempt++)
            {
                await Task.Delay(40);
                ready = await OnUiAsync(() => Find<Button>("SshMfaOpenQrButton").IsEnabled);
            }
            if (!ready)
                return ToolText("FAIL: QR import did not finish.", true);

            var checks = await OnUiAsync(() =>
            {
                var choices = Find<StackPanel>("SshMfaQrImportChoices").Children.OfType<CheckBox>().ToArray();
                var save = Find<Button>("SshMfaSaveButton");
                var results = new Dictionary<string, bool>
                {
                    ["onlySupportedAccounts"] = choices.Select(check => (check.Content as TextBlock)?.Text)
                        .SequenceEqual(["Fixture: jump@example.test", "Fixture: Deploy", "Fixture: Unselected"]),
                    ["requiresExplicitSelection"] = choices.All(check => check.IsChecked != true) && !save.IsEnabled,
                    ["seedBoxIsEmpty"] = string.IsNullOrEmpty(Find<TextBox>("SshMfaSeedBox").Text),
                    ["noProfileBeforeConfirmation"] = store.List().Count == 0,
                };
                if (!results["onlySupportedAccounts"])
                    return results;
                choices[0].SetCurrentValue(CheckBox.IsCheckedProperty, true);
                results["oneSelectionEnablesImport"] = save.IsEnabled;
                choices[0].SetCurrentValue(CheckBox.IsCheckedProperty, false);
                results["clearingSelectionDisablesImport"] = !save.IsEnabled;
                choices[0].SetCurrentValue(CheckBox.IsCheckedProperty, true);
                choices[1].SetCurrentValue(CheckBox.IsCheckedProperty, true);
                Click("SshMfaSaveButton");
                return results;
            });

            if (checks["onlySupportedAccounts"])
            {
                var id = await state.Result.WaitAsync(TimeSpan.FromSeconds(3));
                var profiles = store.Read().ToDictionary(profile => profile.Name, StringComparer.Ordinal);
                checks["onlySelectedProfilesSaved"] = id.HasValue && profiles.Count == 2
                    && profiles.TryGetValue("Fixture: jump@example.test", out var first) && first.Id == id
                    && profiles.ContainsKey("Fixture: Deploy")
                    && profiles.Values.All(profile => MasterKeyService.IsPasswordBlob(profile.EncryptedCredential));

                bool CheckCode(string profileName, string expected)
                {
                    var setup = PasswordProtector.Decrypt(profiles.GetValueOrDefault(profileName)?.EncryptedCredential);
                    var valid = SshTotp.TryParse(setup, out var totp);
                    using (totp)
                        return valid && totp!.Generate(DateTimeOffset.FromUnixTimeSeconds(59)) == expected;
                }
                checks["selectedTotpCodesMatch"] = CheckCode("Fixture: jump@example.test", "287082")
                    && CheckCode("Fixture: Deploy", "46119246");
            }

            var passed = checks.Values.All(value => value);
            return ToolText($"{(passed ? "PASS" : "FAIL")}: SSH MFA QR import\n"
                + string.Join("\n", checks.Select(check => $"{check.Key}={check.Value}")), !passed);
        }
        finally
        {
            await OnUiAsync(() =>
            {
                if (state.Dialog.IsVisible)
                    state.Dialog.Close();
                state.Owner.Close();
                return true;
            });
            if (Directory.Exists(root))
                try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
}
