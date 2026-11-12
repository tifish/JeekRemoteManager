using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using JeekRemoteManager.Models;

namespace JeekRemoteManager.Services;

internal static partial class DebugMcpServer
{
    private static Task<JsonObject> BuiltInScriptsSnapshotAsync()
    {
        // The isolated empty user root prevents user overrides from hiding bundled suites.
        var userRoot = Path.Combine(Path.GetTempPath(), "jrm-script-snapshot-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new RemoteScriptStore(userRoot);
            var suites = store.LoadAll().Where(s => s.Source == RemoteScriptSuiteSource.BuiltIn)
                .Select(s => new
                {
                    name = s.Name,
                    relativePath = s.RelativePath,
                    parameters = s.Parameters.Select(p => new { name = p.Name, type = p.Type.ToString() }),
                    scripts = s.Scripts.Select(script => script.Name),
                    errors = s.Errors,
                });
            return Task.FromResult(ToolText(JsonSerializer.Serialize(new { suites }, PrettyOptions)));
        }
        finally
        {
            if (Directory.Exists(userRoot))
                Directory.Delete(userRoot);
        }
    }
}
