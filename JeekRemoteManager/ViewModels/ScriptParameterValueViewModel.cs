using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using JeekRemoteManager.Models;

namespace JeekRemoteManager.ViewModels;

public partial class ScriptParameterValueViewModel : ViewModelBase
{
    private readonly Action _changed;
    private bool _suppressChanged;

    public ScriptParameterValueViewModel(RemoteScriptParameter parameter, string value, Action changed)
    {
        Parameter = parameter;
        _value = value;
        _changed = changed;
        _boolValue = IsTrue(value);
        _selectedEnumValue = parameter.EnumOptions.Contains(value) ? value : "";
        if (parameter.Type == RemoteScriptParameterType.Bool && string.IsNullOrEmpty(_value))
            _value = "false";
    }

    public RemoteScriptParameter Parameter { get; }

    public string Name => Parameter.Name;

    public RemoteScriptParameterType Type => Parameter.Type;

    public IReadOnlyList<string> EnumOptions => Parameter.EnumOptions;

    public bool IsBool => Type == RemoteScriptParameterType.Bool;

    public bool IsEnum => Type == RemoteScriptParameterType.Enum;

    public bool IsSecret => Type == RemoteScriptParameterType.Secret;

    public bool IsText => Type is RemoteScriptParameterType.String or RemoteScriptParameterType.Number;

    [ObservableProperty]
    private string _value = "";

    [ObservableProperty]
    private bool _boolValue;

    [ObservableProperty]
    private string _selectedEnumValue = "";

    partial void OnValueChanged(string value)
    {
        if (!_suppressChanged)
            _changed();
    }

    partial void OnBoolValueChanged(bool value)
    {
        Value = value ? "true" : "false";
        if (!_suppressChanged)
            _changed();
    }

    partial void OnSelectedEnumValueChanged(string value)
    {
        Value = value ?? "";
        if (!_suppressChanged)
            _changed();
    }

    public void Clear()
    {
        _suppressChanged = true;
        try
        {
            if (IsBool)
            {
                BoolValue = false;
                Value = "false";
            }
            else if (IsEnum)
            {
                SelectedEnumValue = "";
                Value = "";
            }
            else
            {
                Value = "";
            }
        }
        finally
        {
            _suppressChanged = false;
        }
    }

    private static bool IsTrue(string value) =>
        value.Equals("true", StringComparison.OrdinalIgnoreCase)
        || value == "1"
        || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
}
