using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using JeekTools;
using Microsoft.Extensions.Logging;
using Renci.SshNet;
using Renci.SshNet.Common;
using ZLogger;

namespace JeekRemoteManager.Services;

/// <summary>One top process row: RSS/CPU are null on busybox ps (no columns).</summary>
public sealed record ServerMonitorProcess(long? MemBytes, double? CpuPercent, string Command);

/// <summary>One real filesystem row from `df -P -k`.</summary>
public sealed record ServerMonitorDisk(string MountPoint, long TotalBytes, long AvailableBytes)
{
    public double UsedPercent => TotalBytes <= 0
        ? 0
        : Math.Clamp((TotalBytes - AvailableBytes) * 100.0 / TotalBytes, 0, 100);
}

/// <summary>Current network throughput of the primary interface, bytes/second.</summary>
public sealed record ServerMonitorNetRates(string Interface, double UploadBytesPerSecond, double DownloadBytesPerSecond);

/// <summary>
/// One sample of remote server stats. Sections a tick didn't collect (heavy
/// sections on light ticks) or that failed to parse are null — the panel keeps
/// showing the previous value or a dash, never a panel-wide error.
/// </summary>
public sealed record ServerMonitorSnapshot(
    double? CpuPercent,
    long? MemTotalBytes,
    long? MemUsedBytes,
    long? SwapTotalBytes,
    long? SwapUsedBytes,
    string? LoadAverage,
    double? UptimeSeconds,
    ServerMonitorNetRates? NetRates,
    IReadOnlyList<ServerMonitorProcess>? Processes,
    IReadOnlyList<ServerMonitorDisk>? Disks,
    string? RemoteIp,
    double? LatencyMs);

/// <summary>
/// Polls a connected SSH server for live stats (CPU, memory, network, disks,
/// processes) through one persistent hidden shell on the terminal's shared SSH
/// transport. The shell follows the duplicated-session login-command sequence,
/// so bastion menus reach the same target as a duplicated terminal tab. Prefers
/// /proc over tool output so parsing is locale- and busybox-proof; every section
/// is marked with a sentinel line so a missing tool degrades only its own block.
/// </summary>
public sealed class ServerMonitorSession : IDisposable
{
    private static readonly ILogger Log = LogManager.CreateLogger(nameof(ServerMonitorSession));

    private const int LightIntervalSeconds = 2;
    private const int HeavyEveryNTicks = 5;
    private const int FailedProbeIntervalSeconds = 15;
    private const int MaxConsecutiveFailures = 3;
    private const int SampleTimeoutSeconds = 10;
    private const int LoginQuietMilliseconds = 500;
    private const int LoginStepTimeoutSeconds = 30;
    private const int TopProcessCount = 8;

    private const string SectionMarker = "@JRM@";

    // PTY-backed bastion shells can inject colors or terminal-control sequences into
    // otherwise plain command output. Remove them before any monitor field is parsed.
    private static readonly Regex AnsiAndControlChars = new(
        "\u001b\\[[0-9;?]*[ -/]*[@-~]" +
        "|\u001b\\][\\s\\S]*?(?:\u0007|\u001b\\\\)" +
        "|\u001b[@-_]" +
        "|[\u0000-\u0008\u000b\u000c\u000e-\u001f\u007f]",
        RegexOptions.Compiled);

    // Light sections sample fast-moving counters every tick.
    private const string LightCommand =
        "echo @JRM@cpu; head -n 1 /proc/stat 2>/dev/null; " +
        "echo @JRM@mem; cat /proc/meminfo 2>/dev/null; " +
        "echo @JRM@load; cat /proc/loadavg 2>/dev/null; " +
        "echo @JRM@up; cat /proc/uptime 2>/dev/null; " +
        "echo @JRM@net; cat /proc/net/dev 2>/dev/null";

    // Heavy sections (every Nth tick): process list, mounts, addresses.
    private const string HeavyCommand = LightCommand + "; " +
        "echo @JRM@df; LC_ALL=C df -P -k 2>/dev/null; " +
        "echo @JRM@ps; LC_ALL=C ps -eo rss,pcpu,comm 2>/dev/null || LC_ALL=C ps 2>/dev/null; " +
        "echo @JRM@ip; ip -o -4 addr show scope global 2>/dev/null || hostname -i 2>/dev/null; " +
        "echo @JRM@route; ip -o route show to default 2>/dev/null";

