using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
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

    internal static Window CreateEditor(SshMfaProfileStore store, SshMfaProfileChoice? profile,
        Func<Task<Stream?>>? pickQrImage = null)
    {
        var savedInput = profile?.Id is { } id ? store.TryGetProfile(id)?.Input : null;
        var savedMode = savedInput?.Mode ?? SshMfaInputMode.SshAuthentication;
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
        var openQr = new Button { Name = "SshMfaOpenQrButton", Content = Localizer.Get("SshMfaQrOpen") };
        var pasteQr = new Button { Content = Localizer.Get("SshMfaQrPaste") };
        var useKey = new Button { Content = Localizer.Get("SshMfaQrUseKey") };
        var accounts = new ComboBox
        {
            Name = "SshMfaQrAccounts",
            PlaceholderText = Localizer.Get("SshMfaQrChoose"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var checkList = new StackPanel { Name = "SshMfaQrImportChoices", Spacing = 4 };
        var checkScroll = new ScrollViewer
        {
            MaxHeight = 230,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = checkList,
        };
        var nameField = new StackPanel
        {
            Spacing = 6,
            Children = { new TextBlock { Text = Localizer.Get("Name"), Classes = { "label" } }, name },
        };
        var inputMode = new ComboBox
        {
            Name = "SshMfaInputMode",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = new[] { Localizer.Get("SshMfaInputAuthentication"),
                Localizer.Get("SshMfaInputMatchPrompt"), Localizer.Get("SshMfaInputOnShellOpen") },
            SelectedIndex = Enum.IsDefined(savedMode) ? (int)savedMode : 0,
        };
        var inputPrompt = new TextBox
        {
            Name = "SshMfaInputPrompt",
            Text = savedInput?.Prompt,
            PlaceholderText = Localizer.Get("SshMfaInputPromptPlaceholder"),
            IsVisible = savedMode == SshMfaInputMode.MatchPrompt,
        };
        inputMode.SelectionChanged += (_, _) =>
            inputPrompt.IsVisible = inputMode.SelectedIndex == (int)SshMfaInputMode.MatchPrompt;
        var inputField = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = Localizer.Get("SshMfaInputMode"), Classes = { "label" } },
                inputMode, inputPrompt,
            },
        };
        var manual = new StackPanel
        {
            Spacing = 6,
            Children = { new TextBlock { Text = Localizer.Get("SshMfaHint"), TextWrapping = TextWrapping.Wrap }, seed },
        };
        var accountLabel = new TextBlock { Classes = { "label" } };
        var selection = new StackPanel
        {
            Spacing = 6, IsVisible = false,
            Children = { accountLabel, accounts, checkScroll, useKey },
        };
        var dialog = CreateWindow(profile is null ? "SshMfaAdd" : "SshMfaEditTitle",
            nameField, manual, Buttons(openQr, pasteQr), selection, inputField, error, Buttons(save, cancel));

        IReadOnlyList<SshMfaQrReader.Account> imported = [];
        string[] labels = [];
        CheckBox[] checks = [];
        var suggestedName = "";
        var isReadingQr = false;
        bool IsBatchImport() => profile is null && imported.Count > 1;

        void UpdateSaveAvailability() =>
            save.IsEnabled = !isReadingQr && (IsBatchImport()
                ? checks.Any(check => check.IsChecked == true)
                : imported.Count == 0 || accounts.SelectedIndex >= 0);

        void ShowQrAccounts()
        {
            var batch = IsBatchImport();
            checkList.Children.Clear();
            checks = [];
            if (batch)
            {
                checks = labels.Select(label => new CheckBox
                {
                    Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap },
                }).ToArray();
                foreach (var check in checks)
                    check.IsCheckedChanged += (_, _) => UpdateSaveAvailability();
                checkList.Children.AddRange(checks);
            }

            accounts.IsVisible = !batch;
            accounts.ItemsSource = batch ? null : labels;
            accounts.SelectedIndex = imported.Count == 1 ? 0 : -1;
            checkScroll.IsVisible = batch;
            nameField.IsVisible = !batch;
            manual.IsVisible = false;
            selection.IsVisible = true;
            accountLabel.Text = Localizer.Get(batch ? "SshMfaQrChooseMultiple" : "SshMfaQrAccount");
            save.Content = Localizer.Get(batch ? "SshMfaQrImportSelected" : "DialogOk");
            if (batch)
                checks[0].Focus();
            else
                accounts.Focus();
        }

        async Task ReadQrAsync(Func<Task<Stream?>> open, string? emptyError = null)
        {
            if (isReadingQr)
                return;
            isReadingQr = true;
            openQr.IsEnabled = pasteQr.IsEnabled = useKey.IsEnabled = false;
            UpdateSaveAvailability();
            try
            {
                using var image = await open();
                if (image is null)
                {
                    if (emptyError is not null && dialog.IsVisible)
                        ShowError(error, emptyError);
                    return;
                }

                var result = await Task.Run(() => SshMfaQrReader.Read(image, PasswordProtector.Encrypt));
                if (!dialog.IsVisible)
                    return;
                if (result is not { Count: > 0 })
                {
                    ShowError(error, result is null ? "SshMfaQrUnreadable" : "SshMfaQrNoAccounts");
                    return;
                }

                imported = result;
                var names = imported.Select((account, index) => account.Name.Length == 0
                    ? string.Format(Localizer.Get("SshMfaQrUnnamed"), index + 1) : account.Name).ToArray();
                var duplicates = names.GroupBy(label => label, StringComparer.OrdinalIgnoreCase)
                    .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
                labels = names.Select((label, index) =>
                    duplicates.Contains(label) ? $"{label} ({index + 1})" : label).ToArray();
                seed.Clear();
                error.IsVisible = false;
                ShowQrAccounts();
            }
            catch (InvalidOperationException) when (MasterKeyService.Current is not { IsUnlocked: true })
            {
                if (dialog.IsVisible)
                    ShowError(error, "SshMfaEncryptionFailed");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                or InvalidDataException or InvalidOperationException or COMException or TimeoutException)
            {
                if (dialog.IsVisible)
                    ShowError(error, "SshMfaQrUnreadable");
            }
            finally
            {
                isReadingQr = false;
                openQr.IsEnabled = pasteQr.IsEnabled = useKey.IsEnabled = true;
                UpdateSaveAvailability();
            }
        }

        accounts.SelectionChanged += (_, _) =>
        {
            UpdateSaveAvailability();
            if (accounts.SelectedItem is string label && (string.IsNullOrWhiteSpace(name.Text) || name.Text == suggestedName))
                name.Text = suggestedName = label;
        };
        openQr.Click += async (_, _) => await ReadQrAsync(pickQrImage ?? (() => PickQrImageAsync(dialog)));
        pasteQr.Click += async (_, _) => await ReadQrAsync(() => ReadClipboardImageAsync(dialog), "SshMfaQrClipboardEmpty");
        useKey.Click += (_, _) =>
        {
            imported = [];
            checks = [];
            labels = [];
            accounts.ItemsSource = null;
            checkList.Children.Clear();
            selection.IsVisible = false;
            manual.IsVisible = true;
            nameField.IsVisible = true;
            save.Content = Localizer.Get("DialogOk");
            if (name.Text == suggestedName)
                name.Clear();
            suggestedName = "";
            error.IsVisible = false;
            UpdateSaveAvailability();
            seed.Focus();
        };

        save.Click += (_, _) =>
        {
            if (!save.IsEnabled)
                return;

            var mode = inputMode.SelectedIndex < 0 ? SshMfaInputMode.SshAuthentication : (SshMfaInputMode)inputMode.SelectedIndex;
            var promptText = mode == SshMfaInputMode.MatchPrompt ? inputPrompt.Text?.Trim() ?? "" : "";
            if (mode == SshMfaInputMode.MatchPrompt && promptText.Length == 0)
            {
                ShowError(error, "SshMfaInputPromptRequired");
                inputPrompt.Focus();
                return;
            }
            var inputSettings = new SshMfaInput(mode, promptText);

            if (IsBatchImport())
            {
                var selected = Enumerable.Range(0, checks.Length)
                    .Where(index => checks[index].IsChecked == true)
                    .Select(index => (labels[index], imported[index].EncryptedCredential))
                    .ToArray();
                try
                {
                    dialog.Close(store.AddBatch(selected, inputSettings)[0]);
                }
                catch (Exception ex) when (IsStoreError(ex))
                {
                    ShowError(error, "SshMfaStoreError");
                }
                return;
            }

            var profileName = name.Text?.Trim() ?? "";
            if (profileName.Length == 0)
            {
                ShowError(error, "SshMfaNameRequired");
                return;
            }

            string? encrypted = accounts.SelectedIndex >= 0
                ? imported[accounts.SelectedIndex].EncryptedCredential : null;
            var input = seed.Text?.Trim() ?? "";
            if (encrypted is null && (profile is null || input.Length > 0))
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
                if (store.Save(profile?.Id, profileName, encrypted, inputSettings) is { } savedId)
                    dialog.Close(savedId);
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
        dialog.Closed += (_, _) =>
        {
            imported = [];
            checks = [];
            accounts.ItemsSource = null;
            checkList.Children.Clear();
            seed.Clear();
        };
        return dialog;
    }

    private static async Task<Stream?> PickQrImageAsync(Window dialog)
    {
        var files = await dialog.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Localizer.Get("SshMfaQrOpen"),
            FileTypeFilter = [new FilePickerFileType(Localizer.Get("SshMfaQrImages"))
            {
                Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp", "*.gif"],
            }],
        });
        using var file = files.FirstOrDefault();
        return file is null ? null : await file.OpenReadAsync();
    }

    private static async Task<Stream?> ReadClipboardImageAsync(Window dialog)
    {
        if (dialog.Clipboard is not { } clipboard)
            return null;

        using var bitmap = await clipboard.TryGetBitmapAsync();
        if (bitmap is null)
            return null;
        if (!SshMfaQrReader.IsSupportedSize(bitmap.PixelSize.Width, bitmap.PixelSize.Height))
            throw new InvalidDataException();

        return await Task.Run(() =>
        {
            var image = new MemoryStream();
            try
            {
                bitmap.Save(image, PngBitmapEncoderOptions.Default);
                image.Position = 0;
                return image;
            }
            catch
            {
                image.Dispose();
                throw;
            }
        });
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
