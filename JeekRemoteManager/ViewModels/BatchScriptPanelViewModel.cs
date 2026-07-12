using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JeekRemoteManager.Models;
using JeekRemoteManager.Services;

namespace JeekRemoteManager.ViewModels;

public enum BatchScriptTargetState
{
    Pending,
    Connecting,
    Running,
    Succeeded,
    Failed,
    Skipped,
    Canceled,
}

/// <summary>
/// One server row of the batch script panel: live status of the script run on
/// that connection, plus a handle to the terminal tab it runs in.
/// </summary>
public partial class BatchScriptTargetViewModel : ViewModelBase
{
    public BatchScriptTargetViewModel(Connection connection, string sourcePath)
    {
        Connection = connection;
        SourcePath = sourcePath;
        Name = string.IsNullOrWhiteSpace(connection.Name) ? connection.Host : connection.Name;
    }

    public Connection Connection { get; }

    public string SourcePath { get; }

    public string Name { get; }

    /// <summary>The terminal tab the script runs in, so clicking the row can bring it up.</summary>
    public TerminalScriptSession? Terminal { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPendingIcon))]
    [NotifyPropertyChangedFor(nameof(ShowBusyIcon))]
    [NotifyPropertyChangedFor(nameof(ShowSuccessIcon))]
    [NotifyPropertyChangedFor(nameof(ShowFailureIcon))]
    [NotifyPropertyChangedFor(nameof(ShowSkippedIcon))]
    private BatchScriptTargetState _state = BatchScriptTargetState.Pending;

    [ObservableProperty]
    private string _statusText = "";

    public bool ShowPendingIcon => State == BatchScriptTargetState.Pending;

    public bool ShowBusyIcon => State is BatchScriptTargetState.Connecting or BatchScriptTargetState.Running;

    public bool ShowSuccessIcon => State == BatchScriptTargetState.Succeeded;

    public bool ShowFailureIcon => State == BatchScriptTargetState.Failed;

    public bool ShowSkippedIcon => State is BatchScriptTargetState.Skipped or BatchScriptTargetState.Canceled;

    public void SetState(BatchScriptTargetState state, string statusText)
    {
        State = state;
        StatusText = statusText;
    }

    [RelayCommand]
    private void Activate() => Terminal?.Activate();
}

/// <summary>
/// One suite parameter row of the batch panel. Unchecked, every server runs with
/// its own saved value; checked ("unified"), the edited value applies to all
/// targets and is written back to their bindings.
/// </summary>
public partial class BatchScriptParameterViewModel : ViewModelBase
{
    public BatchScriptParameterViewModel(RemoteScriptParameter parameter, string initialValue)
    {
        Editor = new ScriptParameterValueViewModel(parameter, initialValue, () => { });
    }

    /// <summary>Reuses the single-panel editor VM for its type dispatch
    /// (text/secret/bool/enum) and value normalization.</summary>
    public ScriptParameterValueViewModel Editor { get; }

    public string Name => Editor.Name;

    [ObservableProperty]
    private bool _isUnified;
}

/// <summary>
/// Panel shown in the editor tab to run one script suite on several selected
/// connections at once. Each target runs in its own terminal tab; the panel
/// aggregates per-server status. Orchestration lives on the main view model and
/// is reached through the delegates passed to the constructor.
/// </summary>
public partial class BatchScriptPanelViewModel : ViewModelBase
{
    private readonly Func<RemoteScriptFile, Task> _runRequested;
    private readonly Action _closeRequested;
    private int _finishedTargets;

    public BatchScriptPanelViewModel(
        RemoteScriptSuite suite,
        ObservableCollection<BatchScriptTargetViewModel> targets,
        Func<RemoteScriptFile, Task> runRequested,
        Action closeRequested)
    {
        Suite = suite;
        Targets = targets;
        _runRequested = runRequested;
        _closeRequested = closeRequested;

        foreach (var script in suite.Scripts)
            Scripts.Add(script);

        // Prefill each parameter with the value all targets already share, so
        // checking "unified" starts from the current state instead of the default.
        var savedBindings = targets
            .Select(t => FindSavedBinding(t.Connection, suite))
            .Select(b => b is null ? null : RemoteScriptLauncher.UnprotectSecretValues(suite, b))
            .ToList();
        foreach (var parameter in suite.Parameters)
        {
            var values = savedBindings
                .Select(b => b?.Params.FirstOrDefault(p =>
                    string.Equals(p.Name, parameter.Name, StringComparison.OrdinalIgnoreCase))?.Value)
                .ToList();
            var initial = values.Count > 0
                && values.All(v => v is not null)
                && values.Distinct(StringComparer.Ordinal).Count() == 1
                    ? values[0]!
                    : parameter.DefaultValue;
            Parameters.Add(new BatchScriptParameterViewModel(parameter, initial));
        }

        StatusText = L("BatchScriptReady", Targets.Count);
    }

    /// <summary>The connection's saved binding for the suite (cloned, secrets left
    /// protected), or null when it has none.</summary>
    public static ConnectionScriptBinding? FindSavedBinding(Connection connection, RemoteScriptSuite suite)
    {
        var stored = connection.ScriptBindings.LastOrDefault(b =>
            string.Equals(
                RemoteScriptSuiteNames.NormalizeBindingName(b.Name),
                suite.RelativePath,
                StringComparison.OrdinalIgnoreCase));
        if (stored is null)
            return null;

        var clone = RemoteScriptLauncher.CloneBinding(stored);
        clone.Name = suite.RelativePath;
        return clone;
    }

    public RemoteScriptSuite Suite { get; }

    public string SuiteName => Suite.Name;

    public string TargetSummary => L("BatchRunTargets", Targets.Count);

    public ObservableCollection<RemoteScriptFile> Scripts { get; } = new();

    public ObservableCollection<BatchScriptParameterViewModel> Parameters { get; } = new();

    public bool HasParameters => Parameters.Count > 0;

    public ObservableCollection<BatchScriptTargetViewModel> Targets { get; }

    /// <summary>Snapshot of the checked ("unified") parameter values, taken once
    /// per run so edits mid-run cannot produce mixed results across targets.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> GetUnifiedParameterValues() =>
        Parameters
            .Where(p => p.IsUnified)
            .Select(p => new KeyValuePair<string, string>(p.Name, p.Editor.Value))
            .ToList();

    /// <summary>Owned by the orchestrator for the duration of a run; the Cancel
    /// button only signals it.</summary>
    public CancellationTokenSource? Cts { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunScriptCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyCanExecuteChangedFor(nameof(CloseCommand))]
    private bool _isRunning;

    [ObservableProperty]
    private string _statusText = "";

    public void BeginRun()
    {
        _finishedTargets = 0;
        foreach (var target in Targets)
            target.SetState(BatchScriptTargetState.Pending, L("BatchTargetPending"));
    }

    public void ReportTargetFinished()
    {
        _finishedTargets++;
        StatusText = L("BatchStatusProgress", _finishedTargets, Targets.Count);
    }

    [RelayCommand(CanExecute = nameof(CanRunScript))]
    private Task RunScript(RemoteScriptFile? scriptFile) =>
        scriptFile is null ? Task.CompletedTask : _runRequested(scriptFile);

    private bool CanRunScript(RemoteScriptFile? scriptFile) => !IsRunning;

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Cancel() => Cts?.Cancel();

    [RelayCommand(CanExecute = nameof(CanClose))]
    private void Close() => _closeRequested();

    private bool CanClose() => !IsRunning;
}
