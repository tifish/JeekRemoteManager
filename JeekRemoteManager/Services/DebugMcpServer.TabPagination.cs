using System.Text;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using JeekRemoteManager.Controls;
using JeekRemoteManager.Views;

namespace JeekRemoteManager.Services;

internal static partial class DebugMcpServer
{
    private static async Task<JsonObject> TerminalTabPaginationCheckAsync()
    {
        MainWindow? window = null;
        TabControl? tabs = null;
        PagedTabPanel? panel = null;
        TabItem[] created = [];
        var report = new StringBuilder();
        var passed = true;
        void Check(string name, bool ok) { passed &= ok; report.AppendLine($"{name}={ok}"); }
        void Click(string name) => window!.FindControl<Button>(name)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        try
        {
            await OnUiAsync(() =>
            {
                window = new MainWindow { Width = 1000, Height = 700, ShowActivated = false };
                tabs = window.FindControl<TabControl>("RightTabs")!;
                created = Enumerable.Range(0, 41).Select(window.DebugCreatePaginationTab).ToArray();
                window.Show();
                tabs.SelectedItem = created[^1];
                return true;
            });
            await Task.Delay(200);
            await OnUiAsync(() =>
            {
                panel = tabs!.GetVisualDescendants().OfType<PagedTabPanel>().Single();
                Check("defaultFiveRows", panel.MaxRows == 5 && panel.VisibleRows is > 0 and <= 5);
                Check("multiplePagesAndActiveVisible", panel.PageCount > 1 && panel.IsOnCurrentPage(created[^1]));
                Check("counter", window!.FindControl<TextBlock>("TabPageText")!.Text == $"{panel.CurrentPage + 1} / {panel.PageCount}");
                Click("PreviousTabPageButton");
                return true;
            });
            await Task.Delay(100);
            await OnUiAsync(() =>
            {
                Check("manualPagingKeepsSession", ReferenceEquals(tabs!.SelectedItem, created[^1]) && !panel!.IsOnCurrentPage(created[^1]));
                Click("NextTabPageButton");
                window!.FindControl<NumericUpDown>("TabMaxRowsBox")!.Value = 2;
                return true;
            });
            await Task.Delay(100);
            await OnUiAsync(() =>
            {
                Check("configuredRows", panel!.MaxRows == 2 && panel.VisibleRows <= 2 && panel.IsOnCurrentPage(created[^1]));
                window!.FindControl<TextBox>("TabSearchBox")!.Text = "FOLDER/server-007";
                return true;
            });
            await Task.Delay(100);
            await OnUiAsync(() =>
            {
                Check("pathSearch", panel!.MatchingCount == 1 && panel.PageCount == 1 && panel.IsOnCurrentPage(created[7]));
                tabs!.SelectedItem = created[0];
                return true;
            });
            await Task.Delay(100);
            await OnUiAsync(() =>
            {
                Check("selectionClearsExcludingFilter", window!.FindControl<TextBox>("TabSearchBox")!.Text == "" && panel!.IsOnCurrentPage(created[0]));
                window!.FindControl<TextBox>("TabSearchBox")!.Text = "no-such-tab";
                return true;
            });
            await Task.Delay(100);
            await OnUiAsync(() =>
            {
                Check("emptySearch", panel!.MatchingCount == 0 && panel.PageCount == 1 && window!.FindControl<TextBlock>("TabPageText")!.Text == "1 / 1");
                Click("ClearTabSearchButton");
                window!.Width = 760;
                window.Height = 500;
                window.FindControl<NumericUpDown>("TabMaxRowsBox")!.Value = 20;
                return true;
            });
            await Task.Delay(150);
            await OnUiAsync(() =>
            {
                Check("smallWindowKeepsTerminalSpace", tabs!.Bounds.Height - panel!.Bounds.Height >= 119 && panel.IsOnCurrentPage(created[0]));
                var rowsBox = window!.FindControl<NumericUpDown>("TabMaxRowsBox")!;
                var rowsEditor = rowsBox.GetVisualDescendants().OfType<TextBox>().FirstOrDefault();
                Check("rowSettingReadable", rowsEditor is not null && rowsEditor.Bounds.Width >= 20);
                var next = window!.FindControl<Button>("NextTabPageButton")!;
                var nextOrigin = next.TranslatePoint(default, tabs);
                Check("pagingControlsFit", nextOrigin is { } origin && origin.X + next.Bounds.Width <= tabs.Bounds.Width + 1);
                tabs.SelectedItem = created[^1];
                return true;
            });
            await Task.Delay(100);
            await OnUiAsync(() =>
            {
                Check("resizeAndExternalSelection", panel!.IsOnCurrentPage(created[^1]));
                foreach (var tab in created.Skip(1))
                    window!.CloseTerminalSession(tab);
                return true;
            });
            await Task.Delay(100);
            await OnUiAsync(() =>
            {
                Check("closeClampsPage", panel!.CurrentPage == 0 && panel.PageCount == 1);
                return true;
            });
            return ToolText($"{(passed ? "PASS" : "FAIL")}: tab pagination\n{report}", isError: !passed);
        }
        finally
        {
            await OnUiAsync(() =>
            {
                if (window is not null)
                {
                    foreach (var tab in created)
                        window.CloseTerminalSession(tab);
                    window.Close();
                }
                return true;
            });
        }
    }
}
