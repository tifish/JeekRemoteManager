using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JeekRemoteManager.Models;
using JeekRemoteManager.Services;

namespace JeekRemoteManager.ViewModels;

/// <summary>One selectable AI panel launch mode (CLI / Windows Terminal / Desktop).</summary>
public sealed record AgentCliRunModeOption(AgentCliRunMode Mode, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// Drives the AI side panel after the headless-chat rewrite: pick a local agent,
/// host it in-app (ConPTY), open it in Windows Terminal, or launch Claude/Codex
/// desktop via registered protocol, and keep the per-tab MCP endpoint available.
/// </summary>
public sealed partial class AgentCliPanelViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly string _workingDirectory;
    private readonly Func<AgentRemoteMcpServer?> _getMcpServer;
    private readonly Action<bool>? _onHideSshTerminalChanged;
    private readonly Action<bool, bool>? _onSafetyOptionsChanged;
    private readonly Func<AgentCliKind, AgentCliRunMode>? _resolvePreferredRunMode;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private ConPtySession? _session;
    private Process? _externalProcess;
    private bool _desktopSessionActive;
    private bool _disposed;
    /// <summary>Bumped on every start/stop request so superseded launches dispose their process.</summary>
    private int _startGeneration;

    /// <summary>Optional callback from the view: current terminal viewport in character cells.</summary>
    public Func<(int Cols, int Rows)>? GetViewportSize { get; set; }

    /// <summary>
    /// Optional hook run immediately before each CLI start. Receives the live MCP endpoint
    /// URL so the workspace can refresh <c>AGENTS.md</c>/<c>CLAUDE.md</c> and project MCP
    /// configs (no server details on the command line).
    /// </summary>
    public Action<string>? PrepareWorkspace { get; set; }

    /// <summary>Absolute local workspace for this connection (%LOCALAPPDATA%\JeekRemoteManager\AgentWorkspaces\...).</summary>
    public string WorkingDirectory => _workingDirectory;

    public AgentCliPanelViewModel(
        string workingDirectory,
        Func<AgentRemoteMcpServer?> getMcpServer,
        string? preferredProviderLabel = null,
        bool autoRun = true,
        bool autoApproveDangerousCommands = false,
        bool hideSshTerminal = false,
        Action<bool, bool>? onSafetyOptionsChanged = null,
        Action<bool>? onHideSshTerminalChanged = null,
        AgentCliRunMode preferredRunMode = AgentCliRunMode.Cli,
        Func<AgentCliKind, AgentCliRunMode>? resolvePreferredRunMode = null)
    {
        _workingDirectory = workingDirectory;
        _getMcpServer = getMcpServer;
        _onHideSshTerminalChanged = onHideSshTerminalChanged;
        _onSafetyOptionsChanged = onSafetyOptionsChanged;
        _resolvePreferredRunMode = resolvePreferredRunMode;
        _autoRun = autoRun;
        _autoApproveDangerousCommands = autoApproveDangerousCommands;
        _hideSshTerminal = hideSshTerminal;
        Directory.CreateDirectory(_workingDirectory);

        foreach (var descriptor in AgentCliCatalog.Discover())
            Providers.Add(descriptor);

        _selectedProvider = Providers.FirstOrDefault(p =>
                p.Label.Equals(preferredProviderLabel, StringComparison.OrdinalIgnoreCase) && p.IsAvailable)
            ?? Providers.FirstOrDefault(p => p.IsAvailable)
            ?? Providers[0];

        SyncRunModeOptions(AgentCliCatalog.SupportsDesktop(_selectedProvider.Kind));

        // Prefer the slot for this provider (Grok vs Claude/Codex). Desktop is clamped away
        // for agents without a protocol so Grok keeps its own CLI/WT preference.
        var initialMode = preferredRunMode;
        if (initialMode == AgentCliRunMode.Desktop
            && !AgentCliCatalog.SupportsDesktop(_selectedProvider.Kind))
            initialMode = AgentCliRunMode.Cli;
        _selectedRunModeOption = RunModeOptions.FirstOrDefault(o => o.Mode == initialMode)
            ?? RunModeOptions[0];

        RefreshStatusFromProvider();
    }

    public ObservableCollection<AgentCliDescriptor> Providers { get; } = [];

    /// <summary>
    /// Launch modes for the current provider. Desktop is omitted for agents without a
    /// desktop protocol (currently Grok).
    /// </summary>
    public ObservableCollection<AgentCliRunModeOption> RunModeOptions { get; } = [];

    [ObservableProperty]
    private AgentCliDescriptor _selectedProvider;

    [ObservableProperty]
    private AgentCliRunModeOption _selectedRunModeOption;

    /// <summary>Current launch mode (CLI / Windows Terminal / Desktop).</summary>
    public AgentCliRunMode RunMode => SelectedRunModeOption?.Mode ?? AgentCliRunMode.Cli;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private bool _isRunning;

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

    /// <summary>True when the selected CLI is missing and we should show the install panel.
    /// Desktop mode does not require a local CLI binary (protocol launch).</summary>
    public bool ShowInstallPrompt =>
        !IsRunning
        && !IsInstalling
        && RunMode != AgentCliRunMode.Desktop
        && !SelectedProvider.IsAvailable;

    /// <summary>Start is only for installed, idle CLIs (or Desktop Claude/Codex).</summary>
    public bool ShowStartButton =>
        !IsRunning && !IsInstalling && CanStartSelectedProvider();

    /// <summary>Embedded ConPTY surface (CLI mode only; hidden while the install prompt is up).</summary>
    public bool ShowEmbeddedTerminal =>
        !ShowInstallPrompt && RunMode == AgentCliRunMode.Cli;

    /// <summary>Placeholder when the agent runs outside the side panel (WT or Desktop).</summary>
    public bool ShowExternalHint =>
        !ShowInstallPrompt && RunMode != AgentCliRunMode.Cli;

    /// <summary>Localized hint for the external (WT / Desktop) surface.</summary>
    public string ExternalHintText =>
        RunMode == AgentCliRunMode.Desktop
            ? L("AiCliDesktopHint")
            : L("AiCliExternalHint");

    /// <summary>Raised when the embedded ConPTY session should be wired to a terminal control.</summary>
    public event Action<ConPtySession>? SessionStarted;

    /// <summary>
    /// Raised when the embedded session ends. <paramref name="replaced"/> is true when a new
    /// CLI is about to start (provider switch / restart) so the view should not show "session ended".
    /// <paramref name="exitDetail"/> is plain-text CLI output (e.g. config errors) when the process
    /// died with a useful message; shown in the terminal and status bar.
    /// </summary>
    public event Action<bool, string?>? SessionStopped;

    partial void OnSelectedProviderChanged(AgentCliDescriptor value)
    {
        var supportsDesktop = AgentCliCatalog.SupportsDesktop(value.Kind);
        var preferred = _resolvePreferredRunMode?.Invoke(value.Kind) ?? RunMode;
        if (!supportsDesktop && preferred == AgentCliRunMode.Desktop)
            preferred = AgentCliRunMode.Cli;

        // Leaving Desktop for a non-desktop agent: change mode first so removing Desktop is safe.
        if (RunMode == AgentCliRunMode.Desktop && !supportsDesktop)
        {
            var interim = RunModeOptions.FirstOrDefault(o => o.Mode == preferred)
                ?? RunModeOptions.First(o => o.Mode == AgentCliRunMode.Cli);
            SelectedRunModeOption = interim;
            SyncRunModeOptions(includeDesktop: false);
            return; // StartAsync already requested via run-mode change.
        }

        // Rebuild options for the new provider, then restore that family's stored mode
        // (e.g. Grok CLI/WT → Claude Desktop).
        SyncRunModeOptions(supportsDesktop);

        var target = RunModeOptions.FirstOrDefault(o => o.Mode == preferred)
            ?? RunModeOptions[0];
        if (RunMode != target.Mode)
        {
            SelectedRunModeOption = target;
            return;
        }

        // Same mode: keep SelectedRunModeOption pointing at an item still in the list.
        if (RunModeOptions.FirstOrDefault(o => o.Mode == RunMode) is { } match
            && !ReferenceEquals(match, SelectedRunModeOption))
        {
            _selectedRunModeOption = match;
            OnPropertyChanged(nameof(SelectedRunModeOption));
        }

        NotifyLayoutFlags();
        InstallCommand.NotifyCanExecuteChanged();
        RefreshStatusFromProvider();
        // Always launch the newly selected agent. Fire-and-forget is OK: StartAsync is
        // serialized and generation-gated so rapid ComboBox changes only keep the last one.
        if (!_disposed && !IsInstalling && CanStartSelectedProvider())
            _ = StartAsync();
    }

    partial void OnSelectedRunModeOptionChanged(AgentCliRunModeOption value)
    {
        OnPropertyChanged(nameof(RunMode));
        NotifyLayoutFlags();
        InstallCommand.NotifyCanExecuteChanged();
        RefreshStatusFromProvider();
        if (!_disposed && !IsInstalling && CanStartSelectedProvider())
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

    private void NotifyLayoutFlags()
    {
        OnPropertyChanged(nameof(ShowInstallPrompt));
        OnPropertyChanged(nameof(ShowStartButton));
        OnPropertyChanged(nameof(ShowEmbeddedTerminal));
        OnPropertyChanged(nameof(ShowExternalHint));
        OnPropertyChanged(nameof(ExternalHintText));
    }

    /// <summary>
    /// Keeps the run-mode picker in sync: CLI + Windows Terminal always; Desktop only when the
    /// selected agent has a registered desktop protocol (Claude/Codex, not Grok).
    /// </summary>
    private void SyncRunModeOptions(bool includeDesktop)
    {
        if (RunModeOptions.Count == 0)
        {
            RunModeOptions.Add(new AgentCliRunModeOption(AgentCliRunMode.Cli, "CLI"));
            RunModeOptions.Add(new AgentCliRunModeOption(AgentCliRunMode.WindowsTerminal, "Windows Terminal"));
        }

        var desktop = RunModeOptions.FirstOrDefault(o => o.Mode == AgentCliRunMode.Desktop);
        if (includeDesktop && desktop is null)
            RunModeOptions.Add(new AgentCliRunModeOption(AgentCliRunMode.Desktop, "Desktop"));
        else if (!includeDesktop && desktop is not null)
            RunModeOptions.Remove(desktop);
    }

    private bool CanStartSelectedProvider() =>
        RunMode == AgentCliRunMode.Desktop
            ? AgentCliCatalog.SupportsDesktop(SelectedProvider.Kind)
            : SelectedProvider.IsAvailable;

    private void RefreshStatusFromProvider()
    {
        if (IsInstalling)
            return;

        if (RunMode == AgentCliRunMode.Desktop
            && !AgentCliCatalog.SupportsDesktop(SelectedProvider.Kind))
        {
            StatusText = L("AiCliDesktopUnsupported", SelectedProvider.Label);
            return;
        }

        if (RunMode == AgentCliRunMode.Desktop
            || SelectedProvider.IsAvailable)
        {
            StatusText = IsRunning
                ? RunMode switch
                {
                    AgentCliRunMode.WindowsTerminal => L("AiCliRunningExternal", SelectedProvider.Label),
                    AgentCliRunMode.Desktop => L("AiCliRunningDesktop", SelectedProvider.Label),
                    _ => L("AiCliRunning", SelectedProvider.Label),
                }
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
        if (IsRunning && (_session is not null || _externalProcess is not null || _desktopSessionActive))
            return Task.CompletedTask;
        if (!CanStartSelectedProvider())
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
        !IsInstalling
        && !IsRunning
        && RunMode != AgentCliRunMode.Desktop
        && !SelectedProvider.IsAvailable;

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
            if (CanStartSelectedProvider() && !IsRunning)
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
        var runMode = RunMode;

        await _startGate.WaitAsync().ConfigureAwait(true);
        try
        {
            if (_disposed || generation != Volatile.Read(ref _startGeneration))
                return;

            // Replaced=true: clear surface without the permanent "session ended" banner.
            await StopInternalAsync(userStopped: false, replaced: true).ConfigureAwait(true);

            if (_disposed || generation != Volatile.Read(ref _startGeneration))
                return;

            // Prefer the latest selection if the user switched while we waited on the gate.
            provider = SelectedProvider;
            runMode = RunMode;

            if (runMode == AgentCliRunMode.Desktop)
            {
                if (!AgentCliCatalog.SupportsDesktop(provider.Kind))
                {
                    StatusText = L("AiCliDesktopUnsupported", provider.Label);
                    NotifyLayoutFlags();
                    return;
                }
            }
            else if (!provider.IsAvailable || provider.ExecutablePath is null)
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
                // Refresh AGENTS.md / CLAUDE.md + project MCP configs with the live endpoint
                // before the CLI (or a desktop app opening this folder) loads them.
                try { PrepareWorkspace?.Invoke(mcpUrl); }
                catch { /* best-effort; still launch the agent */ }

                Directory.CreateDirectory(_workingDirectory);

                if (runMode == AgentCliRunMode.Desktop)
                {
                    if (!TryStartDesktopApp(provider.Kind))
                    {
                        if (generation != Volatile.Read(ref _startGeneration))
                            return;
                        StatusText = L("AiCliStartFailed", L("AiCliDesktopLaunchFailed", provider.Label));
                        IsRunning = false;
                        NotifyLayoutFlags();
                        return;
                    }

                    if (_disposed || generation != Volatile.Read(ref _startGeneration))
                    {
                        await StopInternalAsync(userStopped: false, replaced: true).ConfigureAwait(true);
                        return;
                    }

                    IsRunning = true;
                    StatusText = L("AiCliRunningDesktop", provider.Label);
                    NotifyLayoutFlags();
                    return;
                }

                var exePath = provider.ExecutablePath!;
                // Runtime flags only (auto-approve tools / scrollback). Server context is in AGENTS.md.
                var args = AgentCliCatalog.BuildInteractiveArguments(provider.Kind, AutoRun);

                if (runMode == AgentCliRunMode.WindowsTerminal)
                {
                    if (!TryStartWindowsTerminal(exePath, args))
                    {
                        if (generation != Volatile.Read(ref _startGeneration))
                            return;
                        StatusText = L("AiCliStartFailed", L("AiCliWindowsTerminalMissing"));
                        IsRunning = false;
                        NotifyLayoutFlags();
                        return;
                    }

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
                    // Brief delay so ConPTY can flush the last error lines (config load
                    // failures often print then exit within milliseconds).
                    _ = FinalizeEmbeddedSessionExitAsync(session, exitCode, generation);
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
                StatusText = L("AiCliStartFailed", FormatExceptionMessage(ex));
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
    /// Waits briefly for ConPTY to deliver final error lines, then finishes exit handling
    /// on the UI thread.
    /// </summary>
    private async Task FinalizeEmbeddedSessionExitAsync(
        ConPtySession session,
        int exitCode,
        int generation)
    {
        try
        {
            if (exitCode != 0)
                await Task.Delay(350).ConfigureAwait(false);
        }
        catch
        {
            // ignore
        }

        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_disposed)
                return;
            // A newer StartAsync superseded this launch — only dispose if still current.
            if (generation != Volatile.Read(ref _startGeneration)
                && !ReferenceEquals(_session, session))
            {
                try { session.Dispose(); } catch { /* ignore */ }
                return;
            }

            HandleCliProcessExited(session, external: null, exitCode);
        });
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

        // Capture before dispose — early-exit CLIs often die before the UI attaches DataReceived.
        var exitDetail = session is not null
            ? SummarizeCliOutput(session.GetRecentOutputPlainText())
            : null;

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

        if (!IsRunning && _session is null && _externalProcess is null && !_desktopSessionActive)
            return;

        IsRunning = false;
        StatusText = FormatProcessExitStatus(SelectedProvider.Label, exitCode, exitDetail);
        NotifyLayoutFlags();
        InstallCommand.NotifyCanExecuteChanged();
        SessionStopped?.Invoke(false, exitDetail);
    }

    private static string FormatProcessExitStatus(string label, int? exitCode, string? exitDetail)
    {
        if (!string.IsNullOrWhiteSpace(exitDetail))
        {
            return exitCode is { } code
                ? L("AiCliExitedWithDetail", label, code, exitDetail)
                : L("AiCliExitedWithDetailNoCode", label, exitDetail);
        }

        return exitCode is { } c
            ? L("AiCliExited", label, c)
            : L("AiCliExitedNoCode", label);
    }

    /// <summary>Collapse multi-line CLI output into a short status-bar detail.</summary>
    private static string? SummarizeCliOutput(string? plain)
    {
        if (string.IsNullOrWhiteSpace(plain))
            return null;

        var lines = plain
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static l => l.Length > 0 && !IsNoiseCliLine(l))
            .ToList();
        if (lines.Count == 0)
            return null;

        // Prefer the last non-noise lines (Codex/Claude print the real error last).
        var take = Math.Min(4, lines.Count);
        var summary = string.Join(" · ", lines.Skip(lines.Count - take));
        if (summary.Length > 360)
            summary = summary[..357] + "…";
        return summary;
    }

    private static bool IsNoiseCliLine(string line)
    {
        // ConPTY / shells often emit blank-ish or cursor-only lines; skip pure noise.
        if (line.All(static c => char.IsWhiteSpace(c) || c is '?' or '.'))
            return true;
        return false;
    }

    private static string FormatExceptionMessage(Exception ex)
    {
        var message = ex.Message?.Trim() ?? ex.GetType().Name;
        if (ex.InnerException is { } inner
            && !string.IsNullOrWhiteSpace(inner.Message)
            && !message.Contains(inner.Message, StringComparison.Ordinal))
        {
            message = $"{message} ({inner.Message.Trim()})";
        }

        if (message.Length > 400)
            message = message[..397] + "…";
        return message;
    }

    private Task StopInternalAsync(bool userStopped, bool replaced)
    {
        // Tell the view to unhook before we kill the process (avoids late feed races).
        // replaced=true during provider switch / restart so the UI does not flash "session ended".
        SessionStopped?.Invoke(replaced, null);

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

        // Desktop apps are not owned by this process; only clear our launch marker.
        _desktopSessionActive = false;

        IsRunning = false;
        if (userStopped)
            StatusText = L("AiCliStopped", SelectedProvider.Label);
        return Task.CompletedTask;
    }

    private bool TryStartDesktopApp(AgentCliKind kind)
    {
        var uri = AgentCliCatalog.BuildDesktopProtocolUri(kind, _workingDirectory);
        if (uri is null)
            return false;

        try
        {
            // Registered protocol (claude:// / codex://). ShellExecute hands off to the
            // desktop app; the returned process (if any) is not the agent and exits quickly.
            var psi = new ProcessStartInfo
            {
                FileName = uri,
                UseShellExecute = true,
            };
            Process.Start(psi);
            _desktopSessionActive = true;
            return true;
        }
        catch
        {
            _desktopSessionActive = false;
            return false;
        }
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
