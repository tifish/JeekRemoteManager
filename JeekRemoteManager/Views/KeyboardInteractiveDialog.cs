using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Threading;
using Jeek.Avalonia.Localization;
using JeekRemoteManager.Services;

namespace JeekRemoteManager.Views;

/// <summary>
/// SSH keyboard-interactive challenge (OTP / second factor / anything the stored
/// password cannot answer). Blocking from the handshake thread, same shape as
/// <see cref="HostKeyDialog"/>: post the window to the UI thread and wait.
/// </summary>
public static class KeyboardInteractiveDialog
{
    private const char Mask = '●';

    /// <summary>
    /// Blocking prompt callable from the SSH handshake thread. Safe only when
    /// called off the UI thread (SSH connect runs on a background thread).
    /// Returns the entered text, or null when the user cancels.
    /// </summary>
    public static string? Prompt(SshConnectionFactory.KeyboardInteractiveChallenge challenge)
    {
        var tcs = new TaskCompletionSource<string?>();
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                tcs.SetResult(await ShowAsync(OwnerWindow(), challenge));
            }
            catch
            {
                tcs.SetResult(null);
            }
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    private static Window? OwnerWindow()
    {
        var window = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
            ?.MainWindow;
        if (window is null)
            return null;

        // The handshake blocks a worker; if the main window is hidden or
        // minimized the dialog would otherwise sit behind nothing.
        window.Show();
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Activate();
        return window;
    }

    public static Task<string?> ShowAsync(
        Window? owner,
        SshConnectionFactory.KeyboardInteractiveChallenge challenge)
    {
        var tcs = new TaskCompletionSource<string?>();

        var request = string.IsNullOrWhiteSpace(challenge.Request)
            ? Localizer.Get("SshAuthResponse")
            : challenge.Request.Trim();

        var box = new TextBox();
        if (!challenge.IsEchoed)
            box.PasswordChar = Mask;

        var reveal = new CheckBox
        {
            Content = Localizer.Get("SshAuthShow"),
            IsVisible = !challenge.IsEchoed,
        };
        reveal.IsCheckedChanged += (_, _) =>
            box.RevealPassword = reveal.IsChecked == true;

        var ok = new Button { Content = Localizer.Get("DialogOk"), MinWidth = 80, IsDefault = true };
        var cancel = new Button { Content = Localizer.Get("DialogCancel"), MinWidth = 80, IsCancel = true };
        ok.Classes.Add("accent");

        var body = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 10,
        };
        body.Children.Add(new TextBlock
        {
            Text = string.Format(
                Localizer.Get("SshAuthPrompt"),
                $"{challenge.Host}:{challenge.Port}",
                challenge.Username),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        });
        if (!string.IsNullOrWhiteSpace(challenge.Instruction))
        {
            body.Children.Add(new TextBlock
            {
                Text = challenge.Instruction.Trim(),
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Classes = { "hint" },
            });
        }

        body.Children.Add(new TextBlock { Text = request, Classes = { "label" } });
        body.Children.Add(box);
        body.Children.Add(reveal);
        body.Children.Add(new TextBlock
        {
            Text = Localizer.Get("SshAuthHint"),
            Classes = { "hint" },
        });
        body.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { ok, cancel },
        });

        var dialog = new Window
        {
            Title = Localizer.Get("SshAuthTitle"),
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = owner is null
                ? WindowStartupLocation.CenterScreen
                : WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = body,
        };

        ok.Click += (_, _) =>
        {
            tcs.TrySetResult(box.Text ?? "");
            dialog.Close();
        };
        cancel.Click += (_, _) =>
        {
            tcs.TrySetResult(null);
            dialog.Close();
        };
        dialog.Closed += (_, _) => tcs.TrySetResult(null);
        dialog.Opened += (_, _) => box.Focus();

        if (owner is null)
            dialog.Show();
        else
            dialog.ShowDialog(owner);

        return tcs.Task;
    }
}
