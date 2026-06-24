using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Jeek.Avalonia.Localization;

namespace JeekRemoteManager.Views;

/// <summary>
/// First-time host-key trust prompt. Shows the server and its SHA256 fingerprint
/// and asks whether to trust and remember it. Defaults to "Reject" (Enter and
/// Escape both reject) so an absent-minded confirmation can't trust an unknown key.
/// </summary>
public static class HostKeyDialog
{
    /// <summary>
    /// Blocking trust prompt callable from the SSH handshake thread: posts the
    /// dialog to the UI thread and waits for the answer. Safe only when called off
    /// the UI thread (the SSH connect runs on a background thread).
    /// </summary>
    public static bool PromptTrust(string host, int port, string keyType, string fingerprintSha256)
    {
        var tcs = new TaskCompletionSource<bool>();
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                tcs.SetResult(await ShowAsync(OwnerWindow(), host, port, keyType, fingerprintSha256));
            }
            catch
            {
                tcs.SetResult(false);
            }
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    private static Window? OwnerWindow() =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    public static Task<bool> ShowAsync(Window? owner, string host, int port, string keyType, string fingerprintSha256)
    {
        var tcs = new TaskCompletionSource<bool>();

        var trust = new Button { Content = Localizer.Get("HostKeyTrust"), MinWidth = 90 };
        var reject = new Button
        {
            Content = Localizer.Get("HostKeyReject"),
            MinWidth = 90,
            IsDefault = true,
            IsCancel = true,
        };

        var dialog = new Window
        {
            Title = Localizer.Get("HostKeyTitle"),
            Width = 480,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = owner is null
                ? WindowStartupLocation.CenterScreen
                : WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(16),
                Spacing = 10,
                Children =
                {
                    new TextBlock
                    {
                        Text = string.Format(Localizer.Get("HostKeyPrompt"), $"{host}:{port}", keyType),
                        TextWrapping = TextWrapping.Wrap,
                    },
                    new SelectableTextBlock
                    {
                        Text = $"SHA256:{fingerprintSha256}",
                        FontFamily = new FontFamily("Consolas"),
                        TextWrapping = TextWrapping.Wrap,
                    },
                    new TextBlock
                    {
                        Text = Localizer.Get("HostKeyHint"),
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = Brushes.Gray,
                        FontSize = 12,
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { trust, reject },
                    },
                },
            },
        };

        trust.Click += (_, _) => { tcs.TrySetResult(true); dialog.Close(); };
        reject.Click += (_, _) => { tcs.TrySetResult(false); dialog.Close(); };
        dialog.Closed += (_, _) => tcs.TrySetResult(false);

        if (owner is null)
            dialog.Show();
        else
            dialog.ShowDialog(owner);

        return tcs.Task;
    }
}
