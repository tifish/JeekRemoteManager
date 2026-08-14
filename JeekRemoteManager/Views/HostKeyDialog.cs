using System;
using System.Collections.Generic;
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
/// Shows the remembered and presented SSH host-key fingerprints and asks whether
/// to replace the remembered key. Defaults to "Reject" (Enter and Escape reject).
/// </summary>
public static class HostKeyDialog
{
    /// <summary>Blocking replacement prompt for a changed remembered host key.</summary>
    public static bool PromptReplace(string host, int port, string keyType, string oldFingerprintSha256, string newFingerprintSha256)
    {
        var tcs = new TaskCompletionSource<bool>();
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                tcs.SetResult(await ShowAsync(OwnerWindow(), host, port, keyType, oldFingerprintSha256, newFingerprintSha256));
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

    public static Task<bool> ShowAsync(Window? owner, string host, int port, string keyType,
        string oldFingerprintSha256, string newFingerprintSha256)
    {
        var tcs = new TaskCompletionSource<bool>();

        var replace = new Button { Content = Localizer.Get("HostKeyReplace"), MinWidth = 90 };
        var reject = new Button
        {
            Content = Localizer.Get("HostKeyReject"),
            MinWidth = 90,
            IsDefault = true,
            IsCancel = true,
        };
        replace.Classes.Add("accent");

        var hint = new TextBlock
        {
            Text = Localizer.Get("HostKeyHint"),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Classes = { "hint" },
        };

        var children = new List<Control>
        {
            new TextBlock
            {
                Text = string.Format(Localizer.Get("HostKeyChangedPrompt"), $"{host}:{port}", keyType),
                TextWrapping = TextWrapping.Wrap,
            },
            new SelectableTextBlock
            {
                Text = string.Format(Localizer.Get("HostKeyPrevious"), $"SHA256:{oldFingerprintSha256}"),
                FontFamily = new FontFamily("Consolas"),
                TextWrapping = TextWrapping.Wrap,
            },
            new SelectableTextBlock
            {
                Text = $"SHA256:{newFingerprintSha256}",
                FontFamily = new FontFamily("Consolas"),
                TextWrapping = TextWrapping.Wrap,
            },
            hint,
            new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 8,
                Children = { replace, reject },
            },
        };

        var content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 10,
        };
        content.Children.AddRange(children);

        var dialog = new Window
        {
            Title = Localizer.Get("HostKeyChangedTitle"),
            Width = 480,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = owner is null
                ? WindowStartupLocation.CenterScreen
                : WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = content,
        };

        replace.Click += (_, _) => { tcs.TrySetResult(true); dialog.Close(); };
        reject.Click += (_, _) => { tcs.TrySetResult(false); dialog.Close(); };
        dialog.Closed += (_, _) => tcs.TrySetResult(false);

        if (owner is null)
            dialog.Show();
        else
            dialog.ShowDialog(owner);

        return tcs.Task;
    }
}
