using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using JeekRemoteManager.Models;

namespace JeekRemoteManager.ViewModels;

public partial class ScriptSuitePanelViewModel : ViewModelBase
{
    private readonly Action _parametersChanged;

    public ScriptSuitePanelViewModel(
        RemoteScriptSuite suite,
        ConnectionScriptBinding binding,
        Action parametersChanged)
    {
        Suite = suite;
        _parametersChanged = parametersChanged;

        foreach (var parameter in suite.Parameters)
        {
            var value = binding.Params.FirstOrDefault(v =>
                string.Equals(v.Name, parameter.Name, StringComparison.OrdinalIgnoreCase))?.Value ?? "";
            Parameters.Add(new ScriptParameterValueViewModel(parameter, value, _parametersChanged));
        }

        ResetScripts();
        StatusText = L("ScriptPanelReady", suite.Name);
    }

    public RemoteScriptSuite Suite { get; }

    public string SuiteName => Suite.Name;

    public ObservableCollection<ScriptParameterValueViewModel> Parameters { get; } = new();

    public ObservableCollection<RemoteScriptFile> Scripts { get; } = new();

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowResultIcon))]
    [NotifyPropertyChangedFor(nameof(ShowSuccessIcon))]
    [NotifyPropertyChangedFor(nameof(ShowFailureIcon))]
    private bool? _lastRunSucceeded;

    public bool ShowResultIcon => LastRunSucceeded.HasValue;

    public bool ShowSuccessIcon => LastRunSucceeded.GetValueOrDefault();

    public bool ShowFailureIcon => LastRunSucceeded.HasValue && !LastRunSucceeded.Value;

    [ObservableProperty]
    private string _output = "";

    public void ClearExecutionResult()
    {
        LastRunSucceeded = null;
    }

    public void SetExecutionResult(bool succeeded)
    {
        LastRunSucceeded = succeeded;
    }

    public void ResetScripts()
    {
        Scripts.Clear();
        foreach (var script in Suite.Scripts)
            Scripts.Add(script);
    }

    public ConnectionScriptBinding ToBinding() => new()
    {
        Name = Suite.RelativePath,
        Params = Parameters
            .Select(p => new ConnectionScriptParameterValue { Name = p.Name, Value = p.Value })
            .ToList(),
    };

    public void AppendOutput(string text)
    {
        Output += text;
    }

    public void ClearParameters()
    {
        foreach (var parameter in Parameters)
            parameter.Clear();
    }
}
