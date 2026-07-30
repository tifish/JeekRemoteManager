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

/// <summary>One selectable AI panel launch mode (CLI / Windows Terminal / Desktop / IDE).</summary>
public sealed record AgentCliRunModeOption(AgentCliRunMode Mode, string Label)
{
    public override string ToString() => Label;
}

/// <summary>What an entry in the endpoint picker does when chosen.</summary>
public enum AgentEndpointAction
{
    /// <summary>Use the agent's own API, or one of the saved endpoints.</summary>
    Select,
    Add,
    Edit,
    Delete,
}

/// <summary>
/// One row of the endpoint picker: the official API, a saved endpoint, or an action. Actions sit
/// in the same list so the picker is the single place endpoints are chosen and managed.
/// </summary>
public sealed record AgentEndpointOption(
    AgentEndpointAction Action,
    string Label,
    AgentEndpointProfile? Profile = null)
{
    public override string ToString() => Label;
}

/// <summary>
/// Drives the AI side panel after the headless-chat rewrite: pick a local agent, host it in-app
/// (ConPTY), open it in Windows Terminal, launch Claude/Codex desktop via registered protocol, or
/// open the workspace folder in an editor — and keep the per-tab MCP endpoint available.
/// </summary>
public sealed partial class AgentCliPanelViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly string _workingDirectory;
    private readonly Action<bool>? _onHideSshTerminalChanged;
    private readonly Action<bool, bool>? _onSafetyOptionsChanged;
    private readonly Func<AgentCliKind, AgentCliRunMode>? _resolvePreferredRunMode;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private ConPtySession? _session;
    private Process? _externalProcess;
    private bool _detachedAppActive;
    private bool _disposed;
    /// <summary>Set while the picker is rebuilt, so repopulating it does not look like a choice.</summary>
    private bool _suppressEndpointSelection;
    /// <summary>Bumped on every start/stop request so superseded launches dispose their process.</summary>
    private int _startGeneration;
    private readonly object _captureGate = new();
    private FileStream? _captureStream;

    /// <summary>Raw VT capture file of the current embedded session (Debug MCP diagnostics), if enabled.</summary>
    public string? CaptureFilePath { get; private set; }

    /// <summary>Optional callback from the view: current terminal viewport in character cells.</summary>
    public Func<(int Cols, int Rows)>? GetViewportSize { get; set; }

    /// <summary>
    /// Optional hook run immediately before each CLI start, so the workspace can refresh
    /// <c>AGENTS.md</c>/<c>CLAUDE.md</c> and the project MCP configs (no server details on
    /// the command line).
    /// </summary>
    public Action? PrepareWorkspace { get; set; }

    /// <summary>
    /// Optional hook from the view: builds this tab's workspace identity plus the live MCP
    /// endpoint, used when linking the connection into the user's own project folders.
    /// </summary>
    public Func<AgentWorkspaceLink?>? ResolveLinkContext { get; set; }

    /// <summary>
    /// Optional hook from the view: this agent's saved endpoints and current selection, or null
    /// for agents that cannot be redirected. Read at launch so edits apply to the next start.
    /// </summary>
    public Func<AgentCliKind, AgentEndpointSettings?>? ResolveEndpoints { get; set; }

    /// <summary>
    /// Raised when the user picks an action from the endpoint picker. The view owns the dialogs,
    /// so it edits the settings and calls <see cref="ReloadEndpointOptions"/> when done.
    /// </summary>
    public event Action<AgentEndpointAction, AgentEndpointProfile?>? EndpointActionRequested;

    /// <summary>The endpoint the next launch will use, or null for the agent's official API.</summary>
    public AgentEndpointProfile? SelectedEndpointProfile
    {
        get
        {
            if (ResolveEndpoints?.Invoke(SelectedProvider.Kind) is not { } settings)
                return null;
            return settings.Profiles.FirstOrDefault(p => p.Id == settings.SelectedId);
        }
    }

    /// <summary>Absolute local workspace for this connection (%LOCALAPPDATA%\JeekRemoteManager\AgentWorkspaces\...).</summary>
    public string WorkingDirectory => _workingDirectory;

    public AgentCliPanelViewModel(
        string workingDirectory,
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

        PopulateRunModeOptions(_selectedProvider.Kind);

        // Prefer the slot for this provider, clamped to the modes it actually offers, so a
        // stored Desktop preference cannot select an agent that has no desktop protocol.
        _selectedRunModeOption = RunModeOptions.FirstOrDefault(o => o.Mode == preferredRunMode)
            ?? RunModeOptions[0];

        RefreshStatusFromProvider();
    }

    public ObservableCollection<AgentCliDescriptor> Providers { get; } = [];

    /// <summary>
    /// Launch modes the current provider offers. Desktop is omitted for agents without a desktop
    /// protocol, and editors offer IDE alone — so for them this list holds a single item and the
    /// picker stops being a choice.
    /// </summary>
    public ObservableCollection<AgentCliRunModeOption> RunModeOptions { get; } = [];

    [ObservableProperty]
    private AgentCliDescriptor _selectedProvider;

    [ObservableProperty]
    private AgentCliRunModeOption _selectedRunModeOption;

    /// <summary>
    /// Official API, then each saved endpoint, then add/edit/delete. Rebuilt whenever the agent,
    /// the mode, or the saved list changes.
    /// </summary>
    public ObservableCollection<AgentEndpointOption> EndpointOptions { get; } = [];

    [ObservableProperty]
    private AgentEndpointOption? _selectedEndpointOption;

    /// <summary>
    /// Endpoints only apply to the terminal surfaces we launch ourselves. A desktop app or editor
    /// is started by the shell or already running with its own settings, so the picker hides
    /// rather than implying a redirect that will not happen.
    /// </summary>
    public bool ShowEndpointPicker =>
        AgentEndpointConfig.Supports(SelectedProvider.Kind)
        && AgentCliCatalog.SurfaceKindFor(RunMode) == AgentSurfaceKind.Terminal;

    /// <summary>Current launch mode (CLI / Windows Terminal / Desktop / IDE).</summary>
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

    /// <summary>
    /// True when the selected agent is missing and we should show the install panel. Only a
    /// protocol-launched desktop app is exempt — the shell resolves it, so there is no local
    /// binary to find. Every other surface, IDE and Antigravity 2.0 included, starts an
    /// executable that has to be there.
    /// </summary>
    public bool ShowInstallPrompt =>
        !IsRunning
        && !IsInstalling
        && !LaunchesWithoutLocalBinary
        && SelectedSurface?.IsAvailable != true;

    /// <summary>
    /// The program behind the current provider and mode. Agents that ship several — Antigravity's
    /// CLI, desktop app, and IDE — resolve to a different one per mode, so availability, the
    /// install hint, and the launch all have to be read from here rather than the provider.
    /// </summary>
    private AgentSurface? SelectedSurface => SelectedProvider.SurfaceFor(RunMode);

    /// <summary>Desktop mode on an agent that registers a URI rather than shipping an exe we run.</summary>
    private bool LaunchesWithoutLocalBinary =>
        RunMode == AgentCliRunMode.Desktop
        && AgentCliCatalog.DesktopLaunch(SelectedProvider.Kind) == AgentDesktopLaunch.Protocol;

    /// <summary>Start is only for installed, idle agents (or a protocol-launched desktop app).</summary>
    public bool ShowStartButton =>
        !IsRunning && !IsInstalling && CanStartSelectedProvider();

    /// <summary>Embedded ConPTY surface (CLI mode only; hidden while the install prompt is up).</summary>
    public bool ShowEmbeddedTerminal =>
        !ShowInstallPrompt && RunMode == AgentCliRunMode.Cli;

    /// <summary>Placeholder when the agent runs outside the side panel (WT / Desktop / IDE).</summary>
    public bool ShowExternalHint =>
        !ShowInstallPrompt && RunMode != AgentCliRunMode.Cli;

    /// <summary>Localized hint for the external (WT / Desktop / IDE) surface.</summary>
    public string ExternalHintText => RunMode switch
    {
        AgentCliRunMode.Desktop => L("AiCliDesktopHint"),
        AgentCliRunMode.Ide => L("AiCliIdeHint"),
        _ => L("AiCliExternalHint"),
    };

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
        // Rebuild options for the new provider and restore that family's stored mode
        // (e.g. Grok CLI/WT → Claude Desktop → VS Code's single IDE mode).
        if (SyncRunModeOptions(value.Kind, _resolvePreferredRunMode?.Invoke(value.Kind) ?? RunMode))
            return; // The run-mode change already requested a start.

        ReloadEndpointOptions();
        NotifyLayoutFlags();
        InstallCommand.NotifyCanExecuteChanged();
        RefreshStatusFromProvider();
        ApplySelection();
    }

    partial void OnSelectedRunModeOptionChanged(AgentCliRunModeOption value)
    {
        OnPropertyChanged(nameof(RunMode));
        ReloadEndpointOptions();
        NotifyLayoutFlags();
        InstallCommand.NotifyCanExecuteChanged();
        RefreshStatusFromProvider();
        ApplySelection();
    }

    /// <summary>
    /// Rebuilds the endpoint picker from the saved list and lands on the selected endpoint.
    /// Called by the view after it adds, edits, or deletes one.
    /// </summary>
    public void ReloadEndpointOptions()
    {
        _suppressEndpointSelection = true;
        try
        {
            EndpointOptions.Clear();
            OnPropertyChanged(nameof(ShowEndpointPicker));
            if (!ShowEndpointPicker)
            {
                SelectedEndpointOption = null;
                return;
            }

            var settings = ResolveEndpoints?.Invoke(SelectedProvider.Kind);
            EndpointOptions.Add(new AgentEndpointOption(AgentEndpointAction.Select, L("AiEndpointOfficial")));
            foreach (var profile in settings?.Profiles ?? [])
            {
                EndpointOptions.Add(new AgentEndpointOption(
                    AgentEndpointAction.Select,
                    profile.Name.Length > 0 ? profile.Name : profile.BaseUrl,
                    profile));
            }

            EndpointOptions.Add(new AgentEndpointOption(AgentEndpointAction.Add, L("AiEndpointAdd")));
            // Edit and delete act on the current selection, so they are pointless on Official.
            if (SelectedEndpointProfile is not null)
            {
                EndpointOptions.Add(new AgentEndpointOption(AgentEndpointAction.Edit, L("AiEndpointEdit")));
                EndpointOptions.Add(new AgentEndpointOption(AgentEndpointAction.Delete, L("AiEndpointDelete")));
            }

            var selectedId = settings?.SelectedId ?? "";
            SelectedEndpointOption = EndpointOptions.FirstOrDefault(o =>
                                         o.Action == AgentEndpointAction.Select
                                         && (o.Profile?.Id ?? "") == selectedId)
                                     ?? EndpointOptions[0];
        }
        finally
        {
            _suppressEndpointSelection = false;
        }
    }

    partial void OnSelectedEndpointOptionChanged(AgentEndpointOption? value)
    {
        if (_suppressEndpointSelection || value is null || _disposed)
            return;

        if (value.Action != AgentEndpointAction.Select)
        {
            // Actions are not a selection: hand off to the view and put the picker back where
            // it was, so the box never reads "Add…" as though it were the chosen endpoint.
            var action = value.Action;
            var target = SelectedEndpointProfile;
            ReloadEndpointOptions();
            EndpointActionRequested?.Invoke(action, target);
            return;
        }

        if (ResolveEndpoints?.Invoke(SelectedProvider.Kind) is not { } settings)
            return;

        var newId = value.Profile?.Id ?? "";
        if (settings.SelectedId == newId)
            return;

        settings.SelectedId = newId;
        EndpointActionRequested?.Invoke(AgentEndpointAction.Select, value.Profile);
        // Edit/Delete only make sense once something is selected, so the list changes shape.
        ReloadEndpointOptions();
        RefreshStatusFromProvider();
        if (IsRunning)
            _ = RestartAsync();
    }

    /// <summary>
    /// Brings the running agent in line with the current provider and mode: launch the new one,
    /// or — when it cannot launch because it is not installed — stop the old one. Leaving the
    /// previous session alive would keep another agent's terminal on screen under this agent's
    /// status text, and its running state would suppress the install prompt the user needs.
    ///
    /// Fire-and-forget is OK: both paths are serialized on the start gate and generation-gated,
    /// so rapid ComboBox changes only leave the last one standing.
    /// </summary>
    private void ApplySelection()
    {
        if (_disposed || IsInstalling)
            return;

        if (CanStartSelectedProvider())
            _ = StartAsync();
        else if (IsRunning)
            _ = StopForSelectionChangeAsync();
    }

    /// <summary>
    /// Stops the previous agent because the newly selected one cannot start. Reported as
    /// replaced rather than user-stopped, so the view clears its surface instead of showing a
    /// "session ended" banner for an agent the user did not stop.
    /// </summary>
    private async Task StopForSelectionChangeAsync()
    {
        Interlocked.Increment(ref _startGeneration);
        await _startGate.WaitAsync().ConfigureAwait(true);
        try
        {
            await StopInternalAsync(userStopped: false, replaced: true).ConfigureAwait(true);
            RefreshStatusFromProvider();
            NotifyLayoutFlags();
        }
        finally
        {
            _startGate.Release();
        }
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
        OnPropertyChanged(nameof(ShowEndpointPicker));
        OnPropertyChanged(nameof(ShowInstallPrompt));
        OnPropertyChanged(nameof(ShowStartButton));
        OnPropertyChanged(nameof(ShowEmbeddedTerminal));
        OnPropertyChanged(nameof(ShowExternalHint));
        OnPropertyChanged(nameof(ExternalHintText));
    }

    private static string RunModeLabel(AgentCliRunMode mode) => mode switch
    {
        AgentCliRunMode.WindowsTerminal => "Windows Terminal",
        AgentCliRunMode.Desktop => "Desktop",
        AgentCliRunMode.Ide => "IDE",
        _ => "CLI",
    };

    /// <summary>Fills an empty picker for the initial provider, without touching any selection.</summary>
    private void PopulateRunModeOptions(AgentCliKind kind)
    {
        RunModeOptions.Clear();
        foreach (var mode in AgentCliCatalog.RunModesFor(kind))
            RunModeOptions.Add(new AgentCliRunModeOption(mode, RunModeLabel(mode)));
    }

    /// <summary>
    /// Rebuilds the picker for <paramref name="kind"/> and lands on <paramref name="preferred"/>
    /// when that agent offers it. Wanted modes are added before stale ones are removed, so the
    /// bound selection always has an item to point at — clearing the collection would null the
    /// ComboBox's selection and drop us into a start with the wrong mode.
    /// </summary>
    /// <returns>True when the selection changed, which has already requested a start.</returns>
    private bool SyncRunModeOptions(AgentCliKind kind, AgentCliRunMode preferred)
    {
        var wanted = AgentCliCatalog.RunModesFor(kind);

        foreach (var mode in wanted)
        {
            if (RunModeOptions.All(o => o.Mode != mode))
                RunModeOptions.Add(new AgentCliRunModeOption(mode, RunModeLabel(mode)));
        }

        var target = wanted.Contains(preferred) ? preferred : wanted[0];
        var changed = RunMode != target;
        if (changed)
        {
            SelectedRunModeOption = RunModeOptions.First(o => o.Mode == target);
        }
        else if (RunModeOptions.FirstOrDefault(o => o.Mode == target) is { } match
                 && !ReferenceEquals(match, SelectedRunModeOption))
        {
            // Same mode, different option instance: keep the binding on a live item. Assign the
            // backing field on purpose — going through the property would run the changed handler
            // and start the agent again, even though the mode did not actually change.
#pragma warning disable MVVMTK0034
            _selectedRunModeOption = match;
#pragma warning restore MVVMTK0034
            OnPropertyChanged(nameof(SelectedRunModeOption));
        }

        for (var i = RunModeOptions.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(RunModeOptions[i].Mode))
                RunModeOptions.RemoveAt(i);
        }

        return changed;
    }

    private bool CanStartSelectedProvider() =>
        LaunchesWithoutLocalBinary || SelectedSurface?.IsAvailable == true;

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

        if (LaunchesWithoutLocalBinary || SelectedSurface?.IsAvailable == true)
        {
            StatusText = IsRunning
                ? RunMode switch
                {
                    AgentCliRunMode.WindowsTerminal => L("AiCliRunningExternal", SelectedProvider.Label),
                    AgentCliRunMode.Desktop => L("AiCliRunningDesktop", SelectedProvider.Label),
                    AgentCliRunMode.Ide => L("AiCliRunningIde", SelectedProvider.Label),
                    _ => L("AiCliRunning", SelectedProvider.Label),
                }
                : L("AiCliReady", SelectedProvider.Label);
            return;
        }

        // Languages.tab uses {2} for newlines so the hint stays readable. Agents we cannot
        // install for the user show a download page, which is not a command to run.
        StatusText = string.Format(
            L(SelectedSurface?.CanAutoInstall == true ? "AiCliNotInstalled" : "AiCliNotInstalledDownload"),
            SelectedProvider.Label,
            SelectedSurface?.InstallHint ?? "",
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
        if (IsRunning && (_session is not null || _externalProcess is not null || _detachedAppActive))
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
        && SelectedSurface is { CanAutoInstall: true, IsAvailable: false }
        && !LaunchesWithoutLocalBinary;

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync()
    {
        if (_disposed || !CanInstall())
            return;

        IsInstalling = true;
        StatusText = string.Format(
            L("AiCliInstalling"),
            SelectedProvider.Label,
            SelectedSurface?.InstallHint ?? "",
            Environment.NewLine);
        var kind = SelectedProvider.Kind;
        // Install the surface the current mode needs — Antigravity's CLI and IDE are separate
        // downloads, so which one is missing depends on the mode the user picked.
        var surface = AgentCliCatalog.SurfaceKindFor(RunMode);
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

            var result = await AgentCliInstaller.InstallAsync(kind, surface, progress)
                .ConfigureAwait(true);
            RediscoverProviders(preferKind: kind);

            if (result.Success && SelectedSurface?.IsAvailable == true)
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

            var desktopLaunch = AgentCliCatalog.DesktopLaunch(provider.Kind);
            if (runMode == AgentCliRunMode.Desktop && desktopLaunch == AgentDesktopLaunch.None)
            {
                StatusText = L("AiCliDesktopUnsupported", provider.Label);
                NotifyLayoutFlags();
                return;
            }

            // Only a protocol launch works without a local binary; every other surface needs its
            // own executable on disk, and which one that is depends on the mode.
            var surface = provider.SurfaceFor(runMode);
            if (!(runMode == AgentCliRunMode.Desktop && desktopLaunch == AgentDesktopLaunch.Protocol)
                && surface?.ExecutablePath is null)
            {
                StatusText = surface?.InstallHint ?? L("AiCliDesktopUnsupported", provider.Label);
                NotifyLayoutFlags();
                return;
            }

            try
            {
                // Refresh AGENTS.md / CLAUDE.md + project MCP configs before the CLI (or a
                // desktop app opening this folder) loads them.
                try { PrepareWorkspace?.Invoke(); }
                catch { /* best-effort; still launch the agent */ }

                Directory.CreateDirectory(_workingDirectory);

                if (runMode == AgentCliRunMode.Desktop)
                {
                    if (!TryStartDesktopApp(provider, surface))
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

                var exePath = surface!.ExecutablePath!;

                if (runMode == AgentCliRunMode.Ide)
                {
                    if (!TryStartOnWorkspaceFolder(exePath))
                    {
                        if (generation != Volatile.Read(ref _startGeneration))
                            return;
                        StatusText = L("AiCliStartFailed", L("AiCliIdeLaunchFailed", provider.Label));
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
                    StatusText = L("AiCliRunningIde", provider.Label);
                    NotifyLayoutFlags();
                    return;
                }

                // Runtime flags only (auto-approve tools / scrollback). Server context is in AGENTS.md.
                var args = AgentCliCatalog.BuildInteractiveArguments(provider.Kind, AutoRun);
                // Custom endpoint, when the user configured one: base URL and key reach the agent
                // as environment variables so the key never lands in a config file.
                var environment = AgentEndpointConfig.BuildEnvironment(
                    provider.Kind,
                    SelectedEndpointProfile);

                if (runMode == AgentCliRunMode.WindowsTerminal)
                {
                    if (!TryStartWindowsTerminal(exePath, args, environment))
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
                        _workingDirectory,
                        environment)).ConfigureAwait(true);

                if (_disposed || generation != Volatile.Read(ref _startGeneration))
                {
                    try { session.Dispose(); } catch { /* ignore */ }
                    return;
                }

                _session = session;
                StartRawCaptureIfEnabled(session);
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
    /// One-shot write of this connection's reference block and MCP entry into
    /// <paramref name="projectDirectory"/>, so agents started there can drive it. Nothing is
    /// remembered afterwards — the entry launches the local adapter and never expires.
    /// </summary>
    public bool WriteToProject(string projectDirectory)
    {
        if (ResolveLinkContext?.Invoke() is not { } link)
        {
            StatusText = L("AiLinkProjectUnavailable");
            return false;
        }

        try
        {
            StatusText = L("AiLinkProjectDone", AgentProjectLink.WriteInto(link, projectDirectory));
            return true;
        }
        catch (Exception ex)
        {
            StatusText = L("AiLinkProjectFailed", FormatExceptionMessage(ex));
            return false;
        }
    }

    /// <summary>Takes this connection back out of a project folder the user picks.</summary>
    public bool RemoveFromProject(string projectDirectory)
    {
        if (ResolveLinkContext?.Invoke() is not { } link)
        {
            StatusText = L("AiLinkProjectUnavailable");
            return false;
        }

        try
        {
            StatusText = L("AiUnlinkProjectDone", AgentProjectLink.RemoveFrom(link, projectDirectory));
            return true;
        }
        catch (Exception ex)
        {
            StatusText = L("AiLinkProjectFailed", FormatExceptionMessage(ex));
            return false;
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

        if (!IsRunning && _session is null && _externalProcess is null && !_detachedAppActive)
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
        _detachedAppActive = false;

        IsRunning = false;
        if (userStopped)
            StatusText = L("AiCliStopped", SelectedProvider.Label);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Opens the workspace in the agent's desktop app, whichever way that agent supports:
    /// Claude and Codex register a URI the shell hands off, while Antigravity 2.0 is an
    /// executable we start on the folder like an editor.
    /// </summary>
    private bool TryStartDesktopApp(AgentCliDescriptor provider, AgentSurface? surface)
    {
        if (AgentCliCatalog.DesktopLaunch(provider.Kind) == AgentDesktopLaunch.Executable)
            return surface?.ExecutablePath is { } exe && TryStartOnWorkspaceFolder(exe);

        var uri = AgentCliCatalog.BuildDesktopProtocolUri(provider.Kind, _workingDirectory);
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
            _detachedAppActive = true;
            return true;
        }
        catch
        {
            _detachedAppActive = false;
            return false;
        }
    }

    /// <summary>
    /// Opens the workspace folder in an editor or desktop app. That app is a long-lived window
    /// the user owns, not a session we manage: it may already be running (in which case it just
    /// opens a new window and the process we started exits at once), and stopping the panel
    /// never closes it.
    /// </summary>
    private bool TryStartOnWorkspaceFolder(string exePath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = false,
                WorkingDirectory = _workingDirectory,
            };
            psi.ArgumentList.Add(_workingDirectory);
            Process.Start(psi);
            _detachedAppActive = true;
            return true;
        }
        catch
        {
            _detachedAppActive = false;
            return false;
        }
    }

    private bool TryStartWindowsTerminal(
        string exePath,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string> environment)
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
        // wt passes its environment down to the console process it launches.
        foreach (var (name, value) in environment)
            psi.Environment[name] = value;
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
    public void ResizeSession(int cols, int rows)
    {
        _session?.Resize(cols, rows);
        RecordCaptureResize(cols, rows);
    }

    /// <summary>
    /// When JRM_AI_CAPTURE_DIR is set, records the raw ConPTY byte stream of the
    /// embedded session plus a ".resizes" sidecar (byteOffset:COLSxROWS per line) so
    /// rendering bugs can be replayed offline against the terminal emulator.
    /// </summary>
    private void StartRawCaptureIfEnabled(ConPtySession session)
    {
        var dir = Environment.GetEnvironmentVariable("JRM_AI_CAPTURE_DIR");
        if (string.IsNullOrWhiteSpace(dir))
            return;

        try
        {
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"ai-{Environment.ProcessId}-{DateTime.Now:HHmmss-fff}.bin");
            var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            lock (_captureGate)
            {
                _captureStream?.Dispose();
                _captureStream = stream;
                CaptureFilePath = path;
            }

            session.DataReceived += data =>
            {
                lock (_captureGate)
                {
                    if (_captureStream != stream)
                        return;
                    try
                    {
                        stream.Write(data, 0, data.Length);
                        stream.Flush();
                    }
                    catch
                    {
                        // capture is best-effort diagnostics
                    }
                }
            };
        }
        catch
        {
            // capture is best-effort diagnostics
        }
    }

    private void RecordCaptureResize(int cols, int rows)
    {
        lock (_captureGate)
        {
            if (_captureStream is not { } stream || CaptureFilePath is not { } path)
                return;
            try
            {
                File.AppendAllText(path + ".resizes", $"{stream.Length}:{cols}x{rows}\n");
            }
            catch
            {
                // capture is best-effort diagnostics
            }
        }
    }

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
