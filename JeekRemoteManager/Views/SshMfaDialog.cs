using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Jeek.Avalonia.Localization;
using JeekRemoteManager.Models;
using JeekRemoteManager.Services;

namespace JeekRemoteManager.Views;

internal static class SshMfaDialog
{
    internal static Task ShowManagerAsync(Window owner, SshMfaProfileStore store,
        Func<Window, string, string, Task<bool>> confirm)
    {
        var list = new ListBox
        {
            Name = "SshMfaProfileList",
            MinHeight = 110,
            MaxHeight = 300,
            ItemTemplate = new FuncDataTemplate<SshMfaProfileChoice>((profile, _) =>
                new TextBlock { Text = profile?.Name, TextTrimming = TextTrimming.CharacterEllipsis }),
        };
        var error = CreateError();
        var add = new Button { Name = "SshMfaAddButton", Content = Localizer.Get("SshMfaAdd"), Classes = { "accent" } };
        var edit = new Button { Name = "SshMfaEditButton", Content = Localizer.Get("Edit"), IsEnabled = false };
        var remove = new Button { Name = "SshMfaRemoveButton", Content = Localizer.Get("SshMfaRemove"), IsEnabled = false };
        var close = new Button { Content = Localizer.Get("Close"), IsCancel = true };
        var manager = CreateWindow("SshMfaManagerTitle", list, error, Buttons(add, edit, remove, close));

        void Reload(Guid? selectedId = null)
        {
            try
            {
                var profiles = store.List();
                list.ItemsSource = profiles;
                list.SelectedItem = profiles.FirstOrDefault(profile => profile.Id == selectedId);
                error.IsVisible = false;
                add.IsEnabled = true;
            }
            catch (Exception ex) when (IsStoreError(ex))
            {
                list.ItemsSource = null;
                add.IsEnabled = false;
                ShowError(error, "SshMfaStoreError");
            }
        }

        async Task EditAsync(SshMfaProfileChoice? profile)
        {
            var id = await CreateEditor(store, profile).ShowDialog<Guid?>(manager);
            if (id.HasValue)
                Reload(id);
        }

        list.SelectionChanged += (_, _) => edit.IsEnabled = remove.IsEnabled = list.SelectedItem is not null;
        add.Click += async (_, _) => await EditAsync(null);
        edit.Click += async (_, _) =>
        {
            if (list.SelectedItem is SshMfaProfileChoice profile)
                await EditAsync(profile);
        };
        remove.Click += async (_, _) =>
        {
            if (list.SelectedItem is not SshMfaProfileChoice { Id: { } id } profile
                || !await confirm(manager, Localizer.Get("DialogDeleteTitle"),
                    string.Format(Localizer.Get("SshMfaRemovePrompt"), profile.Name)))
            {
                return;
            }
            try
            {
                store.Remove(id);
                Reload();
            }
            catch (Exception ex) when (IsStoreError(ex))
            {
                ShowError(error, "SshMfaStoreError");
            }
        };
        close.Click += (_, _) => manager.Close();
        Reload();
        return manager.ShowDialog(owner);
    }

    private static Window CreateEditor(SshMfaProfileStore store, SshMfaProfileChoice? profile)
    {
        var name = new TextBox { Name = "SshMfaNameBox", Text = profile?.Name };
        var seed = new TextBox
        {
            Name = "SshMfaSeedBox",
            PasswordChar = '●',
            IsUndoEnabled = false,
            PlaceholderText = Localizer.Get(profile is null ? "SshMfaSeedPlaceholder" : "SshMfaKeepSeedPlaceholder"),
        };
        var error = CreateError();
        var save = new Button
        {
            Name = "SshMfaSaveButton",
            Content = Localizer.Get("DialogOk"),
            IsDefault = true,
            Classes = { "accent" },
        };
        var cancel = new Button { Content = Localizer.Get("DialogCancel"), IsCancel = true };
        var dialog = CreateWindow(profile is null ? "SshMfaAdd" : "SshMfaEditTitle",
            new TextBlock { Text = Localizer.Get("Name"), Classes = { "label" } }, name,
            new TextBlock { Text = Localizer.Get("SshMfaHint"), TextWrapping = TextWrapping.Wrap },
            seed, error, Buttons(save, cancel));

        save.Click += (_, _) =>
        {
            var profileName = name.Text?.Trim() ?? "";
            if (profileName.Length == 0)
            {
                ShowError(error, "SshMfaNameRequired");
                return;
            }

            string? encrypted = null;
            var input = seed.Text?.Trim() ?? "";
            if (profile is null || input.Length > 0)
            {
                if (!SshTotp.TryParse(input, out var credential))
                {
                    ShowError(error, "SshMfaInvalidSeed");
                    return;
                }
                credential.Dispose();
                try { encrypted = PasswordProtector.Encrypt(input); }
                catch (InvalidOperationException)
                {
                    ShowError(error, "SshMfaEncryptionFailed");
                    return;
                }
                if (string.IsNullOrEmpty(encrypted))
                {
                    ShowError(error, "SshMfaEncryptionFailed");
                    return;
                }
            }
            try
            {
                if (store.Save(profile?.Id, profileName, encrypted) is { } id)
                    dialog.Close(id);
                else
                    ShowError(error, "SshMfaNameConflict");
            }
            catch (Exception ex) when (IsStoreError(ex))
            {
                ShowError(error, "SshMfaStoreError");
            }
        };
        cancel.Click += (_, _) => dialog.Close();
        dialog.Opened += (_, _) => name.Focus();
        dialog.Closed += (_, _) => seed.Clear();
        return dialog;
    }

    private static Window CreateWindow(string title, params Control[] controls)
    {
        var content = new StackPanel { Margin = new Thickness(20), Spacing = 12 };
        content.Children.AddRange(controls);
        return new Window
        {
            Title = Localizer.Get(title),
            Width = 470,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = content,
        };
    }

    private static StackPanel Buttons(params Button[] buttons)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };
        panel.Children.AddRange(buttons);
        return panel;
    }

    private static TextBlock CreateError() => new()
    {
        Name = "SshMfaError",
        IsVisible = false,
        TextWrapping = TextWrapping.Wrap,
        Foreground = Brushes.DarkOrange,
    };

    private static void ShowError(TextBlock error, string key)
    {
        error.Text = Localizer.Get(key);
        error.IsVisible = true;
    }

    private static bool IsStoreError(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or TimeoutException;
}
