using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Jeek.Avalonia.Localization;
using JeekRemoteManager.Models;
using JeekRemoteManager.Services;
using JeekRemoteManager.ViewModels;

namespace JeekRemoteManager.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => WireUp();
        Opened += (_, _) => WireUp();
        Closing += (_, _) =>
        {
            // Persist any pending edit before the app exits.
            (DataContext as MainWindowViewModel)?.FlushAutoSave();
        };
    }

    private void WireUp()
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        vm.Clipboard = Clipboard;
        vm.ConfirmAsync = ConfirmAsync;
        vm.PromptAsync = PromptAsync;
        vm.PickKeyFileAsync = PickKeyFileAsync;
        vm.PickSettingsAsync = PickSettingsAsync;
        vm.PickFolderAsync = PickFolderAsync;
        vm.RequestFocusTree = FocusSelectedTreeItem;
    }

    /// <summary>
    /// Focuses the TreeViewItem container of the current selection so subsequent
    /// keystrokes (Enter, F2, Delete, Ctrl+C/X/V) hit the new item. Falls back
    /// to focusing the TreeView itself if the container isn't realized yet.
    /// </summary>
    private void FocusSelectedTreeItem()
    {
        // Defer until after the TreeView has materialised the container.
        Dispatcher.UIThread.Post(() =>
        {
            var item = Tree.SelectedItem;
            if (item != null)
            {
                var container = FindTreeViewItem(Tree, item);
                if (container != null)
                {
                    container.Focus();
                    return;
                }
            }
            Tree.Focus();
        }, DispatcherPriority.Background);
    }

    private static TreeViewItem? FindTreeViewItem(ItemsControl items, object data)
    {
        foreach (var visual in items.GetVisualDescendants())
        {
            if (visual is TreeViewItem tvi && ReferenceEquals(tvi.DataContext, data))
                return tvi;
        }
        return null;
    }

    private void OnTreeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        if (vm.SelectedNode is { IsConnection: true } && vm.ConnectCommand.CanExecute(null))
            vm.ConnectCommand.Execute(null);
    }

    // - Right-clicking on a node selects it so the context menu acts on it.
    // - Clicking (left or right) on empty tree area clears the selection so
    //   new/paste operations target the root folder.
    private void OnTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        var hitItem = e.Source is Visual source
            ? source.FindAncestorOfType<TreeViewItem>(includeSelf: true)
            : null;

        if (hitItem is { DataContext: TreeNodeViewModel node })
        {
            // Only right-click reselects; left-click on a node is handled by the
            // TreeView normally and would otherwise fight its own selection.
            if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
                vm.SelectedNode = node;
        }
        else
        {
            // Empty area → clear selection so the next New/Paste targets root.
            vm.SelectedNode = null;
        }
    }

    private async Task<string?> PickFolderAsync(string suggestedPath)
    {
        var options = new FolderPickerOpenOptions
        {
            Title = Localizer.Get("DialogPickFinalShellTitle"),
            AllowMultiple = false,
        };

        if (!string.IsNullOrEmpty(suggestedPath) && System.IO.Directory.Exists(suggestedPath))
        {
            try
            {
                options.SuggestedStartLocation =
                    await StorageProvider.TryGetFolderFromPathAsync(suggestedPath);
            }
            catch
            {
                // Best-effort — proceed without a suggestion.
            }
        }

        var folders = await StorageProvider.OpenFolderPickerAsync(options);
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    private async Task<string?> PickKeyFileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Localizer.Get("DialogPickKeyTitle"),
            AllowMultiple = false,
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    private Task<bool> ConfirmAsync(string title, string message)
    {
        var tcs = new TaskCompletionSource<bool>();

        var yes = new Button { Content = Localizer.Get("DialogYes"), MinWidth = 80, IsDefault = true };
        var no = new Button { Content = Localizer.Get("DialogNo"), MinWidth = 80, IsCancel = true };

        var dialog = new Window
        {
            Title = title,
            Width = 380,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(16),
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

    private Task<string?> PromptAsync(string title, string message, string initial)
    {
        var tcs = new TaskCompletionSource<string?>();

        var input = new TextBox { Text = initial };
        var ok = new Button { Content = Localizer.Get("DialogOk"), MinWidth = 80, IsDefault = true };
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
                Margin = new Avalonia.Thickness(16),
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

    private Task<SettingsDialogResult?> PickSettingsAsync(
        StorageLocation current, string? currentLanguage, string? currentTheme)
    {
        var tcs = new TaskCompletionSource<SettingsDialogResult?>();

        var userRadio = new RadioButton
        {
            GroupName = "storage",
            IsChecked = current == StorageLocation.UserDirectory,
            Content = BuildOption(
                Localizer.Get("StorageUserOption"),
                SettingsService.ResolveConnectionsRoot(StorageLocation.UserDirectory)),
        };

        var programRadio = new RadioButton
        {
            GroupName = "storage",
            IsChecked = current == StorageLocation.ProgramDirectory,
            Content = BuildOption(
                Localizer.Get("StorageProgramOption"),
                SettingsService.ResolveConnectionsRoot(StorageLocation.ProgramDirectory)),
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
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = themes,
        };
        themeBox.SelectedIndex =
            System.Array.FindIndex(themes, c => c.Code == currentTheme) is var ti && ti >= 0 ? ti : 0;

        var ok = new Button { Content = Localizer.Get("DialogOk"), MinWidth = 80, IsDefault = true };
        var cancel = new Button { Content = Localizer.Get("DialogCancel"), MinWidth = 80, IsCancel = true };

        var dialog = new Window
        {
            Title = Localizer.Get("DialogSettingsTitle"),
            Width = 460,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(16),
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = Localizer.Get("Language"), FontWeight = FontWeight.SemiBold },
                    languageBox,
                    new TextBlock { Text = Localizer.Get("Theme"), FontWeight = FontWeight.SemiBold },
                    themeBox,
                    new TextBlock { Text = Localizer.Get("DialogStorageQuestion"), FontWeight = FontWeight.SemiBold },
                    userRadio,
                    programRadio,
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
            var storage = userRadio.IsChecked == true
                ? StorageLocation.UserDirectory
                : StorageLocation.ProgramDirectory;
            var language = (languageBox.SelectedItem as LanguageChoice)?.Code;
            var theme = (themeBox.SelectedItem as ThemeChoice)?.Code;
            tcs.TrySetResult(new SettingsDialogResult(storage, language, theme));
            dialog.Close();
        };
        cancel.Click += (_, _) => { tcs.TrySetResult(null); dialog.Close(); };
        dialog.Closed += (_, _) => tcs.TrySetResult(null);

        dialog.ShowDialog(this);
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

    private static Control BuildOption(string title, string path) => new StackPanel
    {
        Spacing = 2,
        Children =
        {
            new TextBlock { Text = title },
            new TextBlock
            {
                Text = path,
                FontSize = 11,
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap,
            },
        },
    };
}
