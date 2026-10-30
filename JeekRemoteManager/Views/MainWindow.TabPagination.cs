using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using JeekRemoteManager.Controls;
using JeekRemoteManager.ViewModels;

namespace JeekRemoteManager.Views;

public partial class MainWindow
{
    private PagedTabPanel? _tabPages;

    private void InitializeTabPagination()
    {
        RightTabs.LayoutUpdated += (_, _) =>
        {
            if (_tabPages is null && RightTabs.GetVisualDescendants().OfType<PagedTabPanel>().FirstOrDefault() is { } panel)
            {
                _tabPages = panel;
                panel.PaginationChanged += (_, _) => UpdateTabPaginationStatus();
                ApplyTabSearch();
                EnsureSelectedTabVisible();
            }
            if (_tabPages is { } pages)
            {
                pages.MaxRows = (int)(TabMaxRowsBox.Value ?? 5);
                pages.HeightBudget = Math.Max(34, RightTabs.Bounds.Height - 120);
            }
        };
    }

    internal TabItem DebugCreatePaginationTab(int index)
    {
        var connection = new Models.Connection { Name = $"pagination-server-prod-{index:000}", Host = $"host-{index}", Type = Models.ConnectionType.Ssh };
        var (view, tab) = CreateTerminalTab(connection, $"pagination/folder/server-{index:000}", select: false);
        view.DebugAttachConnectionWithoutConnecting(connection, $"pagination/folder/server-{index:000}");
        return tab;
    }

    private string TabSearchText(Control control)
    {
        if (control is TabItem { Content: TerminalView view } tab && view.Connection is { } connection)
            return $"{GetTerminalTabTitle(connection)} {connection.Name} {connection.Host} {view.SourcePath} {(tab.Tag is int n ? n : 1)}";
        if (ReferenceEquals(control, EditorTab) && DataContext is MainWindowViewModel vm)
            return vm.EditorTabTitle;
        return control is TabItem item
            ? $"{item.Header as string} {string.Join(" ", (item.Header as Control)?.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text) ?? [])}"
            : "";
    }

    private bool MatchesTabSearch(Control control) =>
        (TabSearchBox?.Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .All(term => TabSearchText(control).Contains(term, StringComparison.OrdinalIgnoreCase));

    private void ApplyTabSearch() => _tabPages?.SetFilter(MatchesTabSearch);

    private void EnsureSelectedTabVisible()
    {
        if (_tabPages is null || RightTabs?.SelectedItem is not Control selected)
            return;
        if (!MatchesTabSearch(selected))
            TabSearchBox.Text = "";
        _tabPages.EnsureVisible(selected);
    }

    private void UpdateTabPaginationStatus()
    {
        if (_tabPages is not { } pages)
            return;
        TabPageText.Text = $"{pages.CurrentPage + 1} / {pages.PageCount}";
        PreviousTabPageButton.IsEnabled = pages.CurrentPage > 0;
        NextTabPageButton.IsEnabled = pages.CurrentPage < pages.PageCount - 1;
        TabSearchCount.Text = string.IsNullOrWhiteSpace(TabSearchBox.Text) ? "" : $"{pages.MatchingCount}";
        ClearTabSearchButton.IsEnabled = !string.IsNullOrEmpty(TabSearchBox.Text);
    }

    private void OnTabSearchChanged(object? sender, TextChangedEventArgs e) => ApplyTabSearch();
    private void OnClearTabSearch(object? sender, RoutedEventArgs e)
    {
        TabSearchBox.Text = "";
        EnsureSelectedTabVisible();
    }
    private void OnPreviousTabPage(object? sender, RoutedEventArgs e) => _tabPages?.MovePage(-1);
    private void OnNextTabPage(object? sender, RoutedEventArgs e) => _tabPages?.MovePage(1);
    private void OnTabMaxRowsChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (_tabPages is not null && e.NewValue is { } rows)
        {
            var wholeRows = Math.Clamp(decimal.Truncate(rows), 1, 20);
            if (wholeRows != rows)
                TabMaxRowsBox.Value = wholeRows;
            _tabPages.MaxRows = (int)wholeRows;
            EnsureSelectedTabVisible();
        }
    }
    private void OnTabSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            TabSearchBox.Text = "";
            EnsureSelectedTabVisible();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && _tabPages is { } pages)
        {
            var match = RightTabs.Items.OfType<TabItem>().FirstOrDefault(pages.IsOnCurrentPage);
            if (match is not null)
            {
                RightTabs.SelectedItem = match;
                EnsureSelectedTabVisible();
                if (match.Content is TerminalView view)
                    view.RestoreLastFocus();
            }
            e.Handled = true;
        }
    }
}
