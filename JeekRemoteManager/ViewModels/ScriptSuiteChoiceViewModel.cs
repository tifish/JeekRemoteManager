using Jeek.Avalonia.Localization;
using JeekRemoteManager.Models;

namespace JeekRemoteManager.ViewModels;

public class ScriptSuiteChoiceViewModel
{
    public ScriptSuiteChoiceViewModel(RemoteScriptSuite suite, bool hasParameters)
    {
        Suite = suite;
        HasParameters = hasParameters;
    }

    public RemoteScriptSuite Suite { get; }

    public bool HasParameters { get; }

    public string StatusText => HasParameters ? Localizer.Get("HasScriptParameters") : "";

    public string SourceText => Suite.Source == RemoteScriptSuiteSource.User
        ? Localizer.Get("ScriptSourceUser")
        : Localizer.Get("ScriptSourceBuiltIn");

    public override string ToString() => HasParameters
        ? $"{Suite.Name} ({SourceText}, {Localizer.Get("HasScriptParameters")})"
        : $"{Suite.Name} ({SourceText})";
}
