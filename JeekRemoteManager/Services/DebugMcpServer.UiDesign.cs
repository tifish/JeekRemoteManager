using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using JeekRemoteManager.Models;
using JeekRemoteManager.ViewModels;
using JeekRemoteManager.Views;
using JeekTools;

namespace JeekRemoteManager.Services;

internal static partial class DebugMcpServer
{
    /// <summary>
    /// Verifies desktop UI design invariants: WCAG AA contrast across Light and Dark themes,
    /// uniform standard/compact control heights, destructive button styling, absence of
    /// unintended horizontal scrollbars in sidebars and editors, empty-state action visibility,
    /// and minimum/wide window resize resilience.
    /// </summary>
    private static async Task<JsonObject> UiDesignAuditCheckAsync()
    {
        if (!DebugInstanceContext.IsDebugBuild)
            return ToolText("Run this probe in a Debug instance.", true);

        var lines = new List<string>();
        var passed = true;

        void Record(bool ok, string message)
        {
            passed &= ok;
            lines.Add($"{(ok ? "ok" : "FAIL")}: {message}");
        }

        // 1. WCAG AA contrast verification across Light and Dark theme dictionaries.
        await OnUiAsync(() =>
        {
            var app = Application.Current;
            if (app is null)
            {
                Record(false, "Application.Current is null.");
                return false;
            }

            var textPairs = new (string Fg, string Bg, double MinRatio)[]
            {
                ("TextPrimaryBrush", "AppBackgroundBrush", 4.5),
                ("TextPrimaryBrush", "ChromeSurfaceBrush", 4.5),
                ("TextPrimaryBrush", "SurfaceBrush", 4.5),
                ("TextPrimaryBrush", "SurfaceAltBrush", 4.5),
                ("TextPrimaryBrush", "PopupSurfaceBrush", 4.5),
                ("TextMutedBrush", "AppBackgroundBrush", 4.5),
                ("TextMutedBrush", "ChromeSurfaceBrush", 4.5),
                ("TextMutedBrush", "SurfaceBrush", 4.5),
                ("TextMutedBrush", "SurfaceAltBrush", 4.5),
                ("AccentBrush", "SurfaceBrush", 4.5),
                ("AccentBrush", "SurfaceAltBrush", 4.5),
                ("AccentForegroundBrush", "AccentBrush", 4.5),
                ("SuccessBrush", "SurfaceBrush", 4.5),
                ("SuccessBrush", "SurfaceAltBrush", 4.5),
                ("WarningBrush", "SurfaceBrush", 4.5),
                ("WarningBrush", "SurfaceAltBrush", 4.5),
                ("DangerBrush", "SurfaceBrush", 4.5),
                ("DangerBrush", "SurfaceAltBrush", 4.5),
                ("NetDownloadBrush", "SurfaceBrush", 4.5),
                ("NetUploadBrush", "SurfaceBrush", 4.5),
                ("ToolbarKeyIconBrush", "ChromeSurfaceBrush", 3.0),
            };

            foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                var failures = new List<string>();
                foreach (var (fgKey, bgKey, minRatio) in textPairs)
                {
                    if (!app.TryFindResource(fgKey, variant, out var fgObj) || fgObj is not ISolidColorBrush fgBrush
                        || !app.TryFindResource(bgKey, variant, out var bgObj) || bgObj is not ISolidColorBrush bgBrush)
                    {
                        failures.Add($"{fgKey}/{bgKey}=missing");
                        continue;
                    }

                    var ratio = ContrastRatio(fgBrush.Color, bgBrush.Color);
                    if (ratio < minRatio)
                        failures.Add($"{fgKey}({fgBrush.Color}) on {bgKey}({bgBrush.Color})={ratio:F2}<{minRatio:F1}");
                }

                Record(failures.Count == 0,
                    $"contrast[{variant.Key}]: {(failures.Count == 0 ? $"all {textPairs.Length} pairs >= WCAG AA" : string.Join("; ", failures))}");
            }

            return true;
        });

        // 2. Uniform control heights and semantic styles in an isolated window.
        await OnUiAsync(() =>
        {
            if (Desktop?.MainWindow is not MainWindow main)
            {
                Record(false, "MainWindow is not available.");
                return false;
            }

            var stdButton = new Button { Content = "Standard" };
            var dangerButton = new Button { Content = "Delete", Classes = { "danger" } };
            var stdText = new TextBox { Text = "Sample" };
            var stdCombo = new ComboBox { ItemsSource = new[] { "Option A", "Option B" }, SelectedIndex = 0 };
            var compactText = new TextBox { Text = "Search", Classes = { "compact-input" } };
            var compactCombo = new ComboBox { ItemsSource = new[] { "CLI" }, SelectedIndex = 0, Classes = { "compact-combo" } };
            var compactIcon = new Button { Content = "X", Classes = { "toolbar", "compact-icon-button" } };

            var probe = new Window
            {
                Title = "ui_design_audit_controls",
                Width = 480,
                Height = 200,
                ShowInTaskbar = false,
                Content = new StackPanel
                {
                    Margin = new Thickness(16),
                    Spacing = 12,
                    Children =
                    {
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Spacing = 8,
                            Children = { stdButton, dangerButton, stdText, stdCombo },
                        },
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Spacing = 8,
                            Children = { compactText, compactCombo, compactIcon },
                        },
                    },
                },
            };

