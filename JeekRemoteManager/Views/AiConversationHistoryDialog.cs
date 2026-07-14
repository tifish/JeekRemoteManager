using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Jeek.Avalonia.Localization;
using JeekRemoteManager.Services;
using JeekRemoteManager.ViewModels;

namespace JeekRemoteManager.Views;

/// <summary>Picker and recycle-bin manager for provider-native AI sessions.</summary>
public static class AiConversationHistoryDialog
{
    private static readonly IBrush RowBackground = new SolidColorBrush(Color.FromArgb(14, 128, 128, 128));
    private static readonly IBrush RowBorder = new SolidColorBrush(Color.FromArgb(42, 128, 128, 128));
    private static readonly IBrush RowHoverBackground = new SolidColorBrush(Color.FromArgb(38, 72, 132, 210));
    private static readonly IBrush RowHoverBorder = new SolidColorBrush(Color.FromArgb(92, 72, 132, 210));

    public static Task ShowAsync(Window owner, AgentChatViewModel viewModel)
    {
        var tcs = new TaskCompletionSource<bool>();
        var historyRows = new StackPanel
        {
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var trashRows = new StackPanel
        {
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var historyList = new ScrollViewer
        {
            Name = "AiConversationHistoryList",
            Content = historyRows,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var trashList = new ScrollViewer
        {
            Name = "AiConversationTrashList",
            Content = trashRows,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var tabs = new TabControl
        {
            Name = "AiConversationHistoryTabs",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            ItemsSource = new[]
            {
                new TabItem
                {
                    Header = Localizer.Get("AiConversationHistoryTab"),
                    Content = historyList,
                },
                new TabItem
                {
                    Header = Localizer.Get("AiConversationTrashTab"),
                    Content = trashList,
                },
            },
            SelectedIndex = 0,
        };
        var close = new Button
        {
            Name = "AiConversationHistoryCloseButton",
            Content = Localizer.Get("Close"),
            MinWidth = 80,
            IsCancel = true,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var hint = new TextBlock
        {
            Text = Localizer.Get("AiConversationHistoryHint"),
            TextWrapping = TextWrapping.Wrap,
        };
        var retentionHint = new TextBlock
        {
            Text = Localizer.Get("AiTrashRetentionHint"),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.72,
        };
        var content = CreateDialogContent(hint, tabs, retentionHint, close);

        var dialog = new Window
        {
            Name = "AiConversationHistoryDialog",
            Title = Localizer.Get("AiConversationHistoryTitle"),
            Width = 720,
            Height = 520,
            MinWidth = 480,
            MinHeight = 360,
            SizeToContent = SizeToContent.Manual,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = true,
            Content = content,
        };

        void RestoreActiveConversation(AiConversationSummary conversation)
        {
            if (!conversation.CanRestore || !viewModel.RestoreConversation(conversation.Id))
                return;
            tcs.TrySetResult(true);
            dialog.Close();
        }

        void RefreshRows()
        {
            viewModel.RefreshConversationHistory();
            historyRows.Children.Clear();
            trashRows.Children.Clear();

            foreach (var conversation in viewModel.ConversationHistory)
            {
                historyRows.Children.Add(CreateConversationRow(
                    conversation,
                    deleted: false,
                    onRestore: () => RestoreActiveConversation(conversation),
                    onDelete: () =>
                    {
                        if (viewModel.MoveConversationToTrash(conversation.Id))
                            RefreshRows();
                    }));
            }

            foreach (var conversation in viewModel.TrashedConversationHistory)
            {
                trashRows.Children.Add(CreateConversationRow(
                    conversation,
                    deleted: true,
                    onRestore: () =>
                    {
                        if (viewModel.RestoreConversationFromTrash(conversation.Id))
                            RefreshRows();
                    },
                    onDelete: async () =>
                    {
                        var confirmed = await ConfirmPermanentDeleteAsync(dialog, conversation.Title);
                        if (confirmed && viewModel.DeleteConversationPermanently(conversation.Id))
                            RefreshRows();
                    }));
            }

            if (historyRows.Children.Count == 0)
                historyRows.Children.Add(CreateEmptyState("AiConversationHistoryEmpty"));
            if (trashRows.Children.Count == 0)
                trashRows.Children.Add(CreateEmptyState("AiConversationTrashEmpty"));
        }

        close.Click += (_, _) =>
        {
            tcs.TrySetResult(true);
            dialog.Close();
        };
        dialog.Closed += (_, _) => tcs.TrySetResult(true);

        RefreshRows();
        dialog.ShowDialog(owner);
        return tcs.Task;
    }

    private static Border CreateConversationRow(
        AiConversationSummary conversation,
        bool deleted,
        Action onRestore,
        Action onDelete)
    {
        var title = new TextBlock
        {
            Text = conversation.Title,
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var metadata = new TextBlock
        {
            Text = FormatMetadata(conversation),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Opacity = 0.72,
        };
        var text = new StackPanel
        {
            Name = "AiConversationRowText",
            Spacing = 3,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { title, metadata },
        };
        if (deleted)
        {
            text.Children.Add(new TextBlock
            {
                Text = FormatPermanentDeleteTime(conversation),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Opacity = 0.62,
            });
        }

        var restore = CreateIconButton(
            deleted ? "AiConversationRestoreFromTrashButton" : "AiConversationRestoreButton",
            "\uE72B",
            Localizer.Get(deleted ? "AiRestoreFromTrash" : "AiRestoreConversation"));
        restore.IsEnabled = deleted || conversation.CanRestore;
        restore.Click += (_, _) => onRestore();

        var delete = CreateIconButton(
            deleted ? "AiConversationDeletePermanentlyButton" : "AiConversationMoveToTrashButton",
            "\uE74D",
            Localizer.Get(deleted ? "AiDeletePermanently" : "AiMoveToTrash"));
        delete.Click += (_, _) => onDelete();

        var actions = new StackPanel
        {
            Name = "AiConversationRowActions",
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { restore, delete },
        };
        var layout = new Grid
        {
            Name = "AiConversationRowLayout",
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 12,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        Grid.SetColumn(text, 0);
        Grid.SetColumn(actions, 1);
        layout.Children.Add(text);
        layout.Children.Add(actions);

        var row = new Border
        {
            Name = deleted ? "AiConversationTrashRow" : "AiConversationHistoryRow",
            Tag = conversation.Id,
            Padding = new Thickness(10, 8),
            Margin = new Thickness(0, 0, 0, 2),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = RowBorder,
            Background = RowBackground,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = layout,
        };
        row.PointerEntered += (_, _) => SetRowHover(row, hovered: true);
        row.PointerExited += (_, _) => SetRowHover(row, hovered: false);
        row.DoubleTapped += (_, e) =>
        {
            if (TryRestoreFromDoubleTap(e.Source, onRestore))
                e.Handled = true;
        };
        return row;
    }

    private static void SetRowHover(Border row, bool hovered)
    {
        row.Background = hovered ? RowHoverBackground : RowBackground;
        row.BorderBrush = hovered ? RowHoverBorder : RowBorder;
    }

    private static bool TryRestoreFromDoubleTap(object? source, Action onRestore)
    {
        if (source is Visual visual
            && (visual is Button || visual.FindAncestorOfType<Button>() is not null))
        {
            return false;
        }

        onRestore();
        return true;
    }

    private static Button CreateIconButton(string name, string glyph, string tooltip)
    {
        var button = new Button
        {
            Name = name,
            Width = 30,
            Height = 30,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(7),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        ToolTip.SetTip(button, tooltip);
        return button;
    }

    private static TextBlock CreateEmptyState(string localizationKey) => new()
    {
        Text = Localizer.Get(localizationKey),
        Margin = new Thickness(12, 28),
        HorizontalAlignment = HorizontalAlignment.Center,
        TextAlignment = TextAlignment.Center,
        Opacity = 0.62,
    };

    private static Border CreateDialogContent(
        Control hint,
        Control tabs,
        Control retentionHint,
        Control close)
    {
        var layout = new Grid
        {
            Name = "AiConversationHistoryLayout",
            RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"),
            RowSpacing = 12,
        };
        Grid.SetRow(hint, 0);
        Grid.SetRow(tabs, 1);
        Grid.SetRow(retentionHint, 2);
        Grid.SetRow(close, 3);
        layout.Children.Add(hint);
        layout.Children.Add(tabs);
        layout.Children.Add(retentionHint);
        layout.Children.Add(close);
        return new Border
        {
            Padding = new Thickness(16),
            Child = layout,
        };
    }

    internal static string DebugMeasureConversationRow(double width, bool deleted)
    {
        var summary = new AiConversationSummary(
            "debug-row",
            "A long conversation title used to verify responsive history row layout",
            "Codex",
            "gpt-debug-model",
            "Debug server",
            DateTimeOffset.Now,
            12,
            true,
            deleted ? DateTimeOffset.Now : null);
        var row = CreateConversationRow(summary, deleted, () => { }, () => { });
        row.Measure(new Size(width, double.PositiveInfinity));
        row.Arrange(new Rect(0, 0, width, row.DesiredSize.Height));
        var layout = (Grid)row.Child!;
        var text = (StackPanel)layout.Children[0];
        var actions = (StackPanel)layout.Children[1];
        var actionsRight = actions.Bounds.X + actions.Bounds.Width;
        var rightAligned = Math.Abs(actionsRight - layout.Bounds.Width) < 0.5;
        return $"width={width:0}; text={text.Bounds.Width:0.0}; actions={actions.Bounds.Width:0.0}; rightAligned={rightAligned}";
    }

    internal static string DebugMeasureDialogLayout(double width, double height)
    {
        var tabs = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Child = CreateConversationRow(
                new AiConversationSummary(
                    "debug-dialog-row", "Debug dialog row", "Codex", null, "Debug server",
                    DateTimeOffset.Now, 2, true, null),
                deleted: false,
                () => { },
                () => { }),
        };
        var close = new Button
        {
            Width = 80,
            Height = 30,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var root = CreateDialogContent(
            new TextBlock { Text = "History" },
            tabs,
            new TextBlock { Text = "30-day retention" },
            close);
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        var layout = (Grid)root.Child!;
        var bottomPinned = Math.Abs(close.Bounds.Y + close.Bounds.Height - layout.Bounds.Height) < 0.5;
        return $"size={width:0}x{height:0}; tabs={tabs.Bounds.Width:0.0}x{tabs.Bounds.Height:0.0}; bottomPinned={bottomPinned}";
    }

    internal static string DebugCheckConversationRowInteraction()
    {
        var restoreCount = 0;
        var row = CreateConversationRow(
            new AiConversationSummary(
                "debug-interaction-row", "Debug interaction row", "Codex", null, "Debug server",
                DateTimeOffset.Now, 2, true, null),
            deleted: false,
            () => restoreCount++,
            () => { });
        var normalBackground = row.Background;
        SetRowHover(row, hovered: true);
        var hoverChanged = !ReferenceEquals(normalBackground, row.Background);
        SetRowHover(row, hovered: false);
        var normalRestored = ReferenceEquals(normalBackground, row.Background);
        var doubleTapRestore = TryRestoreFromDoubleTap(row, () => restoreCount++);
        var actions = (StackPanel)((Grid)row.Child!).Children[1];
        var buttonIgnored = !TryRestoreFromDoubleTap(actions.Children[0], () => restoreCount++);
        return $"hoverChanged={hoverChanged}; normalRestored={normalRestored}; "
               + $"doubleTapRestore={doubleTapRestore && restoreCount == 1}; buttonIgnored={buttonIgnored}";
    }

    private static Task<bool> ConfirmPermanentDeleteAsync(Window owner, string title)
    {
        var tcs = new TaskCompletionSource<bool>();
        var delete = new Button
        {
            Content = Localizer.Get("AiDeletePermanently"),
            MinWidth = 120,
            IsDefault = true,
        };
        var cancel = new Button
        {
            Content = Localizer.Get("DialogCancel"),
            MinWidth = 80,
            IsCancel = true,
        };
        var dialog = new Window
        {
            Title = Localizer.Get("AiDeletePermanently"),
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 16,
                Children =
                {
                    new TextBlock
                    {
                        Text = string.Format(Localizer.Get("AiDeletePermanentlyPrompt"), title),
                        TextWrapping = TextWrapping.Wrap,
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { delete, cancel },
                    },
                },
            },
        };

        delete.Click += (_, _) =>
        {
            tcs.TrySetResult(true);
            dialog.Close();
        };
        cancel.Click += (_, _) =>
        {
            tcs.TrySetResult(false);
            dialog.Close();
        };
        dialog.Closed += (_, _) => tcs.TrySetResult(false);
        dialog.ShowDialog(owner);
        return tcs.Task;
    }

    private static string FormatMetadata(AiConversationSummary conversation)
    {
        var model = string.IsNullOrWhiteSpace(conversation.Model) ? "" : $" · {conversation.Model}";
        var unavailable = conversation.CanRestore ? "" : $" · {Localizer.Get("AiConversationNotReady")}";
        return $"{conversation.Provider}{model} · "
               + $"{conversation.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm} · "
               + $"{string.Format(Localizer.Get("AiConversationMessages"), conversation.MessageCount)}{unavailable}";
    }

    private static string FormatPermanentDeleteTime(AiConversationSummary conversation)
    {
        var deletedAt = conversation.DeletedAt ?? conversation.UpdatedAt;
        var permanentDeleteAt = (deletedAt + AiConversationStore.TrashRetention).ToLocalTime();
        return string.Format(Localizer.Get("AiConversationPermanentDeleteAt"),
            permanentDeleteAt.ToString("yyyy-MM-dd HH:mm"));
    }
}
