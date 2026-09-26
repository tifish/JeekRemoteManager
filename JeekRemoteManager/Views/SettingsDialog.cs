using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Jeek.Avalonia.Localization;
using JeekRemoteManager.Controls;
using JeekRemoteManager.Models;
using JeekRemoteManager.Services;
using JeekRemoteManager.ViewModels;
using JeekTools;

namespace JeekRemoteManager.Views;

/// <summary>
/// The Settings dialog: appearance, terminal, files and storage, security, updates. Built in
/// code and independent of the main window — it gets the few window services it needs
/// (folder picker, master-password change) as delegates, and reports the user's choices as a
/// <see cref="SettingsDialogResult"/> for the view model to apply.
/// </summary>
internal static class SettingsDialog
{
    /// <param name="pickFolder">(suggested path, title) =&gt; chosen folder or null.</param>
    /// <param name="changeMasterPassword">Re-encrypts everything under a new master password.</param>
    public static Task<SettingsDialogResult?> ShowAsync(
        Window owner,
        Func<string, string?, Task<string?>> pickFolder,
        Action<string> changeMasterPassword,
        StorageLocation current,
        string? currentCustomPath,
        string? currentLanguage,
        string? currentTheme,
        bool currentCheckOnStartup,
        int currentIntervalHours,
        string? currentEditorPath,
        TerminalAppearanceSettings currentTerminal)
    {
        var tcs = new TaskCompletionSource<SettingsDialogResult?>();

        var userRadio = new RadioButton
        {
            GroupName = "storage",
            IsChecked = current == StorageLocation.UserDirectory,
            Content = BuildOption(
                Localizer.Get("StorageUserOption"),
                SettingsService.ResolveConfigRoot(StorageLocation.UserDirectory)),
        };

        var programRadio = new RadioButton
        {
            GroupName = "storage",
            IsChecked = current == StorageLocation.ProgramDirectory,
            Content = BuildOption(
                Localizer.Get("StorageProgramOption"),
                SettingsService.ResolveConfigRoot(StorageLocation.ProgramDirectory)),
        };

        // The custom location lets the user point storage at any base directory.
        // Browsing fills in customPath and selects this radio.
        var customPath = currentCustomPath;
        var customPathText = new TextBlock
        {
            Name = "SettingsCustomPathText",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Classes = { "hint" },
        };
        var browseButton = new Button
        {
            Content = Localizer.Get("Browse"),
            MinWidth = 88,
        };

        void RefreshCustomPathText()
        {
            customPathText[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("TextMutedBrush");
            customPathText.Text = string.IsNullOrWhiteSpace(customPath)
                ? Localizer.Get("StorageCustomNotSet")
                : SettingsService.ResolveConfigRoot(StorageLocation.CustomDirectory, customPath);
        }

        RefreshCustomPathText();

        var customRadio = new RadioButton
        {
            GroupName = "storage",
            IsChecked = current == StorageLocation.CustomDirectory,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = new Grid
            {
                RowDefinitions = new RowDefinitions("Auto,Auto"),
                RowSpacing = 7,
            },
        };
        var customContent = (Grid)customRadio.Content;
        var customTitle = new TextBlock { Text = Localizer.Get("StorageCustomOption") };
        var customPathRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            ColumnSpacing = 10,
        };
        Grid.SetRow(customPathRow, 1);
        Grid.SetColumn(customPathText, 1);
        customPathRow.Children.Add(browseButton);
        customPathRow.Children.Add(customPathText);
        customContent.Children.Add(customTitle);
        customContent.Children.Add(customPathRow);

        browseButton.Click += async (_, _) =>
        {
            var picked = await pickFolder(customPath ?? string.Empty, Localizer.Get("DialogPickStorageTitle"));
            if (picked is null)
                return;
            customPath = picked;
            customRadio.IsChecked = true;
            RefreshCustomPathText();
        };

        // null code = follow system. Native names match the in-app language list.
        var languages = new[]
        {
            new LanguageChoice(Localizer.Get("FollowSystem"), null),
            new LanguageChoice("English", "en"),
            new LanguageChoice("中文", "zh"),
        };
        var languageBox = new ComboBox
        {
            Name = "SettingsLanguageBox",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = languages,
        };
        languageBox.SelectedIndex =
            System.Array.FindIndex(languages, c => c.Code == currentLanguage) is var i && i >= 0 ? i : 0;

        // null code = follow system theme.
        var themes = new[]
        {
            new ThemeChoice(Localizer.Get("FollowSystem"), null),
            new ThemeChoice(Localizer.Get("ThemeLight"), "Light"),
            new ThemeChoice(Localizer.Get("ThemeDark"), "Dark"),
        };
        var themeBox = new ComboBox
        {
            Name = "SettingsThemeBox",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = themes,
        };
        themeBox.SelectedIndex =
            System.Array.FindIndex(themes, c => c.Code == currentTheme) is var ti && ti >= 0 ? ti : 0;

        var changePassword = new Button
        {
            Name = "SettingsChangePasswordButton",
            Content = Localizer.Get("ChangeMasterPassword"),
            MinWidth = 150,
        };

        var checkOnStartupBox = new CheckBox
        {
            Name = "SettingsCheckOnStartupBox",
            Content = Localizer.Get("CheckUpdateOnStartup"),
            IsChecked = currentCheckOnStartup,
        };

        // 0 = disabled. Hours used directly as the value.
        var intervals = new[]
        {
            new IntervalChoice(Localizer.Get("IntervalNever"), 0),
            new IntervalChoice(Localizer.Get("IntervalEvery6Hours"), 6),
            new IntervalChoice(Localizer.Get("IntervalDaily"), 24),
            new IntervalChoice(Localizer.Get("IntervalWeekly"), 24 * 7),
        };
        var intervalBox = new ComboBox
        {
            Name = "SettingsIntervalBox",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = intervals,
        };
        intervalBox.SelectedIndex =
            System.Array.FindIndex(intervals, c => c.Hours == currentIntervalHours) is var ii && ii >= 0
                ? ii
                : 2;

        // Editor for the file browser's remote editing (F4); blank = shell association.
        var editorBox = new TextBox
        {
            Name = "SettingsEditorBox",
            Text = currentEditorPath ?? "",
            PlaceholderText = Localizer.Get("SettingsEditorWatermark"),
        };
        var editorBrowse = new Button
        {
            Content = Localizer.Get("Browse"),
            MinWidth = 88,
        };
        editorBrowse.Click += async (_, _) =>
        {
            var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Localizer.Get("DialogPickEditorTitle"),
                AllowMultiple = false,
            });
            if (files.Count > 0 && files[0].TryGetLocalPath() is { } picked)
                editorBox.Text = picked;
        };
        var editorRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 8,
        };
        Grid.SetColumn(editorBox, 0);
        Grid.SetColumn(editorBrowse, 1);
        editorRow.Children.Add(editorBox);
        editorRow.Children.Add(editorBrowse);

        var ok = new Button
        {
            Name = "SettingsOkButton",
            Content = Localizer.Get("DialogOk"),
            MinWidth = 96,
            IsDefault = true,
            Classes = { "accent" },
        };
        var cancel = new Button
        {
            Name = "SettingsCancelButton",
            Content = Localizer.Get("DialogCancel"),
            MinWidth = 96,
            IsCancel = true,
        };

        var appearanceFields = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            ColumnSpacing = 14,
        };
        var languageField = BuildSettingsField(Localizer.Get("Language"), languageBox);
        var themeField = BuildSettingsField(Localizer.Get("Theme"), themeBox);
        Grid.SetColumn(themeField, 1);
        appearanceFields.Children.Add(languageField);
        appearanceFields.Children.Add(themeField);

        var appearanceCard = BuildSettingsCard(
            "SettingsAppearanceCard",
            Localizer.Get("SettingsAppearanceSection"),
            appearanceFields);

        // Terminal font: "default" first, then every installed family (the saved one is kept
        // even if it is no longer installed, so opening the dialog never changes it).
        var fontChoices = new List<FontChoice> { new(Localizer.Get("TerminalFontDefault"), null) };
        fontChoices.AddRange(FontManager.Current.SystemFonts
            .Select(family => family.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(name => new FontChoice(name, name)));
        if (currentTerminal.FontFamily is { } savedFont
            && !fontChoices.Any(choice => string.Equals(choice.Family, savedFont, StringComparison.OrdinalIgnoreCase)))
        {
            fontChoices.Insert(1, new FontChoice(savedFont, savedFont));
        }
        var terminalFontBox = new ComboBox
        {
            Name = "SettingsTerminalFontBox",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MaxDropDownHeight = 360,
            ItemsSource = fontChoices,
            SelectedIndex = Math.Max(0, fontChoices.FindIndex(choice =>
                string.Equals(choice.Family, currentTerminal.FontFamily, StringComparison.OrdinalIgnoreCase))),
        };
        var schemeNames = TerminalAppearance.Schemes.Select(scheme => scheme.Name).ToArray();
        var terminalSchemeBox = new ComboBox
        {
            Name = "SettingsTerminalSchemeBox",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = schemeNames,
            SelectedItem = TerminalAppearance.NormalizeSchemeName(currentTerminal.ColorScheme),
        };
        var scrollbackChoices = TerminalAppearance.ScrollbackChoices
            .Append(currentTerminal.ScrollbackLines)
            .Distinct()
            .Order()
            .ToArray();
        var terminalScrollbackBox = new ComboBox
        {
            Name = "SettingsTerminalScrollbackBox",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = scrollbackChoices,
            SelectedItem = currentTerminal.ScrollbackLines,
        };
        var terminalFields = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto"),
            ColumnSpacing = 14,
            RowSpacing = 14,
        };
        var terminalFontField = BuildSettingsField(Localizer.Get("TerminalFontLabel"), terminalFontBox);
        var terminalSchemeField = BuildSettingsField(Localizer.Get("TerminalColorSchemeLabel"), terminalSchemeBox);
        var terminalScrollbackField = BuildSettingsField(Localizer.Get("TerminalScrollbackLabel"), terminalScrollbackBox);
        var terminalScrollbackHint = new TextBlock
        {
            Text = Localizer.Get("TerminalScrollbackHint"),
            Classes = { "hint" },
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Avalonia.Thickness(0, 0, 0, 6),
        };
        Grid.SetColumn(terminalSchemeField, 1);
        Grid.SetRow(terminalScrollbackField, 1);
        Grid.SetRow(terminalScrollbackHint, 1);
        Grid.SetColumn(terminalScrollbackHint, 1);
        terminalFields.Children.Add(terminalFontField);
        terminalFields.Children.Add(terminalSchemeField);
        terminalFields.Children.Add(terminalScrollbackField);
        terminalFields.Children.Add(terminalScrollbackHint);
        var terminalCard = BuildSettingsCard(
            "SettingsTerminalCard",
            Localizer.Get("SettingsTerminalSection"),
            terminalFields);

        var storageOptions = new StackPanel
        {
            Spacing = 8,
            Children = { userRadio, programRadio, customRadio },
        };
        var filesCardContent = new StackPanel
        {
            Spacing = 16,
            Children =
            {
                BuildSettingsField(Localizer.Get("SettingsEditorLabel"), editorRow),
                BuildSettingsField(Localizer.Get("SettingsStorageLabel"), storageOptions),
            },
        };
        var filesCard = BuildSettingsCard(
            "SettingsFilesCard",
            Localizer.Get("SettingsFilesSection"),
            filesCardContent);

        var securityRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 18,
            Children =
            {
                new TextBlock
                {
                    Text = Localizer.Get("SettingsPasswordHint"),
                    Classes = { "hint" },
                    VerticalAlignment = VerticalAlignment.Center,
                },
            },
        };
        Grid.SetColumn(changePassword, 1);
        securityRow.Children.Add(changePassword);
        var securityCard = BuildSettingsCard(
            "SettingsSecurityCard",
            Localizer.Get("SettingsSecuritySection"),
            securityRow);

        var updatesGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,220"),
            ColumnSpacing = 18,
        };
        checkOnStartupBox.VerticalAlignment = VerticalAlignment.Center;
        var intervalField = BuildSettingsField(Localizer.Get("UpdateCheckInterval"), intervalBox);
        Grid.SetColumn(intervalField, 1);
        updatesGrid.Children.Add(checkOnStartupBox);
        updatesGrid.Children.Add(intervalField);
        var updatesCard = BuildSettingsCard(
            "SettingsUpdatesCard",
            Localizer.Get("SettingsUpdatesSection"),
            updatesGrid);

        var header = new Border
        {
            Name = "SettingsDialogHeader",
            Padding = new Avalonia.Thickness(24, 20, 24, 16),
            Child = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new TextBlock
                    {
                        Text = Localizer.Get("DialogSettingsTitle"),
                        Classes = { "page-title" },
                    },
                    new TextBlock
                    {
                        Text = Localizer.Get("SettingsSubtitle"),
                        Classes = { "hint" },
                    },
                },
            },
        };

        var scroller = new ScrollViewer
        {
            Name = "SettingsDialogScrollViewer",
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(24, 4, 24, 20),
                Spacing = 12,
                Children = { appearanceCard, terminalCard, filesCard, securityCard, updatesCard },
            },
        };

        var footer = new Border
        {
            Name = "SettingsDialogFooter",
            BorderThickness = new Avalonia.Thickness(0, 1, 0, 0),
            Padding = new Avalonia.Thickness(24, 14),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 10,
                Children = { ok, cancel },
            },
        };
        footer[!Border.BackgroundProperty] = new DynamicResourceExtension("ChromeSurfaceBrush");
        footer[!Border.BorderBrushProperty] = new DynamicResourceExtension("BorderBrushSoft");

        var dialogLayout = new Grid
        {
            Name = "SettingsDialogLayout",
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            Children = { header },
        };
        Grid.SetRow(scroller, 1);
        Grid.SetRow(footer, 2);
        dialogLayout.Children.Add(scroller);
        dialogLayout.Children.Add(footer);

        var dialog = new Window
        {
            Name = "SettingsDialog",
            Title = Localizer.Get("DialogSettingsTitle"),
            Width = 680,
            Height = 720,
            MinWidth = 560,
            MinHeight = 560,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = true,
            Content = dialogLayout,
        };

        changePassword.Click += async (_, _) =>
        {
            await MasterPasswordDialog.ShowAsync(
                dialog,
                Localizer.Get("MasterChangeTitle"),
                Localizer.Get("MasterChangePrompt"),
                newPassword =>
                {
                    changeMasterPassword(newPassword);
                    return true;
                });
        };

        ok.Click += (_, _) =>
        {
            var storage = programRadio.IsChecked == true
                ? StorageLocation.ProgramDirectory
                : customRadio.IsChecked == true
                    ? StorageLocation.CustomDirectory
                    : StorageLocation.UserDirectory;

            // The custom option is meaningless without a directory — keep the dialog
            // open and flag the missing path rather than committing a bad setting.
            if (storage == StorageLocation.CustomDirectory && string.IsNullOrWhiteSpace(customPath))
            {
                customPathText[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("DangerBrush");
                customPathText.Text = Localizer.Get("StorageCustomRequired");
                return;
            }

            var language = (languageBox.SelectedItem as LanguageChoice)?.Code;
            var theme = (themeBox.SelectedItem as ThemeChoice)?.Code;
            var checkOnStartup = checkOnStartupBox.IsChecked == true;
            var intervalHours = (intervalBox.SelectedItem as IntervalChoice)?.Hours ?? 0;
            tcs.TrySetResult(new SettingsDialogResult(
                storage,
                storage == StorageLocation.CustomDirectory ? customPath : currentCustomPath,
                language,
                theme,
                checkOnStartup,
                intervalHours,
                editorBox.Text?.Trim(),
                new TerminalAppearanceSettings(
                    (terminalFontBox.SelectedItem as FontChoice)?.Family,
                    terminalSchemeBox.SelectedItem as string ?? TerminalAppearance.DefaultSchemeName,
                    terminalScrollbackBox.SelectedItem is int lines ? lines : currentTerminal.ScrollbackLines)));
            dialog.Close();
        };
        cancel.Click += (_, _) => { tcs.TrySetResult(null); dialog.Close(); };
        dialog.Closed += (_, _) => tcs.TrySetResult(null);

        dialog.ShowDialog(owner);
        return tcs.Task;
    }

    /// <summary>A selectable UI language; <see cref="Code"/> is null for "follow system".</summary>
    private sealed record LanguageChoice(string Label, string? Code)
    {
        public override string ToString() => Label;
    }

    /// <summary>A selectable UI theme; <see cref="Code"/> is null for "follow system".</summary>
    private sealed record ThemeChoice(string Label, string? Code)
    {
        public override string ToString() => Label;
    }

    /// <summary>A terminal font; <see cref="Family"/> is null for the default mono font.</summary>
    private sealed record FontChoice(string Label, string? Family)
    {
        public override string ToString() => Label;
    }

    /// <summary>An auto-update polling interval; <see cref="Hours"/> is 0 for "never".</summary>
    private sealed record IntervalChoice(string Label, int Hours)
    {
        public override string ToString() => Label;
    }

    private static StackPanel BuildSettingsField(string label, Control control) => new()
    {
        Spacing = 6,
        Children =
        {
            new TextBlock { Text = label, Classes = { "label" } },
            control,
        },
    };

    private static Border BuildSettingsCard(string name, string title, Control content) => new()
    {
        Name = name,
        Classes = { "form-card" },
        Padding = new Avalonia.Thickness(16),
        Child = new StackPanel
        {
            Spacing = 13,
            Children =
            {
                new TextBlock { Text = title, Classes = { "section-title" } },
                content,
            },
        },
    };

    private static Control BuildOption(string title, string path) => new StackPanel
    {
        Spacing = 2,
        Children =
        {
            new TextBlock { Text = title },
            new TextBlock
            {
                Text = path,
                TextWrapping = TextWrapping.Wrap,
                Classes = { "hint" },
            },
        },
    };
}
