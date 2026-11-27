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

    private static Window? OwnerWindow()
    {
        var owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is MainWindow mainWindow)
            mainWindow.ActivateMainWindow();
        return owner;
    }

    public static Task<bool> ShowAsync(Window? owner, string host, int port, string keyType,
        string oldFingerprintSha256, string newFingerprintSha256)
    {
        var tcs = new TaskCompletionSource<bool>();

        var replace = new Button
        {
            Content = Localizer.Get("HostKeyReplace"),
            MinWidth = 96,
            Classes = { "danger" },
        };
        var reject = new Button
        {
            Content = Localizer.Get("HostKeyReject"),
            MinWidth = 96,
            IsDefault = true,
            IsCancel = true,
            Classes = { "accent" },
        };

        var hint = new TextBlock
        {
            Text = Localizer.Get("HostKeyHint"),
            TextWrapping = TextWrapping.Wrap,
            Classes = { "hint" },
        };

        var fingerprintsCard = new Border
        {
            Classes = { "form-card" },
            Padding = new Avalonia.Thickness(12, 10),
            Child = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new SelectableTextBlock
                    {
                        Text = string.Format(Localizer.Get("HostKeyPrevious"), $"SHA256:{oldFingerprintSha256}"),
                        Classes = { "mono" },
                        FontSize = 12,
                        TextWrapping = TextWrapping.Wrap,
                    },
                    new SelectableTextBlock
                    {
                        Text = $"SHA256:{newFingerprintSha256}",
                        Classes = { "mono" },
                        FontSize = 12,
                        FontWeight = FontWeight.SemiBold,
                        TextWrapping = TextWrapping.Wrap,
                    },
                },
            },
        };

        // Keep destructive "Replace" visually separated on the left from the safe default "Reject" on the right.
        var actions = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Margin = new Avalonia.Thickness(0, 4, 0, 0),
            Children = { replace },
        };
        Grid.SetColumn(reject, 2);
        actions.Children.Add(reject);

        var children = new List<Control>
        {
            new TextBlock
            {
                Text = string.Format(Localizer.Get("HostKeyChangedPrompt"), $"{host}:{port}", keyType),
                TextWrapping = TextWrapping.Wrap,
            },
            fingerprintsCard,
            hint,
            actions,
        };

        var content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 12,
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
