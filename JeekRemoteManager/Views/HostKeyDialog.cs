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
/// Host-key trust prompt. Shows the server and its SHA256 fingerprint and asks
/// whether to trust a new key or replace a remembered key. Defaults to "Reject"
/// (Enter and Escape both reject).
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

    /// <summary>Blocking replacement prompt for a changed remembered host key.</summary>
    public static bool PromptReplace(string host, int port, string keyType, string oldFingerprintSha256, string newFingerprintSha256)
    {
        var tcs = new TaskCompletionSource<bool>();
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                tcs.SetResult(await ShowAsync(OwnerWindow(), host, port, keyType, newFingerprintSha256, oldFingerprintSha256, true));
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

    public static Task<bool> ShowAsync(Window? owner, string host, int port, string keyType, string fingerprintSha256,
        string? oldFingerprintSha256 = null, bool replacing = false)
    {
        var tcs = new TaskCompletionSource<bool>();

        var trust = new Button { Content = Localizer.Get(replacing ? "HostKeyReplace" : "HostKeyTrust"), MinWidth = 90 };
        var reject = new Button
        {
            Content = Localizer.Get("HostKeyReject"),
            MinWidth = 90,
            IsDefault = true,
            IsCancel = true,
        };
        trust.Classes.Add("accent");

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
                Text = string.Format(Localizer.Get(replacing ? "HostKeyChangedPrompt" : "HostKeyPrompt"), $"{host}:{port}", keyType),
                TextWrapping = TextWrapping.Wrap,
            },
        };
        if (replacing)
        {
            children.Add(new SelectableTextBlock
            {
                Text = string.Format(Localizer.Get("HostKeyPrevious"), $"SHA256:{oldFingerprintSha256}"),
                FontFamily = new FontFamily("Consolas"),
                TextWrapping = TextWrapping.Wrap,
            });
        }
        children.AddRange(new Control[]
        {
            new SelectableTextBlock
            {
                Text = $"SHA256:{fingerprintSha256}",
                FontFamily = new FontFamily("Consolas"),
                TextWrapping = TextWrapping.Wrap,
            },
            hint,
            new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 8,
                Children = { trust, reject },
            },
        });

        var content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 10,
        };
        content.Children.AddRange(children);

        var dialog = new Window
        {
            Title = Localizer.Get(replacing ? "HostKeyChangedTitle" : "HostKeyTitle"),
            Width = 480,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = owner is null
                ? WindowStartupLocation.CenterScreen
                : WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = content,
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
