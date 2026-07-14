using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Jeek.Avalonia.Localization;
using JeekRemoteManager.Services;

namespace JeekRemoteManager.Views;

/// <summary>Picker for provider-native AI sessions saved for the current terminal connection.</summary>
public static class AiConversationHistoryDialog
{
    public static Task<string?> ShowAsync(Window owner, IReadOnlyList<AiConversationSummary> conversations)
    {
        var tcs = new TaskCompletionSource<string?>();
        var rows = conversations.Select(FormatRow).ToArray();
        var list = new ListBox
        {
            ItemsSource = rows,
            MinWidth = 540,
            MinHeight = 260,
            MaxHeight = 520,
        };
        var restore = new Button
        {
            Content = Localizer.Get("AiRestoreConversation"),
            MinWidth = 90,
            IsDefault = true,
            IsEnabled = false,
        };
        var cancel = new Button
        {
            Content = Localizer.Get("DialogCancel"),
            MinWidth = 80,
            IsCancel = true,
        };

        void UpdateSelection() => restore.IsEnabled = list.SelectedIndex >= 0
            && list.SelectedIndex < conversations.Count
            && conversations[list.SelectedIndex].CanRestore;

        var dialog = new Window
        {
            Title = Localizer.Get("AiConversationHistoryTitle"),
            Width = 620,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = true,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(16),
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = Localizer.Get("AiConversationHistoryHint") },
                    list,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { restore, cancel },
                    },
                },
            },
        };

        void CompleteRestore()
        {
            var index = list.SelectedIndex;
            if (index < 0 || index >= conversations.Count || !conversations[index].CanRestore)
                return;
            tcs.TrySetResult(conversations[index].Id);
            dialog.Close();
        }

        list.SelectionChanged += (_, _) => UpdateSelection();
        list.DoubleTapped += (_, _) => CompleteRestore();
        restore.Click += (_, _) => CompleteRestore();
        cancel.Click += (_, _) =>
        {
            tcs.TrySetResult(null);
            dialog.Close();
        };
        dialog.Closed += (_, _) => tcs.TrySetResult(null);

        if (conversations.Count > 0)
            list.SelectedIndex = 0;
        UpdateSelection();
        dialog.ShowDialog(owner);
        return tcs.Task;
    }

    private static string FormatRow(AiConversationSummary conversation)
    {
        var model = string.IsNullOrWhiteSpace(conversation.Model) ? "" : $" · {conversation.Model}";
        var unavailable = conversation.CanRestore ? "" : $" · {Localizer.Get("AiConversationNotReady")}";
        return $"{conversation.Title}\n{conversation.Provider}{model} · "
               + $"{conversation.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm} · "
               + $"{string.Format(Localizer.Get("AiConversationMessages"), conversation.MessageCount)}{unavailable}";
    }
}
