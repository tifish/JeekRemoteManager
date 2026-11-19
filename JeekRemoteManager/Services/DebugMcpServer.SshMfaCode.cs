using System.Text;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Jeek.Avalonia.Localization;
using JeekRemoteManager.Models;
using JeekRemoteManager.Views;

namespace JeekRemoteManager.Services;

internal static partial class DebugMcpServer
{
    private static async Task<JsonObject> SshMfaCodeCheckAsync()
    {
        if (!DebugInstanceContext.IsDebugBuild)
            return ToolText("Run this probe in a Debug instance.", true);

        const string seed = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";
        const string customSetup = "otpauth://totp/Fixture?secret=GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQGEZA"
            + "&algorithm=SHA256&digits=8&period=60";
        var root = Path.Combine(Path.GetTempPath(), "jrm-mfa-code-" + Guid.NewGuid().ToString("N"));
        var store = new SshMfaProfileStore(root);
        var standard = store.Save(null, "Standard", PasswordProtector.Encrypt(seed));
        var custom = store.Save(null, "Custom", PasswordProtector.Encrypt(customSetup));
        var locked = store.Save(null, "Unreadable", MasterKeyService.EncryptWithPassword("different fixture master", seed));
        var clock = new MfaCodeProbeClock { Now = DateTimeOffset.FromUnixTimeSeconds(59) };
        string? copied = null;
        var state = await OnUiAsync(() =>
        {
            var owner = new Window { Width = 480, Height = 300, ShowInTaskbar = false };
            owner.Show();
            var closed = SshMfaDialog.ShowManagerAsync(owner, store, (_, _, _) => Task.FromResult(false), clock, value =>
            {
                copied = value;
                return Task.CompletedTask;
            });
            return (Owner: owner, Manager: owner.OwnedWindows.Single(), Closed: closed);
        });
        T Find<T>(string name) where T : Control =>
            state.Manager.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
        void Select(Guid? id)
        {
            var list = Find<ListBox>("SshMfaProfileList");
            list.SelectedItem = list.Items.OfType<SshMfaProfileChoice>().Single(profile => profile.Id == id);
        }
        TextBlock Code() => Find<TextBlock>(SshMfaDialog.CurrentCodeElementName);
        Button CopyButton() => Find<Button>("SshMfaCopyCodeButton");
        string Remaining() => Find<TextBlock>("SshMfaCodeRemaining").Text ?? "";

        var checks = new Dictionary<string, bool>();
        try
        {
            await OnUiAsync(() =>
            {
                checks["copyDisabledWithoutSelection"] = !CopyButton().IsEnabled;
                Select(standard);
                checks["selectedProfileShowsCurrentCode"] = Code().Text == "287082" && CopyButton().IsEnabled;
                checks["countdownMatchesCurrentPeriod"] =
                    Remaining() == string.Format(Localizer.Get("SshMfaCodeRemaining"), 1);

                var tree = new StringBuilder();
                var count = 0;
                AppendVisual(tree, state.Manager, 0, 16, null, ref count);
                checks["visualTreeOmitsTheCode"] = tree.ToString().Contains(SshMfaDialog.CurrentCodeElementName, StringComparison.Ordinal)
                    && !tree.ToString().Contains("287082", StringComparison.Ordinal);
                clock.Now = DateTimeOffset.FromUnixTimeSeconds(60);
                return true;
            });

            var refreshed = false;
            var deadline = Environment.TickCount64 + 2_000;
            while (!refreshed && Environment.TickCount64 < deadline)
            {
                await Task.Delay(50);
                refreshed = await OnUiAsync(() => Code().Text == "359152");
            }
            checks["codeRefreshesWithoutInteraction"] = refreshed;

            await OnUiAsync(() =>
            {
                clock.Now = DateTimeOffset.FromUnixTimeSeconds(90);
                CopyButton().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                checks["copyUsesTheNewPeriodBeforeTheTimerRefreshes"] = copied == "969429" && Code().Text == "969429";
                checks["copyFeedbackIsShown"] = Find<TextBlock>("SshMfaCopyFeedback").IsVisible;

                clock.Now = DateTimeOffset.FromUnixTimeSeconds(119);
                Select(custom);
                checks["supportsEightDigitsAndCustomPeriods"] = Code().Text == "46119246"
                    && Remaining() == string.Format(Localizer.Get("SshMfaCodeRemaining"), 1);
                Select(locked);
                checks["unreadableProfileClearsCodeAndDisablesCopy"] = Code().Text == "—" && !CopyButton().IsEnabled;
                return true;
            });
            var passed = checks.Values.All(value => value);
            return ToolText($"{(passed ? "PASS" : "FAIL")}: SSH MFA code display and copy\n"
                + string.Join("\n", checks.Select(check => $"{check.Key}={check.Value}")), !passed);
        }
        finally
        {
            await OnUiAsync(() =>
            {
                state.Manager.Close();
                state.Owner.Close();
                return true;
            });
            await state.Closed;
            if (Directory.Exists(root))
                try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private sealed class MfaCodeProbeClock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; }
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
