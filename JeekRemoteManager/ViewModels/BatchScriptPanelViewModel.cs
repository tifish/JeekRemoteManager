using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JeekRemoteManager.Models;

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

        StatusText = L("BatchScriptReady", Targets.Count);
    }

    public RemoteScriptSuite Suite { get; }

    public string SuiteName => Suite.Name;

    public string TargetSummary => L("BatchRunTargets", Targets.Count);

    public ObservableCollection<RemoteScriptFile> Scripts { get; } = new();

    public ObservableCollection<BatchScriptTargetViewModel> Targets { get; }

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
