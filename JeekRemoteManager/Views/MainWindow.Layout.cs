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

/// <summary>Window geometry and chrome: connection panel width, window size and position, toolbar compaction.</summary>
public partial class MainWindow
{
    private void RestoreConnectionPanelWidth(MainWindowViewModel vm)
    {
        if (_treePanelWidthRestored)
            return;

        _treePanelWidthRestored = true;
        _treePanelWidth = vm.ConnectionPanelWidth;
        ApplyConnectionPanelState(vm.ConnectionPanelCollapsed);
    }

    // ColumnDefinitions don't generate fields, so reach the tree column through the grid.
    private ColumnDefinition TreeColumn => MainGrid.ColumnDefinitions[0];

    private void OnToggleConnectionPanelClick(object? sender, RoutedEventArgs e)
    {
        var collapsed = TreePanel.IsVisible;
        ApplyConnectionPanelState(collapsed);
    }

    /// <summary>Collapses or expands the connection tree panel. Collapsing remembers
    /// the splitter-set width so expanding restores it within the session.</summary>
    private void ApplyConnectionPanelState(bool collapsed)
    {
        if (collapsed && TreeColumn.Width.IsAbsolute && TreeColumn.Width.Value > 0)
        {
            _treePanelWidth = TreeColumn.Width.Value;
            PersistConnectionPanelWidth();
        }

        TreePanel.IsVisible = !collapsed;
        TreeSplitter.IsVisible = !collapsed;
        TreeColumn.Width = new GridLength(collapsed ? 0 : _treePanelWidth, GridUnitType.Pixel);
        // Zero the spacing too, or the two inter-column gaps would leave a
        // 16px dead strip at the left edge while the panel is hidden.
        MainGrid.ColumnSpacing = collapsed ? 0 : 8;
        ToggleTreePanelIcon.Text = collapsed ? "\uE8A0" : "\uE89F"; // OpenPane / ClosePane
        ToggleTreePanelButton.Classes.Set("panel-on", !collapsed);

        if (DataContext is MainWindowViewModel vm)
            vm.ConnectionPanelCollapsed = collapsed;
    }

    private void PersistConnectionPanelWidth()
    {
        if (TreePanel.IsVisible && TreeColumn.Width.IsAbsolute && TreeColumn.Width.Value > 0)
            _treePanelWidth = TreeColumn.Width.Value;

        if (DataContext is MainWindowViewModel vm)
            vm.ConnectionPanelWidth = _treePanelWidth;
    }

