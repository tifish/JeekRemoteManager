using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JeekRemoteManager.Models;

public enum RemoteScriptParameterType
{
    String,
    Number,
    Bool,
    Secret,
    Enum,
}

public enum RemoteScriptSuiteSource
{
    BuiltIn,
    User,
}

public class RemoteScriptParameter
{
    public string Name { get; set; } = "";

    public RemoteScriptParameterType Type { get; set; } = RemoteScriptParameterType.String;

    public string DefaultValue { get; set; } = "";

    public List<string> EnumOptions { get; set; } = new();
}

public class RemoteScriptFile
{
    public string Name { get; set; } = "";

    public string DisplayName { get; set; } = "";

    public string FullPath { get; set; } = "";
}

public class RemoteScriptSuite
{
    public string Name { get; set; } = "";

    public string RelativePath { get; set; } = "";

    public string FullPath { get; set; } = "";

    public RemoteScriptSuiteSource Source { get; set; } = RemoteScriptSuiteSource.User;

    public List<RemoteScriptParameter> Parameters { get; set; } = new();

    public List<RemoteScriptFile> Scripts { get; set; } = new();

    public List<string> Errors { get; set; } = new();
}

public class ConnectionScriptParameterValue
{
    public string Name { get; set; } = "";

    public string Value { get; set; } = "";
}

public class ConnectionScriptBinding : IJsonOnDeserialized
{
    public string Name { get; set; } = "";

    public List<ConnectionScriptParameterValue> Params { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    void IJsonOnDeserialized.OnDeserialized()
    {
        if (string.IsNullOrWhiteSpace(Name)
            && ExtensionData?.TryGetValue("SuitePath", out var suitePath) == true
            && suitePath.ValueKind == JsonValueKind.String)
        {
            Name = suitePath.GetString() ?? "";
        }

        if (Params.Count == 0
            && ExtensionData?.TryGetValue("Values", out var values) == true
            && values.ValueKind == JsonValueKind.Array)
        {
            Params = values.Deserialize<List<ConnectionScriptParameterValue>>() ?? new();
        }

        ExtensionData?.Remove("SuitePath");
        ExtensionData?.Remove("Values");
    }
}
