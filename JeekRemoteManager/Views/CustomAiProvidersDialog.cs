using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Jeek.Avalonia.Localization;
using JeekRemoteManager.Models;
using JeekRemoteManager.Services;

namespace JeekRemoteManager.Views;

/// <summary>
/// Editor for the user-defined AI providers of the assistant panel: a list on the left,
/// the selected provider's fields (name, API type, base URL, key, models) on the right.
/// Works on clones and only hands the edited list back when OK is pressed.
/// </summary>
public static class CustomAiProvidersDialog
{
    /// <summary>Shows the dialog. Returns the edited provider list, or null on cancel.</summary>
    public static Task<List<CustomAiProvider>?> ShowAsync(Window owner, IReadOnlyList<CustomAiProvider> current)
    {
        var tcs = new TaskCompletionSource<List<CustomAiProvider>?>();

        // The clones hold CLEAR-TEXT keys while the dialog is open; encryption happens
        // once on OK. A blob that cannot be decrypted (settings copied from a machine
        // with a different master password) shows as an empty box but its ciphertext is
        // preserved on OK unless the user types a replacement.
        var providers = current.Select(p => p.Clone()).ToList();
        var undecryptableKeys = new Dictionary<CustomAiProvider, string>();
        foreach (var provider in providers)
        {
            if (string.IsNullOrEmpty(provider.ApiKey) || !MasterKeyService.IsPasswordBlob(provider.ApiKey))
                continue; // empty, or legacy plaintext — edit as-is.
            if (PasswordProtector.TryDecrypt(provider.ApiKey, out var clearKey))
            {
                provider.ApiKey = clearKey;
            }
            else
            {
                undecryptableKeys[provider] = provider.ApiKey;
                provider.ApiKey = null;
            }
        }

        var names = new ObservableCollection<string>(providers.Select(DisplayName));

        var list = new ListBox { ItemsSource = names, MinHeight = 220 };
        var addButton = new Button { Content = Localizer.Get("AiAddProvider"), HorizontalAlignment = HorizontalAlignment.Stretch };
        var removeButton = new Button { Content = Localizer.Get("AiRemoveProvider"), HorizontalAlignment = HorizontalAlignment.Stretch };

        var nameBox = new TextBox();
        var typeBox = new ComboBox
        {
            ItemsSource = new[] { "OpenAI", "Anthropic" },
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var urlBox = new TextBox();
        var keyBox = new TextBox { PasswordChar = '●' };
        var revealKey = new CheckBox { Content = Localizer.Get("AiShowApiKey") };
        revealKey.IsCheckedChanged += (_, _) => keyBox.RevealPassword = revealKey.IsChecked == true;
        var modelsBox = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = Avalonia.Media.TextWrapping.NoWrap,
            MinHeight = 76,
            MaxHeight = 140,
        };

        var editPanel = new StackPanel
        {
            Spacing = 6,
            IsEnabled = false,
            Children =
            {
                new TextBlock { Text = Localizer.Get("AiProviderName") },
                nameBox,
                new TextBlock { Text = Localizer.Get("AiApiType") },
                typeBox,
                new TextBlock { Text = Localizer.Get("AiBaseUrl") },
                urlBox,
                new TextBlock { Text = Localizer.Get("AiApiKey") },
                keyBox,
                revealKey,
                new TextBlock { Text = Localizer.Get("AiModelsLabel") },
                modelsBox,
            },
        };

        var editing = -1;
        var loading = false;

        void UpdateUrlWatermark() => urlBox.PlaceholderText = typeBox.SelectedIndex == 1
            ? AnthropicChatSession.DefaultBaseUrl
            : OpenAiChatSession.DefaultBaseUrl;

        void Apply()
        {
            if (loading || editing < 0 || editing >= providers.Count)
                return;

            var provider = providers[editing];
            provider.Name = nameBox.Text?.Trim() ?? "";
            provider.ApiType = typeBox.SelectedIndex == 1 ? CustomAiApiType.Anthropic : CustomAiApiType.OpenAI;
            provider.BaseUrl = string.IsNullOrWhiteSpace(urlBox.Text) ? null : urlBox.Text.Trim();
            provider.ApiKey = string.IsNullOrWhiteSpace(keyBox.Text) ? null : keyBox.Text.Trim();
            provider.Models = (modelsBox.Text ?? "")
                .Split('\n')
                .Select(m => m.Trim())
                .Where(m => m.Length > 0)
                .ToList();

            var display = DisplayName(provider);
            if (names[editing] != display)
            {
                // Replacing the selected item drops the ListBox selection; restore it
                // without re-entering Load.
                loading = true;
                names[editing] = display;
                list.SelectedIndex = editing;
                loading = false;
            }
        }

        void Load()
        {
            editing = list.SelectedIndex;
            loading = true;
            try
            {
                var hasSelection = editing >= 0 && editing < providers.Count;
                editPanel.IsEnabled = hasSelection;
                removeButton.IsEnabled = hasSelection;
                if (!hasSelection)
                {
                    nameBox.Text = "";
                    typeBox.SelectedIndex = 0;
                    urlBox.Text = "";
                    keyBox.Text = "";
                    modelsBox.Text = "";
                }
                else
                {
                    var provider = providers[editing];
                    nameBox.Text = provider.Name;
                    typeBox.SelectedIndex = provider.ApiType == CustomAiApiType.Anthropic ? 1 : 0;
                    urlBox.Text = provider.BaseUrl ?? "";
                    keyBox.Text = provider.ApiKey ?? "";
                    modelsBox.Text = string.Join("\n", provider.Models);
                }
                UpdateUrlWatermark();
            }
            finally
            {
                loading = false;
            }
        }

        list.SelectionChanged += (_, _) =>
        {
            if (!loading)
                Load();
        };
        nameBox.TextChanged += (_, _) => Apply();
        urlBox.TextChanged += (_, _) => Apply();
        keyBox.TextChanged += (_, _) => Apply();
        modelsBox.TextChanged += (_, _) => Apply();
        typeBox.SelectionChanged += (_, _) =>
        {
            if (loading)
                return;
            Apply();
            UpdateUrlWatermark();
        };

        addButton.Click += (_, _) =>
        {
            var provider = new CustomAiProvider();
            providers.Add(provider);
            names.Add(DisplayName(provider));
            list.SelectedIndex = providers.Count - 1;
            nameBox.Focus();
        };
        removeButton.Click += (_, _) =>
        {
            if (editing < 0 || editing >= providers.Count)
                return;
            var index = editing;
            editing = -1;
            providers.RemoveAt(index);
            names.RemoveAt(index);
            list.SelectedIndex = Math.Min(index, providers.Count - 1);
            Load();
        };

        var ok = new Button { Content = Localizer.Get("DialogOk"), MinWidth = 80, IsDefault = true };
        var cancel = new Button { Content = Localizer.Get("DialogCancel"), MinWidth = 80, IsCancel = true };

        var leftPanel = new DockPanel
        {
            Children =
            {
                new StackPanel
                {
                    [DockPanel.DockProperty] = Dock.Bottom,
                    Orientation = Orientation.Vertical,
                    Spacing = 6,
                    Margin = new Avalonia.Thickness(0, 8, 0, 0),
                    Children = { addButton, removeButton },
                },
                list,
            },
        };

        var body = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("200,16,*"),
            Children = { leftPanel, editPanel },
        };
        Grid.SetColumn(leftPanel, 0);
        Grid.SetColumn(editPanel, 2);

        var dialog = new Window
        {
            Title = Localizer.Get("AiCustomProvidersTitle"),
            Width = 620,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(16),
                Spacing = 12,
                Children =
                {
                    body,
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
            Apply();
            var result = providers.Where(p => p.Name.Length > 0).ToList();
            foreach (var provider in result)
            {
                provider.ApiKey = string.IsNullOrEmpty(provider.ApiKey)
                    ? undecryptableKeys.TryGetValue(provider, out var kept) ? kept : null
                    : PasswordProtector.Encrypt(provider.ApiKey);
            }
            tcs.TrySetResult(result);
            dialog.Close();
        };
        cancel.Click += (_, _) => { tcs.TrySetResult(null); dialog.Close(); };
        dialog.Closed += (_, _) => tcs.TrySetResult(null);

        if (providers.Count > 0)
            list.SelectedIndex = 0;
        else
            Load();

        dialog.ShowDialog(owner);
        return tcs.Task;
    }

    private static string DisplayName(CustomAiProvider provider) =>
        string.IsNullOrWhiteSpace(provider.Name) ? Localizer.Get("AiProviderUnnamed") : provider.Name;
}
