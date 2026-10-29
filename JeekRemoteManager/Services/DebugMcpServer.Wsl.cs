using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using JeekRemoteManager.Models;
using JeekRemoteManager.ViewModels;
using JeekRemoteManager.Views;

namespace JeekRemoteManager.Services;

internal static partial class DebugMcpServer
{
    private static async Task<JsonObject> WslEditorSelectionCheckAsync()
    {
        // Detect the old getter before binding: its feedback loop can overflow
        // the UI thread's stack, which cannot be caught by a probe's try/catch.
        var stable = await OnUiAsync(() =>
        {
            var editor = new ConnectionEditorViewModel { WslDistro = "Jeek-Wsl-probe" };
            return ReferenceEquals(editor.AvailableWslDistros, editor.AvailableWslDistros);
        });
        if (!stable)
            return ToolText("FAIL: unchanged WSL choices replace ItemsSource.", isError: true);
        var available = await OnUiAsync(() => ResolveRoot("MainWindow") is MainWindow);
        if (!available)
            return ToolText("FAIL: MainWindow is not available.", isError: true);

        var state = await OnUiAsync(() =>
        {
            var editor = new ConnectionEditorViewModel
            {
                WslDistro = "Jeek-Wsl-probe", Type = ConnectionType.Wsl,
            };
            // Use the production template and its compiled bindings, rather than
            // a separate reflection-binding approximation of the editor.
            var mainWindow = (MainWindow)ResolveRoot("MainWindow")!;
            var template = mainWindow.GetLogicalDescendants().OfType<ContentControl>()
                .SelectMany(control => control.DataTemplates).First(item => item.Match(editor));
            var view = template.Build(editor)!;
            view.DataContext = editor;
            var window = new Window
            {
                Content = view, Width = 800, Height = 500,
                ShowInTaskbar = false, ShowActivated = false, Title = "WSL selection probe",
            };
            window.Show();
            try
            {
                var selector = view.GetLogicalDescendants().OfType<ComboBox>()
                    .First(box => ReferenceEquals(box.ItemsSource, editor.AvailableWslDistros));
                return (Editor: editor, Selector: selector, View: view, Window: window);
            }
            catch
            {
                window.Close();
                throw;
            }
        });

        try
        {
            var initialized = await ActThenReadAsync(() => { },
                () => Equals(state.Selector.SelectedItem, state.Editor.WslDistro));
            var recovered = await ActThenReadAsync(
                () => state.Editor.WslDistro = null!,
                () => state.Editor.WslDistro == "Jeek-Wsl-probe"
                    && Equals(state.Selector.SelectedItem, state.Editor.WslDistro));
            var nextEditor = await OnUiAsync(() => new ConnectionEditorViewModel
            {
                WslDistro = "Jeek-Wsl-probe-next", Type = ConnectionType.Wsl,
            });
            var rebound = await ActThenReadAsync(
                () => state.View.DataContext = nextEditor,
                () => nextEditor.WslDistro == "Jeek-Wsl-probe-next"
                    && Equals(state.Selector.SelectedItem, nextEditor.WslDistro)
                    && ReferenceEquals(state.Selector.ItemsSource, nextEditor.AvailableWslDistros));
            var passed = initialized && recovered && rebound;
            return ToolText(
                $"{(passed ? "PASS" : "FAIL")}: WSL editor selection\n"
                + $"stableItemsSource={stable}\ninitialized={initialized}\nselectionRecovered={recovered}\neditorRebound={rebound}",
                isError: !passed);
        }
        finally
        {
            await OnUiAsync(() =>
            {
                state.Window.Close();
                state.Window.Content = null;
                return true;
            });
        }
    }
}
