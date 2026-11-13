using System.Text;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Interactivity;
using JeekRemoteManager.Views;

namespace JeekRemoteManager.Services;

internal static partial class DebugMcpServer
{
    private static async Task<JsonObject> TerminalTabSideCloseCheckAsync()
    {
        MainWindow? window = null;
        var report = new StringBuilder();
        var passed = true;
        void Check(string name, bool ok) { passed &= ok; report.AppendLine($"{name}={ok}"); }
        static void Click(TabItem tab, bool before)
        {
            var name = before ? "TerminalTabCloseLeftItem" : "TerminalTabCloseRightItem";
            var item = tab.ContextMenu!.Items.OfType<MenuItem>().Single(i => i.Name == name);
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        }

        try
        {
            await OnUiAsync(() =>
            {
                window = new MainWindow { Width = 760, Height = 500, ShowActivated = false };
                window.Show();
                var tabs = window.FindControl<TabControl>("RightTabs")!;
                var editor = window.FindControl<TabItem>("EditorTab")!;
                var agent = window.FindControl<TabItem>("GlobalAgentTab")!;
                // Insert the real permanent tab without starting its agent.
                if (!tabs.Items.Contains(agent))
                    tabs.Items.Add(agent);
                var created = Enumerable.Range(0, 8).Select(window.DebugCreatePaginationTab).ToArray();
                var anchor = created[3];
                // Simulate reordering: the final tab now precedes the anchor.
                tabs.Items.Remove(created[7]);
                tabs.Items.Insert(tabs.Items.IndexOf(anchor), created[7]);
                tabs.Items.Remove(agent);
                tabs.Items.Insert(tabs.Items.IndexOf(anchor) + 1, agent);
                tabs.SelectedItem = created[0];
                Click(anchor, before: false);
                Check("rightUsesCurrentOrder", created.Take(4).Append(created[7]).All(t => tabs.Items.Contains(t))
                    && created.Skip(4).Take(3).All(t => !tabs.Items.Contains(t)));
                Check("backgroundCloseKeepsSelection", ReferenceEquals(tabs.SelectedItem, created[0]));
                Check("rightKeepsPermanentTabs", tabs.Items.Contains(editor) && tabs.Items.Contains(agent));
                var count = tabs.Items.Count;
                Click(anchor, before: false);
                Check("rightBoundaryNoOp", tabs.Items.Count == count && ReferenceEquals(tabs.SelectedItem, created[0]));
                return true;
            });
            // Allow the real header panel to apply a search before invoking the left action.
            await OnUiAsync(() =>
            {
                window!.FindControl<TextBox>("TabSearchBox")!.Text = "server-003";
                return true;
            });
            await Task.Delay(100);
            await OnUiAsync(() =>
            {
                var main = window!;
                var tabs = main.FindControl<TabControl>("RightTabs")!;
                var anchor = tabs.Items.OfType<TabItem>().Single(t => t.Content is TerminalView view
                    && view.SourcePath == "pagination/folder/server-003");
                var changes = 0;
                void OnChanged(object? sender, SelectionChangedEventArgs e)
                {
                    if (ReferenceEquals(e.Source, sender)) changes++;
                }
                tabs.SelectionChanged += OnChanged;
                try { Click(anchor, before: true); }
                finally { tabs.SelectionChanged -= OnChanged; }
                Check("leftClosesFilteredAndReorderedTabs", tabs.Items.OfType<TabItem>().Count(t => t.Content is TerminalView) == 1);
                Check("closedSelectionMovesDirectlyToAnchor", ReferenceEquals(tabs.SelectedItem, anchor) && changes == 1);
                Check("leftKeepsPermanentTabs", tabs.Items.Contains(main.FindControl<TabItem>("EditorTab")!)
                    && tabs.Items.Contains(main.FindControl<TabItem>("GlobalAgentTab")!));
                var count = tabs.Items.Count;
                Click(anchor, before: true);
                Check("leftBoundaryNoOp", tabs.Items.Count == count && ReferenceEquals(tabs.SelectedItem, anchor));
                var right = main.DebugCreatePaginationTab(9);
                tabs.SelectedItem = right;
                Click(anchor, before: false);
                Check("rightClosedSelectionMovesToAnchor", !tabs.Items.Contains(right) && ReferenceEquals(tabs.SelectedItem, anchor));
                return true;
            });
            return ToolText($"{(passed ? "PASS" : "FAIL")}: tab context-menu side close\n{report}", isError: !passed);
        }
        finally
        {
            await OnUiAsync(() =>
            {
                if (window is not null)
                {
                    window.CloseAllTerminalTabs();
                    window.Close();
                }
                return true;
            });
        }
    }
}
