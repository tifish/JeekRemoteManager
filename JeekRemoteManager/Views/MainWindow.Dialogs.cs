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

/// <summary>Modal dialogs other than settings: confirmation, prompts, login-command help and the bastion template editor.</summary>
public partial class MainWindow
{
    private Task<bool> ConfirmAsync(string title, string message)
    {
        var tcs = new TaskCompletionSource<bool>();

        var yes = new Button
        {
            Name = "ConfirmYesButton",
            Content = Localizer.Get("DialogYes"),
            MinWidth = 80,
            IsDefault = true,
            Classes = { "accent" },
        };
        var no = new Button
        {
            Name = "ConfirmNoButton",
            Content = Localizer.Get("DialogNo"),
            MinWidth = 80,
            IsCancel = true,
        };

        var dialog = new Window
        {
            Title = title,
            Width = 380,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(20),
                Spacing = 16,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { yes, no },
                    },
                },
            },
        };

        yes.Click += (_, _) => { tcs.TrySetResult(true); dialog.Close(); };
        no.Click += (_, _) => { tcs.TrySetResult(false); dialog.Close(); };
        dialog.Closed += (_, _) => tcs.TrySetResult(false);

        dialog.ShowDialog(this);
        return tcs.Task;
    }

    private async void OnLoginCommandsHelpClick(object? sender, RoutedEventArgs e) =>
        await ShowLoginCommandsHelpAsync();

    private async void OnEditBastionTemplateClick(object? sender, RoutedEventArgs e) =>
        await ShowBastionTemplateEditorAsync();

    private void OnLoginCommandsLostFocus(object? sender, RoutedEventArgs e)
    {
        if ((DataContext as MainWindowViewModel)?.Editor is not { } editor)
            return;

        editor.LoginCommands =
            LoginCommandSequence.TrimSurroundingBlankLines(editor.LoginCommands);
    }

    /// <summary>Edits the four fixed fragments shared by the automatically linked bastion.</summary>
    public async Task ShowBastionTemplateEditorAsync()
    {
        if ((DataContext as MainWindowViewModel)?.Editor is not { HasBastionProfile: true } editor)
            return;

        await CreateBastionTemplateEditorDialog(editor, flushAutoSave: true)
            .ShowDialog(this);
    }

    /// <summary>Builds the real template dialog for the UI and Debug MCP probes.</summary>
    internal Window CreateBastionTemplateEditorDialog(
        ConnectionEditorViewModel editor,
        bool flushAutoSave = false,
        Func<string, string, Task<bool>>? confirmAsync = null)
    {
        static TextBox FragmentEditor(string name, string text) =>
            new LoginCommandsTextBox
            {
                Name = name,
                Text = text,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                MinHeight = 82,
                FontFamily = new FontFamily("Consolas"),
            };

        var fragments = new[]
        {
            FragmentEditor("BastionTemplateSegment1", editor.BastionTemplateSegment1),
            FragmentEditor("BastionTemplateSegment2", editor.BastionTemplateSegment2),
            FragmentEditor("BastionTemplateSegment3", editor.BastionTemplateSegment3),
            FragmentEditor("BastionTemplateSegment4", editor.BastionTemplateSegment4),
        };
        var insertConnectionLoginCommands = false;

        static StackPanel Section(string title, string hint, Control editorControl) =>
            new()
            {
                Spacing = 5,
                Children =
                {
                    new TextBlock { Text = title, FontWeight = FontWeight.SemiBold },
                    new TextBlock
                    {
                        Text = hint,
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 11,
                        Classes = { "hint" },
                    },
                    editorControl,
                },
            };

        var save = new Button
        {
            Name = "SaveBastionTemplateButton",
            Content = Localizer.Get("BastionTemplateSave"),
            MinWidth = 112,
            IsDefault = true,
            Classes = { "accent" },
        };
        var insertTypical = new Button
        {
            Name = "InsertTypicalBastionTemplateButton",
            Content = Localizer.Get("BastionTemplateInsertTypical"),
            MinWidth = 144,
        };
        var typicalHint = new TextBlock
        {
            Name = "TypicalBastionTemplateHint",
            Text = Localizer.Get("BastionTemplateTypicalHint"),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 11,
            Classes = { "hint" },
        };
        var typicalRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            ColumnSpacing = 12,
        };
        typicalRow.Children.Add(insertTypical);
        Grid.SetColumn(typicalHint, 1);
        typicalRow.Children.Add(typicalHint);
        var cancel = new Button
        {
            Content = Localizer.Get("DialogCancel"),
            MinWidth = 88,
            IsCancel = true,
        };
        var dialog = new Window
        {
            Title = Localizer.Get("BastionTemplateEditTitle"),
            Width = 720,
            Height = 760,
            MinWidth = 560,
            MinHeight = 560,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var content = new Grid
        {
            RowDefinitions = new RowDefinitions("*,Auto"),
            Margin = new Thickness(22),
            RowSpacing = 14,
        };
        content.Children.Add(new ScrollViewer
        {
            Content = new StackPanel
            {
                Spacing = 16,
                Children =
                {
                    new TextBlock
                    {
                        Text = string.Format(
                            Localizer.Get("BastionTemplateAppliesTo"),
                            editor.BastionProfileEndpoint),
                        TextWrapping = TextWrapping.Wrap,
                    },
                    typicalRow,
                    Section("1", Localizer.Get("BastionTemplateFragmentHint"), fragments[0]),
                    Section("2", Localizer.Get("BastionTemplateFragmentHint"), fragments[1]),
                    Section("3", Localizer.Get("BastionTemplateFragmentHint"), fragments[2]),
                    Section("4", Localizer.Get("BastionTemplateFragmentHint"), fragments[3]),
                },
            },
        });
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { save, cancel },
        };
        Grid.SetRow(actions, 1);
        content.Children.Add(actions);
        dialog.Content = content;

        var requestConfirmation = confirmAsync ?? ConfirmAsync;
        insertTypical.Click += async (_, _) =>
        {
            if (fragments.Any(fragment => !string.IsNullOrWhiteSpace(fragment.Text))
                && !await requestConfirmation(
                    Localizer.Get("BastionTemplateOverwriteTitle"),
                    Localizer.Get("BastionTemplateOverwritePrompt")))
            {
                return;
            }

            for (var id = 1; id <= BastionLoginProfile.SegmentCount; id++)
            {
                fragments[id - 1].Text = BastionLoginTemplatePreset
                    .GetSegment(id)
                    .ReplaceLineEndings(Environment.NewLine);
            }

            insertConnectionLoginCommands =
                string.IsNullOrWhiteSpace(editor.LoginCommands);
        };
        save.Click += (_, _) =>
        {
            var values = fragments
                .Select(fragment => LoginCommandSequence.TrimSurroundingBlankLines(
                    fragment.Text ?? ""))
                .ToArray();
            var invalid = Array.FindIndex(
                values,
                LoginCommandSequence.ContainsTemplateDirective);
            if (invalid >= 0)
            {
                if (DataContext is MainWindowViewModel vm)
                {
                    vm.StatusMessage = string.Format(
                        Localizer.Get("BastionTemplateInvalid"),
                        $"#{invalid + 1}: #template");
                }
                fragments[invalid].Focus();
                return;
            }

            for (var index = 0; index < values.Length; index++)
            {
                var errors = editor.ValidateBastionTemplateFragment(values[index]);
                if (errors.Count == 0)
                    continue;

                if (DataContext is MainWindowViewModel vm)
                {
                    vm.StatusMessage = string.Format(
                        Localizer.Get("BastionTemplateInvalid"),
                        $"#{index + 1}: {errors[0]}");
                }
                fragments[index].Focus();
                return;
            }

            editor.BastionTemplateSegment1 = values[0];
            editor.BastionTemplateSegment2 = values[1];
            editor.BastionTemplateSegment3 = values[2];
            editor.BastionTemplateSegment4 = values[3];
            if (insertConnectionLoginCommands)
            {
                editor.LoginCommands =
                    BastionLoginTemplatePreset.UseConnectionCommandsWhenEmpty(
                        editor.LoginCommands);
            }
            if (flushAutoSave && DataContext is MainWindowViewModel mainVm)
                mainVm.FlushAutoSave();
            dialog.Close();
        };
        cancel.Click += (_, _) => dialog.Close();
        return dialog;
    }

    /// <summary>Shows the localized login-command guide used by the SSH editor.</summary>
    public async Task ShowLoginCommandsHelpAsync()
    {
        var close = new Button
        {
            Content = Localizer.Get("Close"),
            HorizontalAlignment = HorizontalAlignment.Right,
            MinWidth = 88,
            Classes = { "accent" },
        };
        var rows = new StackPanel { Spacing = 7 };

        void AddDirective(string directive, string localizationKey)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("120,*"), ColumnSpacing = 12 };
            row.Children.Add(new SelectableTextBlock
            {
                Text = directive,
                FontFamily = new FontFamily("Consolas"),
                FontWeight = FontWeight.SemiBold,
            });
            var description = new TextBlock
            {
                Text = Localizer.Get(localizationKey),
                TextWrapping = TextWrapping.Wrap,
            };
            Grid.SetColumn(description, 1);
            row.Children.Add(description);
            rows.Children.Add(row);
        }

        AddDirective("command", "LoginCommandsHelpOrdinary");
        foreach (var completion in LoginCommandSequence.Completions)
            AddDirective(completion.DisplayText, completion.HelpLocalizationKey);
        AddDirective("{{name}}", "LoginCommandsHelpVariableName");
        AddDirective("{{host}}", "LoginCommandsHelpVariableHost");
        AddDirective("{{port}}", "LoginCommandsHelpVariablePort");
        AddDirective("{{username}}", "LoginCommandsHelpVariableUsername");

        var example = new TextBox
        {
            Text = "#template 1\r\n#reuse-enter\r\n#select {{name}}\r\n#duplicate\r\n#reuse-leave\r\n#template 4",
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Consolas"),
            MinHeight = 150,
        };

        var content = new Grid
        {
            RowDefinitions = new RowDefinitions("*,Auto"),
            Margin = new Thickness(22),
            RowSpacing = 14,
        };
        content.Children.Add(new ScrollViewer
        {
            Content = new StackPanel
            {
                Spacing = 14,
                Children =
                {
                    new TextBlock
                    {
                        Text = Localizer.Get("LoginCommandsHelpIntro"),
                        TextWrapping = TextWrapping.Wrap,
                    },
                    rows,
                    new TextBlock
                    {
                        Text = Localizer.Get("LoginCommandsHelpExampleTitle"),
                        FontWeight = FontWeight.SemiBold,
                    },
                    example,
                    new TextBlock
                    {
                        Text = Localizer.Get("LoginCommandsHelpExampleDescription"),
                        TextWrapping = TextWrapping.Wrap,
                    },
                },
            },
        });
        Grid.SetRow(close, 1);
        content.Children.Add(close);

        var dialog = new Window
        {
            Title = Localizer.Get("LoginCommandsHelpTitle"),
            Width = 720,
            Height = 650,
            MinWidth = 560,
            MinHeight = 480,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = content,
        };
        close.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(this);
    }

    private Task<string?> PromptAsync(string title, string message, string initial)
    {
        var tcs = new TaskCompletionSource<string?>();

        var input = new TextBox { Text = initial };
        var ok = new Button
        {
            Content = Localizer.Get("DialogOk"),
            MinWidth = 80,
            IsDefault = true,
            Classes = { "accent" },
        };
        var cancel = new Button { Content = Localizer.Get("DialogCancel"), MinWidth = 80, IsCancel = true };

        var dialog = new Window
        {
            Title = title,
            Width = 380,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(20),
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = message },
                    input,
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

        ok.Click += (_, _) => { tcs.TrySetResult(input.Text); dialog.Close(); };
        cancel.Click += (_, _) => { tcs.TrySetResult(null); dialog.Close(); };
        dialog.Closed += (_, _) => tcs.TrySetResult(null);
        // Focus + select once the window is actually shown, so it reliably sticks.
        dialog.Opened += (_, _) => { input.Focus(); input.SelectAll(); };

        dialog.ShowDialog(this);
        return tcs.Task;
    }
}
