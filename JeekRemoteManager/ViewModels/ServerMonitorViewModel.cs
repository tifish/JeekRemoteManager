using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JeekRemoteManager.Services;

namespace JeekRemoteManager.ViewModels;

/// <summary>One row of the top-processes table.</summary>
public sealed record ServerMonitorProcessRow(string MemText, string CpuText, string Command);

/// <summary>One row of the disks table.</summary>
public sealed record ServerMonitorDiskRow(string MountPoint, string SizeText, double UsedPercent);

/// <summary>
/// Presents live server stats sampled by a <see cref="ServerMonitorSession"/> over
/// a hidden duplicated shell on the terminal's SSH connection. Owns the session:
/// polling runs only between
/// <see cref="Start"/> and <see cref="Stop"/> (the panel's visibility).
/// </summary>
public sealed partial class ServerMonitorViewModel : ViewModelBase, IDisposable
{
    private const int NetHistoryLength = 60;

    private readonly ServerMonitorSession _session;
    private readonly List<double> _uploadHistory = new();
    private readonly List<double> _downloadHistory = new();

    public string HostLabel { get; }

    [ObservableProperty]
    private string _addressText;

    [ObservableProperty]
    private bool _hasData;

    [ObservableProperty]
    private bool _isWaiting = true;

    [ObservableProperty]
    private bool _isFailed;

    [ObservableProperty]
    private string _uptimeText = "—";

    [ObservableProperty]
    private string _latencyText = "—";

    [ObservableProperty]
    private string _loadText = "—";

    [ObservableProperty]
    private double _cpuPercent;

    [ObservableProperty]
    private string _cpuText = "—";

    [ObservableProperty]
    private double _memPercent;

    [ObservableProperty]
    private string _memText = "—";

    [ObservableProperty]
    private double _swapPercent;

    [ObservableProperty]
    private string _swapText = "—";

    [ObservableProperty]
    private string _uploadRateText = "—";

    [ObservableProperty]
    private string _downloadRateText = "—";

    public ObservableCollection<ServerMonitorProcessRow> Processes { get; } = new();

    public ObservableCollection<ServerMonitorDiskRow> Disks { get; } = new();

    /// <summary>Raised on the UI thread whenever the network rate history gains a
    /// sample; the view redraws its sparkline from <see cref="GetNetHistory"/>.</summary>
    public event Action? NetHistoryChanged;

    /// <param name="acquireClient">See <see cref="ServerMonitorSession"/>.</param>
    /// <param name="hostLabel">"user@host:port" caption for the panel header.</param>
    /// <param name="address">The configured host, shown until the server reports its own IP.</param>
    public ServerMonitorViewModel(
        Func<SharedSshClient?> acquireClient,
        string terminalType,
        string loginCommands,
        string hostLabel,
        string address)
    {
        HostLabel = hostLabel;
        _addressText = address;
        _session = new ServerMonitorSession(
            acquireClient,
            terminalType,
            loginCommands,
            snapshot => Dispatcher.UIThread.Post(() => ApplySnapshot(snapshot)),
            () => Dispatcher.UIThread.Post(OnWaiting),
            () => Dispatcher.UIThread.Post(OnFailed));
    }

    public void Start() => _session.Start();

    public void Stop() => _session.Stop();

    public void Dispose() => _session.Dispose();

    // Intentionally public so Debug MCP can verify the live channel strategy and
    // sampling progress without reaching through private implementation fields.
    public string MonitorChannelMode => _session.ChannelMode;
    public bool IsMonitorShellReady => _session.IsShellReady;
    public long MonitorSampleCount => _session.SampleCount;
    public long MonitorShellGeneration => _session.ShellGeneration;

    [RelayCommand]
    private void Retry()
    {
        IsFailed = false;
        IsWaiting = true;
        _session.Stop();
        _session.Start();
    }

    public (IReadOnlyList<double> Upload, IReadOnlyList<double> Download) GetNetHistory() =>
        (_uploadHistory, _downloadHistory);

    private void OnWaiting()
    {
        if (!HasData)
            IsWaiting = true;
    }

    private void OnFailed()
    {
        // The three states are exclusive in the view; drop stale data so the
        // failure message isn't drawn on top of it.
        HasData = false;
        IsWaiting = false;
        IsFailed = true;
    }

    private void ApplySnapshot(ServerMonitorSnapshot snapshot)
    {
        HasData = true;
        IsWaiting = false;
        IsFailed = false;

        if (snapshot.CpuPercent is { } cpu)
        {
            CpuPercent = cpu;
            CpuText = cpu.ToString("0", CultureInfo.InvariantCulture) + " %";
        }

        if (snapshot is { MemTotalBytes: { } memTotal, MemUsedBytes: { } memUsed } && memTotal > 0)
        {
            MemPercent = memUsed * 100.0 / memTotal;
            MemText = FileBrowserViewModel.FormatSize(memUsed) + " / " + FileBrowserViewModel.FormatSize(memTotal);
        }

        if (snapshot is { SwapTotalBytes: { } swapTotal, SwapUsedBytes: { } swapUsed })
        {
            if (swapTotal > 0)
            {
                SwapPercent = swapUsed * 100.0 / swapTotal;
                SwapText = FileBrowserViewModel.FormatSize(swapUsed) + " / " + FileBrowserViewModel.FormatSize(swapTotal);
            }
            else
            {
                SwapPercent = 0;
                SwapText = "0 / 0";
            }
        }

        if (snapshot.LoadAverage is { } load)
            LoadText = load;

        if (snapshot.UptimeSeconds is { } uptime)
            UptimeText = L("MonitorUptimeDays", (uptime / 86400).ToString("0.0", CultureInfo.InvariantCulture));

        if (snapshot.LatencyMs is { } latency)
            LatencyText = "~ " + latency.ToString("0", CultureInfo.InvariantCulture) + " ms";

        if (snapshot.RemoteIp is { } ip && ip.Length > 0)
            AddressText = ip;

        UpdateNetwork(snapshot.NetRates);

        if (snapshot.Processes is { } processes)
        {
            Processes.Clear();
            foreach (var process in processes)
            {
                Processes.Add(new ServerMonitorProcessRow(
                    process.MemBytes is { } mem ? FileBrowserViewModel.FormatSize(mem) : "—",
                    process.CpuPercent is { } processCpu
                        ? processCpu.ToString("0.0", CultureInfo.InvariantCulture)
                        : "—",
                    process.Command));
            }
        }

        if (snapshot.Disks is { } disks)
        {
            Disks.Clear();
            foreach (var disk in disks)
            {
                Disks.Add(new ServerMonitorDiskRow(
                    disk.MountPoint,
                    FileBrowserViewModel.FormatSize(disk.AvailableBytes)
                        + " / " + FileBrowserViewModel.FormatSize(disk.TotalBytes),
                    disk.UsedPercent));
            }
        }
    }

    private void UpdateNetwork(ServerMonitorNetRates? rates)
    {
        if (rates is null)
            return;

        UploadRateText = "↑ " + FileBrowserViewModel.FormatSize((long)rates.UploadBytesPerSecond) + "/s";
        DownloadRateText = "↓ " + FileBrowserViewModel.FormatSize((long)rates.DownloadBytesPerSecond) + "/s";

        _uploadHistory.Add(rates.UploadBytesPerSecond);
        _downloadHistory.Add(rates.DownloadBytesPerSecond);
        if (_uploadHistory.Count > NetHistoryLength)
            _uploadHistory.RemoveAt(0);
        if (_downloadHistory.Count > NetHistoryLength)
            _downloadHistory.RemoveAt(0);

        NetHistoryChanged?.Invoke();
    }
}