    private readonly Func<SharedSshClient?> _acquireClient;
    private readonly string _terminalType;
    private readonly string _loginCommands;
    private readonly Action<ServerMonitorSnapshot> _onSnapshot;
    private readonly Action _onWaiting;
    private readonly Action _onFailed;

    private readonly object _gate = new();
    private CancellationTokenSource? _cancellation;
    private SharedSshClient? _held;
    private ShellStream? _shell;
    private InteractiveShellPayloadMonitor? _activePayloadMonitor;
    private long _lastShellDataTicks;
    private Exception? _shellFailure;
    private long _sampleCount;
    private long _shellGeneration;

    // Delta state between ticks (background thread only).
    private (long Total, long Idle)? _prevCpu;
    private (long Rx, long Tx)? _prevNet;
    private Stopwatch? _prevNetClock;
    private string? _primaryInterface;
    private double? _latencyEmaMs;

    /// <param name="acquireClient">Takes a counted reference on the terminal's live
    /// shared SSH client, or returns null when nothing usable is connected.</param>
    /// <param name="onSnapshot">Called on a background thread with each new sample.</param>
    /// <param name="onWaiting">Called when there is no connection to sample yet.</param>
    /// <param name="onFailed">Called after repeated sampling failures; the session
    /// keeps probing at a slow interval and recovers on the next success.</param>
    public ServerMonitorSession(
        Func<SharedSshClient?> acquireClient,
        string terminalType,
        string loginCommands,
        Action<ServerMonitorSnapshot> onSnapshot,
        Action onWaiting,
        Action onFailed)
    {
        _acquireClient = acquireClient;
        _terminalType = string.IsNullOrWhiteSpace(terminalType)
            ? Models.Connection.DefaultTerminalType
            : terminalType.Trim();
        _loginCommands = loginCommands;
        _onSnapshot = onSnapshot;
        _onWaiting = onWaiting;
        _onFailed = onFailed;
    }

    /// <summary>Debug-MCP-visible runtime state used to verify that monitoring is
    /// using one persistent duplicated shell rather than repeated exec channels.</summary>
    public string ChannelMode => "PersistentDuplicatedShell";
    public bool IsShellReady => _shell is not null && Volatile.Read(ref _shellFailure) is null;
    public long SampleCount => Interlocked.Read(ref _sampleCount);
    public long ShellGeneration => Interlocked.Read(ref _shellGeneration);

    public void Start()
    {
        CancellationTokenSource cancellation;
        lock (_gate)
        {
            if (_cancellation is not null)
                return;
            cancellation = _cancellation = new CancellationTokenSource();
        }

        _ = Task.Run(() => RunAsync(cancellation), CancellationToken.None);
    }

    /// <summary>Stops polling. The loop task releases the client reference and
    /// disposes the token source as it winds down.</summary>
    public void Stop()
    {
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            cancellation = _cancellation;
            _cancellation = null;
        }