            try
            {
                probe.Show(main);
                probe.UpdateLayout();

                var stdHeightsMatch = Math.Abs(stdButton.Bounds.Height - 32) <= 1.0
                                      && Math.Abs(stdText.Bounds.Height - 32) <= 1.0
                                      && Math.Abs(stdCombo.Bounds.Height - 32) <= 1.0
                                      && Math.Abs(stdButton.Bounds.Height - stdText.Bounds.Height) <= 0.5
                                      && Math.Abs(stdCombo.Bounds.Height - stdText.Bounds.Height) <= 0.5;
                Record(stdHeightsMatch,
                    $"standardHeights: Button={stdButton.Bounds.Height:F1}, TextBox={stdText.Bounds.Height:F1}, ComboBox={stdCombo.Bounds.Height:F1}");

                var compactHeightsMatch = Math.Abs(compactText.Bounds.Height - 28) <= 0.5
                                          && Math.Abs(compactCombo.Bounds.Height - 28) <= 0.5
                                          && Math.Abs(compactIcon.Bounds.Height - 28) <= 0.5;
                Record(compactHeightsMatch,
                    $"compactHeights: TextBox={compactText.Bounds.Height:F1}, ComboBox={compactCombo.Bounds.Height:F1}, IconBtn={compactIcon.Bounds.Height:F1}");

                var dangerStyled = Application.Current!.TryFindResource("DangerBrush", probe.ActualThemeVariant, out var dObj)
                                   && dObj is ISolidColorBrush expectedDanger
                                   && dangerButton.Foreground is ISolidColorBrush actualDanger
                                   && actualDanger.Color == expectedDanger.Color;
                Record(dangerStyled, $"dangerButtonForeground={dangerButton.Foreground}");

                // Live MainWindow sidebar horizontal scrollbar hygiene.
                var mainTree = main.FindControl<TreeView>("Tree");
                var mainTreeScroll = mainTree?.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
                var mainTreeHBar = mainTreeScroll?.GetVisualDescendants().OfType<ScrollBar>()
                    .FirstOrDefault(bar => bar.Orientation == Orientation.Horizontal);
                var mainTreeNoHScroll = mainTree is not null
                                        && ScrollViewer.GetHorizontalScrollBarVisibility(mainTree) == ScrollBarVisibility.Disabled
                                        && mainTreeHBar?.IsVisible != true;
                Record(mainTreeNoHScroll,
                    $"mainTreeNoHorizontalScroll: policy={mainTree?.GetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty)}, hBarVisible={mainTreeHBar?.IsVisible}");
            }
            finally
            {
                probe.Close();
            }

