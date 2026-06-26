using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using JeekRemoteManager.Models;
using JeekRemoteManager.Services;

namespace JeekRemoteManager.ViewModels;

public partial class ConnectionScriptBindingViewModel : ViewModelBase
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    private string _name = "";

    public List<ConnectionScriptParameterValue> Params { get; set; } = new();

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? L("UnnamedScriptBinding") : Name;

    public static ConnectionScriptBindingViewModel FromModel(ConnectionScriptBinding binding) => new()
    {
        Name = RemoteScriptSuiteNames.NormalizeBindingName(binding.Name),
        Params = binding.Params
            .Select(v => new ConnectionScriptParameterValue { Name = v.Name, Value = v.Value })
            .ToList(),
    };

    public ConnectionScriptBinding ToModel() => new()
    {
        Name = RemoteScriptSuiteNames.NormalizeBindingName(Name),
        Params = Params
            .Select(v => new ConnectionScriptParameterValue { Name = v.Name, Value = v.Value })
            .ToList(),
    };

    public ConnectionScriptBinding ToProtectedModel(RemoteScriptSuite? suite) =>
        suite is null
            ? ToModel()
            : RemoteScriptLauncher.ProtectSecretValues(suite, ToModel());
}