    private void RestoreWindowSize(MainWindowViewModel vm)
    {
        if (_windowSizeRestored)
            return;

        _windowSizeRestored = true;
        if (!vm.TryGetSavedMainWindowSize(out var width, out var height))
        {
            width = Width;
            height = Height;
        }

        _ignoreWindowSizeChange = true;
        try
        {
            var size = ClampWindowSizeToCurrentScreen(width, height);
            Width = size.Width;
            Height = size.Height;

            // Restore the last position only when a grabbable part of the title
            // bar still lands on a live screen; otherwise keep CenterScreen so a
            // window saved on a since-removed monitor stays reachable.
            if (vm.TryGetSavedMainWindowPosition(out var x, out var y)
                && IsPositionOnAnyScreen(x, y))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Position = new PixelPoint(x, y);
            }

            // Set after normal bounds so un-maximizing returns to them.
            if (vm.MainWindowMaximized)
                WindowState = WindowState.Maximized;
        }
        finally
        {
            _ignoreWindowSizeChange = false;
        }
    }

    private void EnsureWindowFitsCurrentScreen()
    {
        // While maximized, Bounds is the full working area; clamping Width/Height
        // to it would corrupt the remembered normal-state size.
        if (WindowState != WindowState.Normal)
            return;

        _ignoreWindowSizeChange = true;
        try
        {
            var size = ClampWindowSizeToCurrentScreen(Bounds.Width, Bounds.Height);
            Width = size.Width;
            Height = size.Height;
        }
        finally
        {
            _ignoreWindowSizeChange = false;
        }
    }

    private void OnWindowSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (!_canPersistWindowSize
            || !_windowSizeRestored
            || _ignoreWindowSizeChange
            || WindowState != WindowState.Normal)
            return;

        ScheduleWindowSizeSave();
    }

    private void OnWindowPositionChanged(object? sender, PixelPointEventArgs e)
    {
        if (!_canPersistWindowSize
            || !_windowSizeRestored
            || _ignoreWindowSizeChange
            || WindowState != WindowState.Normal)
            return;

        ScheduleWindowSizeSave();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // Record maximize/restore transitions (minimize and fullscreen are
        // transient states and never persisted).
        if (change.Property != WindowStateProperty
            || !_canPersistWindowSize
            || !_windowSizeRestored
            || _ignoreWindowSizeChange
            || DataContext is not MainWindowViewModel vm)
            return;

        if (WindowState == WindowState.Maximized)
            vm.MainWindowMaximized = true;
        else if (WindowState == WindowState.Normal)
            vm.MainWindowMaximized = false;
    }

    private void ScheduleWindowSizeSave()
    {
        if (_windowSizeSaveTimer is null)
        {
            _windowSizeSaveTimer = new DispatcherTimer { Interval = WindowSizeSaveDelay };
            _windowSizeSaveTimer.Tick += (_, _) =>
            {
                _windowSizeSaveTimer!.Stop();
                SaveCurrentWindowSize(DataContext as MainWindowViewModel);
            };
        }

        _windowSizeSaveTimer.Stop();
        _windowSizeSaveTimer.Start();
    }

    private void SaveCurrentWindowSize(MainWindowViewModel? vm)
    {
        _windowSizeSaveTimer?.Stop();

        if (vm is null
            || !_canPersistWindowSize
            || !_windowSizeRestored)
            return;

        if (WindowState is WindowState.Maximized or WindowState.Normal)
            vm.MainWindowMaximized = WindowState == WindowState.Maximized;

        if (WindowState != WindowState.Normal)
            return;

        vm.SaveMainWindowSize(Bounds.Width, Bounds.Height);
        vm.SaveMainWindowPosition(Position.X, Position.Y);
    }

    /// <summary>True when a 160x48 physical-pixel strip at the window's top-left
    /// corner intersects some screen's working area, i.e. the title bar stays grabbable.</summary>
    private bool IsPositionOnAnyScreen(int x, int y)
    {
        var titleStrip = new PixelRect(x, y, 160, 48);
        foreach (var screen in Screens.All)
        {
            if (screen.WorkingArea.Intersects(titleStrip))
                return true;
        }

        return false;
    }

    private Size ClampWindowSizeToCurrentScreen(double width, double height)
    {
        if (!double.IsFinite(width) || width <= 0)
            width = Width;
        if (!double.IsFinite(height) || height <= 0)
            height = Height;

        if (TryGetCurrentScreenWorkingSize(out var workingSize))
        {
            MinWidth = Math.Min(_defaultMinWidth, workingSize.Width);
            MinHeight = Math.Min(_defaultMinHeight, workingSize.Height);
            width = Math.Min(width, workingSize.Width);
            height = Math.Min(height, workingSize.Height);
        }

        width = ClampWindowDimension(width, MinWidth);
        height = ClampWindowDimension(height, MinHeight);
        return new Size(width, height);
    }

    private bool TryGetCurrentScreenWorkingSize(out Size workingSize)
    {
        workingSize = default;

        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null)
            return false;

        var scaling = screen.Scaling > 0 ? screen.Scaling : 1;
        workingSize = new Size(screen.WorkingArea.Width / scaling, screen.WorkingArea.Height / scaling);
        return workingSize.Width > 0 && workingSize.Height > 0;
    }

    private static double ClampWindowDimension(double value, double minimum) =>
        double.IsFinite(minimum) && minimum > 0 ? Math.Max(value, minimum) : value;

    // Natural (label-included) width the command bar wanted when it last switched
    // to compact; the bar expands back once the window offers at least that much.
    private double _toolbarFullWidth;

    /// <summary>
    /// Toggles the command bar's icon-only mode. The bar's sections are plain
    /// horizontal StackPanels, which do not shrink — on a too-narrow window they
    /// would just paint over each other. So after every layout pass, compare the
    /// buttons' natural width against the available width and hide the text labels
    /// (the "compact" style class) when they no longer fit.
    /// </summary>
    private void UpdateToolbarCompactMode()
    {
        var available = CommandBar.Bounds.Width;
        if (available <= 0)
            return;

        if (!CommandBar.Classes.Contains("compact"))
        {
            var needed = NaturalPanelWidth(ToolbarBrand)
                         + NaturalPanelWidth(ToolbarNew)
                         + NaturalPanelWidth(ToolbarTerminal)
                         + CommandBar.ColumnSpacing * 2;
            if (needed > available)
            {
                _toolbarFullWidth = needed;
                CommandBar.Classes.Add("compact");
            }
        }
        else if (available >= _toolbarFullWidth)
        {
            // Optimistic when the cached width is stale (e.g. buttons appeared or
            // disappeared meanwhile): labels come back, and if they still do not
            // fit the next pass re-compacts with a fresh measurement.
            CommandBar.Classes.Remove("compact");
        }
    }

    /// <summary>Sum of the visible children's desired widths plus spacing — the width
    /// the panel paints at, unlike its DesiredSize which the Grid clamps.</summary>
    private static double NaturalPanelWidth(StackPanel panel)
    {
        double width = 0;
        var visibleCount = 0;
        foreach (var child in panel.Children)
        {
            if (!child.IsVisible)
                continue;
            width += child.DesiredSize.Width;
            visibleCount++;
        }

        if (visibleCount > 1)
            width += panel.Spacing * (visibleCount - 1);
        return width;
    }
}