        try { cancellation?.Cancel(); } catch { /* already winding down */ }
    }

    public void Dispose() => Stop();

    private async Task RunAsync(CancellationTokenSource cancellation)
    {
        var cancellationToken = cancellation.Token;
        var tick = 0;
        var failures = 0;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var delaySeconds = LightIntervalSeconds;
                try
                {
                    var client = AcquireClient();
                    if (client is null)
                    {
                        ResetDeltas();
                        _onWaiting();
                    }
                    else
                    {
                        var heavy = tick % HeavyEveryNTicks == 0;
                        await EnsureShellAsync(client, cancellationToken).ConfigureAwait(false);
                        var snapshot = await SampleAsync(heavy, cancellationToken).ConfigureAwait(false);
                        tick++;
                        failures = 0;
                        Interlocked.Increment(ref _sampleCount);
                        _onSnapshot(snapshot);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    failures++;
                    ReleaseClient();
                    ResetDeltas();
                    if (failures == MaxConsecutiveFailures)
                        Log.ZLogWarning(ex, $"Server monitor sampling failed {failures} times, backing off");
                    if (failures >= MaxConsecutiveFailures)
                    {
                        _onFailed();
                        delaySeconds = FailedProbeIntervalSeconds;
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }
        finally
        {
            ReleaseClient();
            cancellation.Dispose();
        }
    }

    /// <summary>Returns the held live client, or re-acquires one (dropping a stale
    /// reference first, e.g. after the terminal reconnected on a fresh transport).</summary>
    private SharedSshClient? AcquireClient()
    {
        if (_held is { IsConnected: true })
            return _held;

        ReleaseClient();
        _held = _acquireClient();
        return _held;
    }

    private void ReleaseClient()
    {
        ReleaseShell();
        _held?.Release();
        _held = null;
    }

    private async Task EnsureShellAsync(SharedSshClient client, CancellationToken cancellationToken)
    {
        if (_shell is not null && Volatile.Read(ref _shellFailure) is null)
            return;

        ReleaseShell();
        var shell = await Task.Run(
            () => client.Client.CreateShellStream(_terminalType, 120, 30, 0, 0, 4096),
            cancellationToken).ConfigureAwait(false);

        _shell = shell;
        Interlocked.Increment(ref _shellGeneration);
        _shellFailure = null;
        Interlocked.Exchange(ref _lastShellDataTicks, 0);
        shell.DataReceived += OnShellData;
        shell.ErrorOccurred += OnShellError;
        shell.Closed += OnShellClosed;

        try
        {
            await RunDuplicatedLoginCommandsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            ReleaseShell();
            throw;
        }
    }

    private async Task RunDuplicatedLoginCommandsAsync(CancellationToken cancellationToken)
    {
        var lines = LoginCommandSequence.Select(_loginCommands, isDuplicatedSession: true);
        long mustBeAfterTicks = 0;

        // Wait for the initial prompt/menu before typing the first selection.
        if (lines.Length > 0)
            await WaitForShellQuietAsync(mustBeAfterTicks, cancellationToken).ConfigureAwait(false);

        foreach (var line in lines)
        {
            if (LoginCommandSequence.IsManualInputDirective(line))
                throw new InvalidOperationException(
                    "The server monitor cannot complete a duplicated login sequence containing #input.");

            ThrowIfShellFailed();
            mustBeAfterTicks = Environment.TickCount64;
            WriteToShell(line + "\r");
            await WaitForShellQuietAsync(mustBeAfterTicks, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task WaitForShellQuietAsync(long mustBeAfterTicks, CancellationToken cancellationToken)
    {
        var startedAt = Environment.TickCount64;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfShellFailed();

            var lastData = Interlocked.Read(ref _lastShellDataTicks);
            var now = Environment.TickCount64;
            if (lastData != 0
                && lastData >= mustBeAfterTicks
                && now - lastData >= LoginQuietMilliseconds)
                return;
            if (now - startedAt >= LoginStepTimeoutSeconds * 1000L)
                return;

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
    }

    private void OnShellData(object? sender, ShellDataEventArgs e)
    {
        Interlocked.Exchange(ref _lastShellDataTicks, Environment.TickCount64);
        Volatile.Read(ref _activePayloadMonitor)?.Append(e.Data);
    }

    private void OnShellError(object? sender, ExceptionEventArgs e) => FailShell(e.Exception);

    private void OnShellClosed(object? sender, EventArgs e) =>
        FailShell(new InvalidOperationException("Server monitor shell closed."));

    private void FailShell(Exception exception)
    {
        Volatile.Write(ref _shellFailure, exception);
        Volatile.Read(ref _activePayloadMonitor)?.Fail(exception);
    }

    private void ThrowIfShellFailed()
    {
        if (Volatile.Read(ref _shellFailure) is { } failure)
            throw new InvalidOperationException("Server monitor shell is unavailable.", failure);
    }

    private void WriteToShell(string text)
    {
        ThrowIfShellFailed();
        var shell = _shell ?? throw new InvalidOperationException("Server monitor shell is not open.");
        var bytes = Encoding.UTF8.GetBytes(text);
        shell.Write(bytes, 0, bytes.Length);
        shell.Flush();
    }

    private void ReleaseShell()
    {
        var shell = _shell;
        _shell = null;
        var monitor = Interlocked.Exchange(ref _activePayloadMonitor, null);
        monitor?.Fail(new OperationCanceledException("Server monitor shell was closed."));
        if (shell is null)
            return;

        shell.DataReceived -= OnShellData;
        shell.ErrorOccurred -= OnShellError;
        shell.Closed -= OnShellClosed;
        try { shell.Dispose(); } catch { /* already broken */ }
        _shellFailure = null;
    }

    private void ResetDeltas()
    {
        _prevCpu = null;
        _prevNet = null;
        _prevNetClock = null;
    }

    private async Task<ServerMonitorSnapshot> SampleAsync(
        bool heavy,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(SampleTimeoutSeconds));

        var payload = InteractiveShellPayloadRunner.Build(heavy ? HeavyCommand : LightCommand);
        var monitor = new InteractiveShellPayloadMonitor(payload);
        if (Interlocked.CompareExchange(ref _activePayloadMonitor, monitor, null) is not null)
            throw new InvalidOperationException("A server monitor sample is already running.");

        var clock = Stopwatch.StartNew();
        InteractiveShellPayloadResult result;
        try
        {
            result = await InteractiveShellPayloadRunner.RunAsync(
                payload,
                monitor,
                WriteToShell,
                timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            try { WriteToShell(InteractiveShellPayloadRunner.RestoreEchoCommand); } catch { /* broken shell */ }
            throw;
        }
        finally
        {
            Interlocked.CompareExchange(ref _activePayloadMonitor, null, monitor);
        }
        clock.Stop();

        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Monitor command exited with code {result.ExitCode}.");

        var sections = SplitSections(result.Output);
        if (sections.Count == 0)
            throw new InvalidOperationException("Monitor command produced no recognizable output.");

        // Only light ticks feed the latency estimate, so ps/df cost never pollutes it.
        if (!heavy || _latencyEmaMs is null)
        {
            var rtt = clock.Elapsed.TotalMilliseconds;
            _latencyEmaMs = _latencyEmaMs is { } ema ? 0.5 * ema + 0.5 * rtt : rtt;
        }

        var cpuPercent = ComputeCpuPercent(sections.GetValueOrDefault("cpu"));
        var (memTotal, memUsed, swapTotal, swapUsed) = ParseMemInfo(sections.GetValueOrDefault("mem"));
        var load = ParseLoadAverage(sections.GetValueOrDefault("load"));
        var uptime = ParseUptimeSeconds(sections.GetValueOrDefault("up"));

        if (heavy)
            _primaryInterface = ParseDefaultRouteInterface(sections.GetValueOrDefault("route")) ?? _primaryInterface;
        var netRates = ComputeNetRates(sections.GetValueOrDefault("net"));

        return new ServerMonitorSnapshot(
            cpuPercent,
            memTotal, memUsed, swapTotal, swapUsed,
            load,
            uptime,
            netRates,
            heavy ? ParseProcesses(sections.GetValueOrDefault("ps")) : null,
            heavy ? ParseDisks(sections.GetValueOrDefault("df")) : null,
            heavy ? ParseRemoteIp(sections.GetValueOrDefault("ip")) : null,
            _latencyEmaMs);
    }

    /// <summary>Splits command output into marker-delimited sections. Lines before
    /// the first marker (login banners, motd fragments) are ignored.</summary>
    internal static Dictionary<string, List<string>> SplitSections(string output)
    {
        output = AnsiAndControlChars.Replace(output, string.Empty);
        var sections = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        List<string>? current = null;

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.StartsWith(SectionMarker, StringComparison.Ordinal))
            {
                var name = line[SectionMarker.Length..].Trim();
                current = new List<string>();
                sections[name] = current;
            }
            else if (current is not null && line.Length > 0)
            {
                current.Add(line);
            }
        }

        return sections;
    }

    private double? ComputeCpuPercent(List<string>? lines)
    {
        var parsed = ParseCpuCounters(lines);
        if (parsed is not { } cpu)
        {
            _prevCpu = null;
            return null;
        }

        var prev = _prevCpu;
        _prevCpu = cpu;
        if (prev is not { } last || cpu.Total <= last.Total)
            return null;

        var deltaTotal = cpu.Total - last.Total;
        var deltaIdle = Math.Max(0, cpu.Idle - last.Idle);
        return Math.Clamp((deltaTotal - deltaIdle) * 100.0 / deltaTotal, 0, 100);
    }

    /// <summary>Parses the aggregate cpu line of /proc/stat into (total, idle+iowait) jiffies.</summary>
    internal static (long Total, long Idle)? ParseCpuCounters(List<string>? lines)
    {
        var line = lines?.FirstOrDefault(l => l.StartsWith("cpu ", StringComparison.Ordinal));
        if (line is null)
            return null;

        var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 5)
            return null;

        long total = 0;
        long idle = 0;
        for (var i = 1; i < fields.Length; i++)
        {
            if (!long.TryParse(fields[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                return null;
            total += value;
            // Field 4 = idle, field 5 = iowait; both count as not-busy.
            if (i is 4 or 5)
                idle += value;
        }

        return (total, idle);
    }

    /// <summary>Parses /proc/meminfo into used/total bytes for memory and swap.
    /// MemAvailable is the kernel's own "usable without swapping" estimate; ancient
    /// kernels without it fall back to free + buffers + cached.</summary>
    internal static (long? MemTotal, long? MemUsed, long? SwapTotal, long? SwapUsed) ParseMemInfo(List<string>? lines)
    {
        if (lines is null || lines.Count == 0)
            return (null, null, null, null);

        var values = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var colon = line.IndexOf(':');
            if (colon <= 0)
                continue;
            var key = line[..colon];
            var rest = line[(colon + 1)..].Trim();
            var space = rest.IndexOf(' ');
            if (space > 0)
                rest = rest[..space];
            if (long.TryParse(rest, NumberStyles.Integer, CultureInfo.InvariantCulture, out var kb))
                values[key] = kb * 1024;
        }

        if (!values.TryGetValue("MemTotal", out var memTotal) || memTotal <= 0)
            return (null, null, null, null);

        long available;
        if (values.TryGetValue("MemAvailable", out var memAvailable))
            available = memAvailable;
        else
            available = values.GetValueOrDefault("MemFree")
                + values.GetValueOrDefault("Buffers")
                + values.GetValueOrDefault("Cached");

        long? swapTotal = null;
        long? swapUsed = null;
        if (values.TryGetValue("SwapTotal", out var st))
        {
            swapTotal = st;
            swapUsed = Math.Max(0, st - values.GetValueOrDefault("SwapFree"));
        }

        return (memTotal, Math.Clamp(memTotal - available, 0, memTotal), swapTotal, swapUsed);
    }

    internal static string? ParseLoadAverage(List<string>? lines)
    {
        var fields = lines?.FirstOrDefault()?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields is { Length: >= 3 } ? $"{fields[0]} {fields[1]} {fields[2]}" : null;
    }

    internal static double? ParseUptimeSeconds(List<string>? lines)
    {
        var first = lines?.FirstOrDefault()?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            ? seconds
            : null;
    }

    private ServerMonitorNetRates? ComputeNetRates(List<string>? lines)
    {
        var counters = ParseNetCounters(lines);
        if (counters.Count == 0)
        {
            _prevNet = null;
            _prevNetClock = null;
            return null;
        }

        // Prefer the default-route interface; otherwise the busiest non-loopback one.
        var iface = _primaryInterface is { } primary && counters.ContainsKey(primary)
            ? primary
            : counters.OrderByDescending(pair => pair.Value.Rx + pair.Value.Tx).First().Key;

        var current = counters[iface];
        var prev = _prevNet;
        var elapsed = _prevNetClock?.Elapsed.TotalSeconds;
        _prevNet = current;
        _prevNetClock = Stopwatch.StartNew();

        // First sample, interface switch, or counter reset — no rate yet.
        if (prev is not { } last || elapsed is not > 0.2 || current.Rx < last.Rx || current.Tx < last.Tx)
            return null;

        return new ServerMonitorNetRates(
            iface,
            (current.Tx - last.Tx) / elapsed.Value,
            (current.Rx - last.Rx) / elapsed.Value);
    }

    /// <summary>Parses /proc/net/dev into per-interface cumulative (rx, tx) byte counters,
    /// skipping the loopback device.</summary>
    internal static Dictionary<string, (long Rx, long Tx)> ParseNetCounters(List<string>? lines)
    {
        var counters = new Dictionary<string, (long Rx, long Tx)>(StringComparer.Ordinal);
        if (lines is null)
            return counters;

        foreach (var line in lines)
        {
            var colon = line.IndexOf(':');
            if (colon <= 0)
                continue;

            var name = line[..colon].Trim();
            if (name.Length == 0 || name == "lo")
                continue;

            var fields = line[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // Receive bytes is field 0, transmit bytes is field 8.
            if (fields.Length >= 9
                && long.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var rx)
                && long.TryParse(fields[8], NumberStyles.Integer, CultureInfo.InvariantCulture, out var tx))
            {
                counters[name] = (rx, tx);
            }
        }

        return counters;
    }

    /// <summary>Extracts the interface name after "dev" from `ip -o route show to default`.</summary>
    internal static string? ParseDefaultRouteInterface(List<string>? lines)
    {
        var fields = lines?.FirstOrDefault()?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields is null)
            return null;

        for (var i = 0; i < fields.Length - 1; i++)
        {
            if (fields[i] == "dev")
                return fields[i + 1];
        }

        return null;
    }

    /// <summary>Parses `ps -eo rss,pcpu,comm` (or busybox `ps` as a degraded fallback:
    /// command only) into the top rows by memory.</summary>
    internal static IReadOnlyList<ServerMonitorProcess>? ParseProcesses(List<string>? lines)
    {
        if (lines is null || lines.Count == 0)
            return null;

        var rows = new List<ServerMonitorProcess>();
        var procpsFormat = lines[0].Contains("RSS", StringComparison.Ordinal);

        foreach (var line in lines.Skip(1))
        {
            if (procpsFormat)
            {
                var fields = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (fields.Length < 3
                    || !long.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var rssKb)
                    || !double.TryParse(fields[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var cpu))
                    continue;
                rows.Add(new ServerMonitorProcess(rssKb * 1024, cpu, fields[2]));
            }
            else
            {
                // busybox ps: "PID USER TIME COMMAND" — keep the command, no sizes.
                var fields = line.Split(' ', 4, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (fields.Length == 4 && long.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                    rows.Add(new ServerMonitorProcess(null, null, fields[3]));
            }
        }

        if (rows.Count == 0)
            return null;

        return procpsFormat
            ? rows.OrderByDescending(p => p.MemBytes ?? 0).Take(TopProcessCount).ToList()
            : rows.Take(TopProcessCount).ToList();
    }

    /// <summary>Parses `df -P -k`, keeping real block devices (filesystem starts with /)
    /// and deduplicating by mount point.</summary>
    internal static IReadOnlyList<ServerMonitorDisk>? ParseDisks(List<string>? lines)
    {
        if (lines is null || lines.Count == 0)
            return null;

        var byMount = new Dictionary<string, ServerMonitorDisk>(StringComparer.Ordinal);
        foreach (var line in lines.Skip(1))
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // Filesystem 1024-blocks Used Available Capacity Mounted-on
            if (fields.Length < 6
                || !fields[0].StartsWith('/')
                || !long.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var totalKb)
                || !long.TryParse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var availKb)
                || totalKb <= 0)
                continue;

            var mount = string.Join(' ', fields.Skip(5));
            byMount[mount] = new ServerMonitorDisk(mount, totalKb * 1024, availKb * 1024);
        }

        return byMount.Count == 0
            ? null
            : byMount.Values.OrderBy(d => d.MountPoint, StringComparer.Ordinal).ToList();
    }

    /// <summary>First global IPv4 address from `ip -o -4 addr` (or `hostname -i` output).</summary>
    internal static string? ParseRemoteIp(List<string>? lines)
    {
        var first = lines?.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(first))
            return null;

        var fields = first.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < fields.Length - 1; i++)
        {
            if (fields[i] == "inet")
            {
                var address = fields[i + 1];
                var slash = address.IndexOf('/');
                return slash > 0 ? address[..slash] : address;
            }
        }

        // hostname -i fallback: plain address(es).
        return fields.FirstOrDefault(f => f.Contains('.'));
    }
}
