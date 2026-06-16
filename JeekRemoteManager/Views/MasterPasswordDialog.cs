using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Jeek.Avalonia.Localization;

namespace JeekRemoteManager.Views;

/// <summary>
/// Modal windows for setting, entering and changing the master password. Built in
/// code so they match the project's other dialogs (see <see cref="MainWindow"/>).
/// </summary>
public static class MasterPasswordDialog
{
    private const char Mask = '●'; // ●

    /// <summary>
    /// Prompts the user to choose a new master password (with confirmation). Returns
    /// the chosen password, or null if cancelled. <paramref name="owner"/> may be null
    /// at startup, in which case the window is shown standalone.
    /// </summary>
    public static Task<string?> ShowSetupAsync(Window? owner) =>
        ShowNewPasswordAsync(owner, Localizer.Get("MasterSetupTitle"), Localizer.Get("MasterSetupPrompt"));

    /// <summary>
    /// Prompts for a new master password to replace the current one. Returns the new
    /// password, or null if cancelled.
    /// </summary>
    public static Task<string?> ShowChangeAsync(Window owner) =>
        ShowNewPasswordAsync(owner, Localizer.Get("MasterChangeTitle"), Localizer.Get("MasterChangePrompt"));

    private static Task<string?> ShowNewPasswordAsync(Window? owner, string title, string prompt)
    {
        var tcs = new TaskCompletionSource<string?>();

        var newBox = new TextBox { PasswordChar = Mask };
        var confirmBox = new TextBox { PasswordChar = Mask };
        var reveal = new CheckBox { Content = Localizer.Get("MasterShowPassword") };
        reveal.IsCheckedChanged += (_, _) =>
        {
            var show = reveal.IsChecked == true;
            newBox.RevealPassword = show;
            confirmBox.RevealPassword = show;
        };

        var error = new TextBlock
        {
            Foreground = Brushes.IndianRed,
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
        };

        var ok = new Button { Content = Localizer.Get("DialogOk"), MinWidth = 80, IsDefault = true };
        var cancel = new Button { Content = Localizer.Get("DialogCancel"), MinWidth = 80, IsCancel = true };

        var dialog = new Window
        {
            Title = title,
            Width = 420,
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
                    new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = Localizer.Get("MasterNewPassword") },
                    newBox,
                    new TextBlock { Text = Localizer.Get("MasterConfirmPassword") },
                    confirmBox,
                    reveal,
                    error,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { ok, cancel },
                    },
                },
            },
        };

        ok.Click += (_, _) =>
        {
            var pw = newBox.Text ?? "";
            if (pw.Length == 0)
            {
                Fail(error, Localizer.Get("MasterErrorEmpty"));
                return;
            }
            if (pw != (confirmBox.Text ?? ""))
            {
                Fail(error, Localizer.Get("MasterErrorMismatch"));
                return;
            }

            tcs.TrySetResult(pw);
            dialog.Close();
        };
        cancel.Click += (_, _) => { tcs.TrySetResult(null); dialog.Close(); };
        dialog.Closed += (_, _) => tcs.TrySetResult(null);
        dialog.Opened += (_, _) => newBox.Focus();

        Show(dialog, owner);
        return tcs.Task;
    }

    /// <summary>
    /// Prompts for the master password, validating each attempt via
    /// <paramref name="validate"/> (which should unlock on success). The window only
    /// closes on a correct password or cancellation. Returns true once unlocked,
    /// false if the user cancelled.
    /// </summary>
    public static Task<bool> ShowUnlockAsync(Window? owner, Func<string, bool> validate)
    {
        var tcs = new TaskCompletionSource<bool>();

        var box = new TextBox { PasswordChar = Mask };
        var reveal = new CheckBox { Content = Localizer.Get("MasterShowPassword") };
        reveal.IsCheckedChanged += (_, _) => box.RevealPassword = reveal.IsChecked == true;

        var error = new TextBlock
        {
            Foreground = Brushes.IndianRed,
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
        };

        var ok = new Button { Content = Localizer.Get("DialogOk"), MinWidth = 80, IsDefault = true };
        var cancel = new Button { Content = Localizer.Get("DialogCancel"), MinWidth = 80, IsCancel = true };

        var dialog = new Window
        {
            Title = Localizer.Get("MasterUnlockTitle"),
            Width = 420,
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
                    new TextBlock { Text = Localizer.Get("MasterUnlockPrompt"), TextWrapping = TextWrapping.Wrap },
                    box,
                    reveal,
                    error,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { ok, cancel },
                    },
                },
            },
        };

        ok.Click += (_, _) =>
        {
            if (validate(box.Text ?? ""))
            {
                tcs.TrySetResult(true);
                dialog.Close();
            }
            else
            {
                Fail(error, Localizer.Get("MasterErrorWrong"));
                box.Text = "";
                box.Focus();
            }
        };
        cancel.Click += (_, _) => { tcs.TrySetResult(false); dialog.Close(); };
        dialog.Closed += (_, _) => tcs.TrySetResult(false);
        dialog.Opened += (_, _) => box.Focus();

        Show(dialog, owner);
        return tcs.Task;
    }

    private static void Fail(TextBlock error, string message)
    {
        error.Text = message;
        error.IsVisible = true;
    }

    private static void Show(Window dialog, Window? owner)
    {
        if (owner is null)
            dialog.Show();
        else
            dialog.ShowDialog(owner);
    }
}
