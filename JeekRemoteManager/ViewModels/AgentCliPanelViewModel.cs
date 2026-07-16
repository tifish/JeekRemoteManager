using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JeekRemoteManager.Services;

namespace JeekRemoteManager.ViewModels;

/// <summary>
/// Drives the AI side panel after the headless-chat rewrite: pick a local agent CLI,
/// host it in-app (ConPTY) or open it in Windows Terminal, and keep the per-tab MCP
/// endpoint available for remote tools.
/// </summary>
public sealed partial class AgentCliPanelViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly string _workingDirectory;
    private readonly Func<AgentRemoteMcpServer?> _getMcpServer;
    private readonly Action<bool>? _onHideSshTerminalChanged;
    private readonly Action<bool, bool>? _onSafetyOptionsChanged;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private ConPtySession? _session;
    private Process? _externalProcess;
    private bool _disposed;
    /// <summary>Bumped on every start/stop request so superseded launches dispose their process.</summary>
    private int _startGeneration;

    /// <summary>Optional callback from the view: current terminal viewport in character cells.</summary>
    public Func<(int Cols, int Rows)>? GetViewportSize { get; set; }

    /// <summary>
    /// Optional hook run immediately before each CLI start (refresh CLAUDE.md / AGENTS.md).
    /// </summary>
    public Action? PrepareWorkspace { get; set; }

    /// <summary>Absolute local workspace for this connection (%LOCALAPPDATA%\JeekRemoteManager\AgentWorkspaces\...).</summary>
    public string WorkingDirectory => _workingDirectory;

    public AgentCliPanelViewModel(
        string workingDirectory,
        Func<AgentRemoteMcpServer?> getMcpServer,
        string? preferredProviderLabel = null,
        bool autoRun = true,
        bool autoApproveDangerousCommands = false,
        Action<bool, bool>? onSafetyOptionsChanged = null,
        Action<bool>? onHideSshTerminalChanged = null)
    {
        _workingDirectory = workingDirectory;
        _getMcpServer = getMcpServer;
        _onHideSshTerminalChanged = onHideSshTerminalChanged;
        _onSafetyOptionsChanged = onSafetyOptionsChanged;
        _autoRun = autoRun;
        _autoApproveDangerousCommands = autoApproveDangerousCommands;
        Directory.CreateDirectory(_workingDirectory);

        foreach (var descriptor in AgentCliCatalog.Discover())
            Providers.Add(descriptor);

        _selectedProvider = Providers.FirstOrDefault(p =>
                p.Label.Equals(preferredProviderLabel, StringComparison.OrdinalIgnoreCase) && p.IsAvailable)
            ?? Providers.FirstOrDefault(p => p.IsAvailable)
            ?? Providers[0];
        RefreshStatusFromProvider();
    }

    public ObservableCollection<AgentCliDescriptor> Providers { get; } = [];

    [ObservableProperty]
    private AgentCliDescriptor _selectedProvider;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private bool _useWindowsTerminal;

    [ObservableProperty]
    private bool _hideSshTerminal;

    [ObservableProperty]
    private bool _autoRun = true;

    [ObservableProperty]
    private bool _autoApproveDangerousCommands;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowInstallPrompt))]
    [NotifyPropertyChangedFor(nameof(ShowStartButton))]
    [NotifyPropertyChangedFor(nameof(ShowEmbeddedTerminal))]
    [NotifyPropertyChangedFor(nameof(ShowExternalHint))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    private bool _isInstalling;

    /// <summary>True when the selected CLI is missing and we should show the install panel.</summary>
    public bool ShowInstallPrompt =>
        !IsRunning && !SelectedProvider.IsAvailable;

    /// <summary>Start is only for installed, idle CLIs (install uses its own button).</summary>
    public bool ShowStartButton =>
        !IsRunning && !IsInstalling && SelectedProvider.IsAvailable;

    /// <summary>Embedded ConPTY surface (hidden while the install prompt is up).</summary>
    public bool ShowEmbeddedTerminal =>
        !ShowInstallPrompt && !UseWindowsTerminal;

    /// <summary>Placeholder when the CLI was launched in Windows Terminal.</summary>
    public bool ShowExternalHint =>
        !ShowInstallPrompt && UseWindowsTerminal;

    /// <summary>Raised when the embedded ConPTY session should be wired to a terminal control.</summary>
    public event Action<ConPtySession>? SessionStarted;

    /// <summary>
    /// Raised when the embedded session ends. <paramref name="replaced"/> is true when a new
    /// CLI is about to start (provider switch / restart) so the view should not show "session ended".
    /// </summary>
    public event Action<bool>? SessionStopped;

    partial void OnSelectedProviderChanged(AgentCliDescriptor value)
    {
        NotifyLayoutFlags();
        InstallCommand.NotifyCanExecuteChanged();
        RefreshStatusFromProvider();
        // Always launch the newly selected agent. Fire-and-forget is OK: StartAsync is
        // serialized and generation-gated so rapid ComboBox changes only keep the last one.
        if (!_disposed && !IsInstalling && value.IsAvailable)
            _ = StartAsync();
    }

    partial void OnIsRunningChanged(bool value)
    {
        NotifyLayoutFlags();
        InstallCommand.NotifyCanExecuteChanged();
    }

    partial void OnHideSshTerminalChanged(bool value) => _onHideSshTerminalChanged?.Invoke(value);

    partial void OnAutoRunChanged(bool value)
    {
        _onSafetyOptionsChanged?.Invoke(value, AutoApproveDangerousCommands);
        if (IsRunning)
            _ = RestartAsync();
    }

    partial void OnAutoApproveDangerousCommandsChanged(bool value) =>
        _onSafetyOptionsChanged?.Invoke(AutoRun, value);

    partial void OnUseWindowsTerminalChanged(bool value)
    {
        NotifyLayoutFlags();
        if (IsRunning)
            _ = RestartAsync();
    }

    private void NotifyLayoutFlags()
    {
        OnPropertyChanged(nameof(ShowInstallPrompt));
        OnPropertyChanged(nameof(ShowStartButton));
        OnPropertyChanged(nameof(ShowEmbeddedTerminal));
        OnPropertyChanged(nameof(ShowExternalHint));
    }

    private void RefreshStatusFromProvider()
    {
        if (IsInstalling)
            return;

        if (SelectedProvider.IsAvailable)
        {
            StatusText = IsRunning
                ? L("AiCliRunning", SelectedProvider.Label)
                : L("AiCliReady", SelectedProvider.Label);
            return;
        }

        // Languages.tab uses {2} for newlines so install command stays readable.
        StatusText = string.Format(
            L("AiCliNotInstalled"),
            SelectedProvider.Label,
            SelectedProvider.InstallHint,
            Environment.NewLine);
    }

    /// <summary>
    /// Starts the selected CLI if it is available and not already running.
    /// Used when the AI panel opens so the user does not need a manual Start click.
    /// </summary>
    public Task EnsureStartedAsync()
    {
        if (_disposed || IsInstalling)
            return Task.CompletedTask;
        // Already have a live session for the current selection — do not bounce it.
        if (IsRunning && (_session is not null || _externalProcess is not null))
            return Task.CompletedTask;
        if (!SelectedProvider.IsAvailable)
        {
            RefreshStatusFromProvider();
            return Task.CompletedTask;
        }

        return StartAsync();
    }

    /// <summary>True when <paramref name="session"/> is still the active embedded session.</summary>
    public bool IsCurrentSession(ConPtySession session) =>
        session is not null && ReferenceEquals(_session, session);

    /// <summary>
    /// Active embedded ConPTY, if any. Used by the view to re-attach after TabControl
    /// unloads/reloads the AI panel without stopping the CLI process.
    /// </summary>
    public ConPtySession? EmbeddedSession => _session;

    private bool CanInstall() =>
        !IsInstalling && !IsRunning && !SelectedProvider.IsAvailable;

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync()
    {
        if (_disposed || !CanInstall())
            return;

        IsInstalling = true;
        StatusText = string.Format(
            L("AiCliInstalling"),
            SelectedProvider.Label,
            SelectedProvider.InstallHint,
            Environment.NewLine);
        var kind = SelectedProvider.Kind;
        try
        {
            var progress = new Progress<string>(line =>
            {
                if (_disposed || !IsInstalling)
                    return;
                // Keep the last installer line visible without flooding the status bar.
                var trimmed = line.Trim();
                if (trimmed.Length == 0)
                    return;
                StatusText = trimmed.Length > 200 ? trimmed[..200] + "…" : trimmed;
            });

            var result = await AgentCliInstaller.InstallAsync(kind, progress).ConfigureAwait(true);
            RediscoverProviders(preferKind: kind);

            if (result.Success && SelectedProvider.IsAvailable)
            {
                StatusText = L("AiCliInstallSucceeded", SelectedProvider.Label);
                await StartAsync().ConfigureAwait(true);
            }
            else
            {
                StatusText = string.Format(
                    L("AiCliInstallFailed"),
                    SelectedProvider.Label,
                    result.Message,
                    Environment.NewLine);
            }
        }
        catch (Exception ex)
        {
            StatusText = string.Format(
                L("AiCliInstallFailed"),
                SelectedProvider.Label,
                ex.Message,
                Environment.NewLine);
            RediscoverProviders(preferKind: kind);
        }
        finally
        {
            IsInstalling = false;
            NotifyLayoutFlags();
            InstallCommand.NotifyCanExecuteChanged();
            if (SelectedProvider.IsAvailable && !IsRunning)
                RefreshStatusFromProvider();
        }
    }

    /// <summary>Re-probes PATH/install folders and refreshes the provider picker.</summary>
    private void RediscoverProviders(AgentCliKind preferKind)
    {
        var discovered = AgentCliCatalog.Discover();
        Providers.Clear();
        foreach (var d in discovered)
            Providers.Add(d);

        SelectedProvider = Providers.FirstOrDefault(p => p.Kind == preferKind)
            ?? Providers.FirstOrDefault(p => p.IsAvailable)
            ?? Providers[0];
        NotifyLayoutFlags();
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        if (_disposed)
            return;

        // Capture selection at request time; a later ComboBox change bumps generation.
        var generation = Interlocked.Increment(ref _startGeneration);
        var provider = SelectedProvider;

        await _startGate.WaitAsync().ConfigureAwait(true);
        try
        {
            if (_disposed || generation != Volatile.Read(ref _startGeneration))
                return;

            // Replaced=true: clear surface without the permanent "session ended" banner.
            await StopInternalAsync(userStopped: false, replaced: true).ConfigureAwait(true);

            if (_disposed || generation != Volatile.Read(ref _startGeneration))
                return;

            // Prefer the latest SelectedProvider if the user switched while we waited on the gate.
            provider = SelectedProvider;
            if (!provider.IsAvailable || provider.ExecutablePath is null)
            {
                StatusText = provider.InstallHint;
                NotifyLayoutFlags();
                return;
            }

            var mcp = _getMcpServer();
            var mcpUrl = mcp?.EndpointUrl;
            if (string.IsNullOrEmpty(mcpUrl))
            {
                StatusText = L("AiCliMcpUnavailable");
                return;
            }

            try
            {
                // Refresh connection-scoped CLAUDE.md / AGENTS.md before the CLI loads them.
                try { PrepareWorkspace?.Invoke(); }
                catch { /* best-effort; still launch the CLI */ }

                Directory.CreateDirectory(_workingDirectory);
                var exePath = provider.ExecutablePath;
                var args = AgentCliCatalog.BuildInteractiveArguments(
                    provider.Kind,
                    _workingDirectory,
                    mcpUrl,
                    AutoRun);

                if (UseWindowsTerminal && TryStartWindowsTerminal(exePath, args))
                {
                    if (_disposed || generation != Volatile.Read(ref _startGeneration))
                    {
                        // Superseded while wt was starting — tear down the external process.
                        await StopInternalAsync(userStopped: false, replaced: true).ConfigureAwait(true);
                        return;
                    }

                    IsRunning = true;
                    StatusText = L("AiCliRunningExternal", provider.Label);
                    NotifyLayoutFlags();
                    return;
                }

                var (cols, rows) = GetViewportSize?.Invoke() ?? (100, 30);
                cols = Math.Max(20, cols);
                rows = Math.Max(5, rows);
                var session = await Task.Run(() =>
                    ConPtySession.Start(
                        exePath,
                        args,
                        cols,
                        rows,
                        _workingDirectory)).ConfigureAwait(true);

                if (_disposed || generation != Volatile.Read(ref _startGeneration))
                {
                    try { session.Dispose(); } catch { /* ignore */ }
                    return;
                }

                _session = session;
                session.Exited += exitCode =>
                {
                    // CLI closed itself (/exit, crash, …) — not StopInternal dispose.
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        HandleCliProcessExited(session, external: null, exitCode));
                };

                IsRunning = true;
                StatusText = L("AiCliRunning", provider.Label);
                NotifyLayoutFlags();
                SessionStarted?.Invoke(session);
            }
            catch (Exception ex)
            {
                if (generation != Volatile.Read(ref _startGeneration))
                    return;
                StatusText = L("AiCliStartFailed", ex.Message);
                IsRunning = false;
                NotifyLayoutFlags();
            }
        }
        finally
        {
            _startGate.Release();
        }
    }

    [RelayCommand]
    private Task RestartAsync() => StartAsync();

    [RelayCommand]
    private async Task StopAsync()
    {
        // Invalidate any in-flight StartAsync so it does not revive the session after stop.
        Interlocked.Increment(ref _startGeneration);
        await _startGate.WaitAsync().ConfigureAwait(true);
        try
        {
            await StopInternalAsync(userStopped: true, replaced: false).ConfigureAwait(true);
            RefreshStatusFromProvider();
            NotifyLayoutFlags();
        }
        finally
        {
            _startGate.Release();
        }
    }

    /// <summary>
    /// CLI process ended on its own (or the external console closed). Clears session
    /// state, refreshes toolbar/status, and tells the view to update the terminal surface.
    /// </summary>
    private void HandleCliProcessExited(ConPtySession? session, Process? external, int? exitCode)
    {
        if (_disposed)
            return;

        // Ignore stale exit from a session we already replaced or stopped.
        if (session is not null && !ReferenceEquals(_session, session))
            return;
        if (external is not null && !ReferenceEquals(_externalProcess, external))
            return;

        if (session is not null)
        {
            _session = null;
            try { session.Dispose(); } catch { /* ignore */ }
        }

        if (external is not null)
        {
            _externalProcess = null;
            try { external.Dispose(); } catch { /* ignore */ }
        }

        if (!IsRunning && _session is null && _externalProcess is null)
            return;

        IsRunning = false;
        StatusText = exitCode is { } code
            ? L("AiCliExited", SelectedProvider.Label, code)
            : L("AiCliExitedNoCode", SelectedProvider.Label);
        NotifyLayoutFlags();
        InstallCommand.NotifyCanExecuteChanged();
        SessionStopped?.Invoke(false);
    }

    private Task StopInternalAsync(bool userStopped, bool replaced)
    {
        // Tell the view to unhook before we kill the process (avoids late feed races).
        // replaced=true during provider switch / restart so the UI does not flash "session ended".
        SessionStopped?.Invoke(replaced);

        var session = _session;
        _session = null;
        if (session is not null)
        {
            try { session.Dispose(); } catch { /* ignore */ }
        }

        var external = _externalProcess;
        _externalProcess = null;
        if (external is not null)
        {
            try
            {
                if (!external.HasExited)
                    external.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best-effort.
            }

            try { external.Dispose(); } catch { /* ignore */ }
        }

        IsRunning = false;
        if (userStopped)
            StatusText = L("AiCliStopped", SelectedProvider.Label);
        return Task.CompletedTask;
    }

    private bool TryStartWindowsTerminal(string exePath, IReadOnlyList<string> args)
    {
        var wt = FindWindowsTerminal();
        if (wt is null)
            return false;

        // wt.exe -d <dir> -- <exe> <args...>
        // Do not treat wt.exe lifetime as the CLI: Windows Terminal often returns
        // immediately after handing off to the real console process.
        var psi = new ProcessStartInfo
        {
            FileName = wt,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-d");
        psi.ArgumentList.Add(_workingDirectory);
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add(exePath);
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        var process = Process.Start(psi);
        if (process is null)
            return false;

        _externalProcess = process;
        return true;
    }

    private static string? FindWindowsTerminal()
    {
        var local = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps", "wt.exe");
        if (File.Exists(local))
            return local;

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), "wt.exe");
                if (File.Exists(candidate))
                    return candidate;
            }
            catch
            {
                // ignore bad PATH entries
            }
        }

        return null;
    }

    /// <summary>Resizes the active embedded ConPTY when the host terminal control changes size.</summary>
    public void ResizeSession(int cols, int rows) => _session?.Resize(cols, rows);

    /// <summary>Writes user keystrokes into the embedded ConPTY.</summary>
    public void WriteToSession(byte[] data) => _session?.Write(data);

    public bool HasEmbeddedSession => _session is not null;

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        Interlocked.Increment(ref _startGeneration);
        await _startGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopInternalAsync(userStopped: false, replaced: false).ConfigureAwait(false);
        }
        finally
        {
            _startGate.Release();
            _startGate.Dispose();
        }
    }
}
