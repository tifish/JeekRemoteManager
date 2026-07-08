using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using JeekRemoteManager.ViewModels;

namespace JeekRemoteManager.Views;

/// <summary>
/// FinalShell-style server monitor panel. All state binds to
/// <see cref="ServerMonitorViewModel"/>; the only code here draws the network
/// sparkline, which needs the chart's pixel bounds.
/// </summary>
public partial class ServerMonitorView : UserControl
{
    /// <summary>How many samples the sparkline window holds; matches the view model's history length.</summary>
    private const int SparklineWindow = 60;

    private ServerMonitorViewModel? _viewModel;

    /// <summary>Raised by the panel's own close button; the hosting TerminalView
    /// collapses the panel (which also stops polling).</summary>
    public event EventHandler? CloseRequested;

    public ServerMonitorView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => AttachViewModel();
        NetChart.SizeChanged += (_, _) => RedrawSparkline();
    }

    private void AttachViewModel()
    {
        if (_viewModel is not null)
            _viewModel.NetHistoryChanged -= RedrawSparkline;

        _viewModel = DataContext as ServerMonitorViewModel;
        if (_viewModel is not null)
            _viewModel.NetHistoryChanged += RedrawSparkline;

        RedrawSparkline();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) =>
        CloseRequested?.Invoke(this, EventArgs.Empty);

    private void RedrawSparkline()
    {
        var width = NetChart.Bounds.Width;
        var height = NetChart.Bounds.Height;
        if (_viewModel is null || width < 10 || height < 10)
        {
            UploadLine.Points = new List<Point>();
            DownloadLine.Points = new List<Point>();
            return;
        }

        var (upload, download) = _viewModel.GetNetHistory();
        // One shared Y scale so the two lines are comparable at a glance.
        var max = Math.Max(1.0, Math.Max(
            upload.DefaultIfEmpty(0).Max(),
            download.DefaultIfEmpty(0).Max()));

        UploadLine.Points = BuildPoints(upload, width, height, max);
        DownloadLine.Points = BuildPoints(download, width, height, max);
    }

    /// <summary>Maps samples into chart coordinates, right-aligned so new samples
    /// slide in from the right edge like a scope trace.</summary>
    private static List<Point> BuildPoints(IReadOnlyList<double> samples, double width, double height, double max)
    {
        var points = new List<Point>(samples.Count);
        if (samples.Count < 2)
            return points;

        var step = width / (SparklineWindow - 1);
        var left = width - (samples.Count - 1) * step;
        for (var i = 0; i < samples.Count; i++)
        {
            var x = left + i * step;
            var y = height - 2 - (height - 4) * (samples[i] / max);
            points.Add(new Point(x, y));
        }

        return points;
    }
}
