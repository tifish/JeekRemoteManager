using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Jeek.Avalonia.Localization;

namespace JeekRemoteManager.Views;

/// <summary>
/// A single password-entry dialog used for every master-password interaction
/// (first-time setup, day-to-day unlock, and changing). Two password boxes are
/// shown and must match before <paramref name="submit"/> is consulted — typing
/// the password twice is intentionally required everywhere, since the cost of a
/// silent typo (locking yourself out, or corrupting stored secrets by re-saving
/// under a slightly-wrong key) outweighs the small cost of re-typing.
/// </summary>
public static class MasterPasswordDialog
{
    private const char Mask = '●';

    /// <summary>
    /// Shows the dialog. <paramref name="submit"/> validates/acts on the entered
    /// password and returns whether it was accepted. Returns true once accepted,
    /// false if the user cancelled. <paramref name="owner"/> may be null at startup.
    /// </summary>
    public static Task<bool> ShowAsync(Window? owner, string title, string prompt, Func<string, bool> submit)
    {
        var tcs = new TaskCompletionSource<bool>();

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

            if (submit(pw))
            {
                tcs.TrySetResult(true);
                dialog.Close();
            }
            else
            {
                Fail(error, Localizer.Get("MasterErrorWrong"));
                newBox.Text = "";
                confirmBox.Text = "";
                newBox.Focus();
            }
        };
        cancel.Click += (_, _) => { tcs.TrySetResult(false); dialog.Close(); };
        dialog.Closed += (_, _) => tcs.TrySetResult(false);
        dialog.Opened += (_, _) => newBox.Focus();

        if (owner is null)
            dialog.Show();
        else
            dialog.ShowDialog(owner);

        return tcs.Task;
    }

    /// <summary>
    /// Shows a single-field master-password prompt used for verification
    /// (e.g. unlocking "Show password" in the editor). <paramref name="verify"/>
    /// checks the entered password and returns whether it matched. Returns true
    /// on a successful verify, false if the user cancelled.
    /// </summary>
    public static Task<bool> ShowVerifyAsync(Window? owner, string title, string prompt, Func<string, bool> verify)
    {
        var tcs = new TaskCompletionSource<bool>();

        var passwordBox = new TextBox { PasswordChar = Mask };
        var reveal = new CheckBox { Content = Localizer.Get("MasterShowPassword") };
        reveal.IsCheckedChanged += (_, _) =>
            passwordBox.RevealPassword = reveal.IsChecked == true;

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
                    passwordBox,
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
            var pw = passwordBox.Text ?? "";
            if (pw.Length == 0)
            {
                Fail(error, Localizer.Get("MasterErrorEmpty"));
                return;
            }

            if (verify(pw))
            {
                tcs.TrySetResult(true);
                dialog.Close();
            }
            else
            {
                Fail(error, Localizer.Get("MasterErrorWrong"));
                passwordBox.Text = "";
                passwordBox.Focus();
            }
        };
        cancel.Click += (_, _) => { tcs.TrySetResult(false); dialog.Close(); };
        dialog.Closed += (_, _) => tcs.TrySetResult(false);
        dialog.Opened += (_, _) => passwordBox.Focus();

        if (owner is null)
            dialog.Show();
        else
            dialog.ShowDialog(owner);

        return tcs.Task;
    }

    private static void Fail(TextBlock error, string message)
    {
        error.Text = message;
        error.IsVisible = true;
    }
}
