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

        // Synthetic Google Authenticator export with two TOTP accounts, one HOTP,
        // one unsupported algorithm, and the metadata for page 2 of 3.
        const string batch = "CjgKFDEyMzQ1Njc4OTAxMjM0NTY3ODkwEhFqdW1wQGV4YW1wbGUudGVzdBoHRml4dHVyZSABKAEwAgon"
            + "CglvYXRoLW9ubHkSC0hPVFAgc2FtcGxlGgdGaXh0dXJlIAEoATABCjkKIDEyMzQ1Njc4OTAxMjM0NTY3ODkwMTIzNDU2Nzg5"
            + "MDEyEgZEZXBsb3kaB0ZpeHR1cmUgAigCMAIKIAoDYmFkEgpNRDUgc2FtcGxlGgdGaXh0dXJlIAQoATACEAEYAyABKNVM";
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
                var choices = Find<ComboBox>("SshMfaQrAccounts");
                var results = new Dictionary<string, bool>
                {
                    ["twoSupportedAccounts"] = choices.Items.OfType<string>()
                        .SequenceEqual(["Fixture: jump@example.test", "Fixture: Deploy"]),
                    ["requiresExplicitSelection"] = choices.SelectedIndex < 0 && !Find<Button>("SshMfaSaveButton").IsEnabled,
                    ["seedBoxIsEmpty"] = string.IsNullOrEmpty(Find<TextBox>("SshMfaSeedBox").Text),
                    ["noProfileBeforeConfirmation"] = store.List().Count == 0,
                };
                if (!results["twoSupportedAccounts"])
                    return results;
                choices.SelectedIndex = 1;
                results["suggestsSelectedAccountName"] = Find<TextBox>("SshMfaNameBox").Text == "Fixture: Deploy";
                Click("SshMfaSaveButton");
                return results;
            });

            if (checks["twoSupportedAccounts"])
            {
                var id = await state.Result.WaitAsync(TimeSpan.FromSeconds(3));
                var profile = store.Read().SingleOrDefault();
                checks["onlySelectedProfileSaved"] = id.HasValue && profile is not null && profile.Id == id
                    && profile.Name == "Fixture: Deploy" && MasterKeyService.IsPasswordBlob(profile.EncryptedCredential);
                var setup = PasswordProtector.Decrypt(profile?.EncryptedCredential);
                var valid = SshTotp.TryParse(setup, out var totp);
                using (totp)
                    checks["selectedTotpMatches"] = valid && totp!.Generate(DateTimeOffset.FromUnixTimeSeconds(59)) == "46119246";
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