            return true;
        });

        // 3. Isolated MainWindow resize, empty-state, and long-name resilience test.
        var root = Path.Combine(Path.GetTempPath(), "jrm-ui-audit-" + Guid.NewGuid().ToString("N"));
        MainWindow? isolatedWindow = null;
        MainWindowViewModel? isolatedVm = null;
        try
        {
            await OnUiAsync(() =>
            {
                var settings = new SettingsService(Path.Combine(root, "machine.json"), Path.Combine(root, "roaming.json"));
                settings.Settings.StorageLocation = StorageLocation.CustomDirectory;
                settings.Settings.CustomStoragePath = root;
                settings.Settings.ConnectionPanelWidth = 306;
                var store = new ConnectionStore(settings.ResolveConnectionsRoot());
                store.Save(new Connection
                {
                    Name = "production-cluster-eu-central-very-long-connection-name-001.example.internal",
                    Type = ConnectionType.Ssh,
                    Host = "10.20.30.40",
                    Port = 2222,
                    Username = "deploy",
                }, store.RootPath);

                isolatedVm = new MainWindowViewModel(store, new ConnectionLauncher(), settings);
                isolatedWindow = new MainWindow
                {
                    DataContext = isolatedVm,
                    ShowInTaskbar = false,
                    Width = 760,
                    Height = 500,
                };
                isolatedWindow.Show();
                isolatedVm.SelectedNode = null;
                isolatedWindow.UpdateLayout();
                return true;
            });

            await Dispatcher.UIThread.InvokeAsync(() => true, DispatcherPriority.Background);

            await OnUiAsync(() =>
            {
                var window = isolatedWindow!;
                var vm = isolatedVm!;

                // Empty state: helpful action button visible when nothing is selected.
                var emptyBtn = window.FindControl<Button>("EmptyStateNewSshButton");
                Record(emptyBtn?.IsEffectivelyVisible == true,
                    $"emptyStateActionVisible={emptyBtn?.IsEffectivelyVisible}");

                // Tree horizontal scrollbar stays disabled & hidden even with an 80-char connection name.
                var tree = window.FindControl<TreeView>("Tree");
                var treeHBar = tree?.GetVisualDescendants().OfType<ScrollBar>()
                    .FirstOrDefault(bar => bar.Orientation == Orientation.Horizontal);
                Record(treeHBar?.IsVisible != true,
                    $"isolatedTreeLongNameNoHScroll: hBarVisible={treeHBar?.IsVisible}");

                // Select the long-name SSH connection and check editor scrollbars at MinWidth (760x500).
                vm.SelectedNode = vm.Nodes.First(node => node.IsConnection);
                window.UpdateLayout();
                return true;
            });

            await Dispatcher.UIThread.InvokeAsync(() => true, DispatcherPriority.Background);

            await OnUiAsync(() =>
            {
                var window = isolatedWindow!;
                var editorScroll = window.GetVisualDescendants().OfType<ScrollViewer>()
                    .FirstOrDefault(sv => sv.Name == "EditorScrollViewer");
                var editorHBar = editorScroll?.GetVisualDescendants().OfType<ScrollBar>()
                    .FirstOrDefault(bar => bar.Orientation == Orientation.Horizontal);
                var editorNoHScroll = editorScroll is not null
                                      && editorScroll.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled
                                      && editorHBar?.IsVisible != true;
                Record(editorNoHScroll,
                    $"editorNoHScrollAtMinWidth(760x500): policy={editorScroll?.HorizontalScrollBarVisibility}, hBarVisible={editorHBar?.IsVisible}");

                var commandBar = window.FindControl<Grid>("CommandBar");
                var toolbarBrand = window.FindControl<StackPanel>("ToolbarBrand");
                var toolbarNew = window.FindControl<StackPanel>("ToolbarNew");
                var toolbarTerminal = window.FindControl<StackPanel>("ToolbarTerminal");
                var noToolbarOverlap = toolbarBrand is not null && toolbarNew is not null && toolbarTerminal is not null
                                       && toolbarBrand.Bounds.Right <= toolbarNew.Bounds.Left + 1
                                       && toolbarNew.Bounds.Right <= toolbarTerminal.Bounds.Left + 1;
                Record(noToolbarOverlap,
                    $"commandBarNoOverlapAtMinWidth: compact={commandBar?.Classes.Contains("compact")}, brandRight={toolbarBrand?.Bounds.Right:F0}, new={toolbarNew?.Bounds.Left:F0}..{toolbarNew?.Bounds.Right:F0}, termLeft={toolbarTerminal?.Bounds.Left:F0}");

                // Resize to wide desktop (1440x900) and verify clean expansion.
                window.Width = 1440;
                window.Height = 900;
                window.UpdateLayout();
                return true;
            });

            await Dispatcher.UIThread.InvokeAsync(() => true, DispatcherPriority.Background);

            await OnUiAsync(() =>
            {
                var window = isolatedWindow!;
                var toolbarBrand = window.FindControl<StackPanel>("ToolbarBrand");
                var toolbarNew = window.FindControl<StackPanel>("ToolbarNew");
                var toolbarTerminal = window.FindControl<StackPanel>("ToolbarTerminal");
                var wideNoOverlap = toolbarBrand is not null && toolbarNew is not null && toolbarTerminal is not null
                                    && toolbarBrand.Bounds.Right <= toolbarNew.Bounds.Left + 1
                                    && toolbarNew.Bounds.Right <= toolbarTerminal.Bounds.Left + 1;
                Record(wideNoOverlap,
                    $"commandBarNoOverlapAtWide(1440x900): brandRight={toolbarBrand?.Bounds.Right:F0}, newRight={toolbarNew?.Bounds.Right:F0}, termLeft={toolbarTerminal?.Bounds.Left:F0}");
                return true;
            });
        }
        finally
        {
            await OnUiAsync(() =>
            {
                isolatedVm?.StopWatchingForDebug();
                isolatedWindow?.Close();
                if (isolatedWindow is not null)
                    isolatedWindow.DataContext = null;
                return true;
            });
            if (Directory.Exists(root))
            {
                try { Directory.Delete(root, recursive: true); }
                catch (IOException) { }
            }
        }

        return ToolText(
            $"{(passed ? "PASS" : "FAIL")}: UI design audit\n" + string.Join("\n", lines),
            isError: !passed);
    }

    private static double ContrastRatio(Color foreground, Color background)
    {
        var l1 = RelativeLuminance(foreground);
        var l2 = RelativeLuminance(background);
        var lighter = Math.Max(l1, l2);
        var darker = Math.Min(l1, l2);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double RelativeLuminance(Color color)
    {
        static double Channel(byte value)
        {
            var c = value / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
    }
}
