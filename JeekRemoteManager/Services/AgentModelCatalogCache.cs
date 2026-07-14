using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace JeekRemoteManager.Services;

/// <summary>
/// Persists the last model catalog successfully reported by each locally installed AI CLI.
/// The cache is machine-local because the available models can depend on that CLI's version
/// and signed-in account. Callers can use it immediately while a fresh catalog is requested.
/// </summary>
public static class AgentModelCatalogCache
{
    private static readonly object SyncRoot = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string CachePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JeekRemoteManager",
        "Config",
        "ai-model-catalogs.json");

    public static IReadOnlyList<AgentModelInfo>? Load(string provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
            return null;

        lock (SyncRoot)
        {
            var catalogs = ReadAll();
            return catalogs.TryGetValue(provider, out var models)
                ? Normalize(models)
                : null;
        }
    }

    public static void Save(string provider, IReadOnlyList<AgentModelInfo> models)
    {
        if (string.IsNullOrWhiteSpace(provider))
            return;

        var normalized = Normalize(models);
        if (normalized is null)
            return;

        lock (SyncRoot)
        {
            try
            {
                var catalogs = ReadAll();
                catalogs[provider] = normalized.ToList();

                var directory = Path.GetDirectoryName(CachePath);
                if (string.IsNullOrWhiteSpace(directory))
                    return;

                Directory.CreateDirectory(directory);
                var tempPath = CachePath + ".tmp";
                File.WriteAllText(tempPath, JsonSerializer.Serialize(catalogs, JsonOptions));
                File.Move(tempPath, CachePath, overwrite: true);
            }
            catch
            {
                // Model discovery is best-effort; a read-only or corrupt cache must not
                // prevent the AI panel from using the live/static catalog.
            }
        }
    }

    private static Dictionary<string, List<AgentModelInfo>> ReadAll()
    {
        try
        {
            if (!File.Exists(CachePath))
                return new(StringComparer.OrdinalIgnoreCase);

            var catalogs = JsonSerializer.Deserialize<Dictionary<string, List<AgentModelInfo>>>(
                File.ReadAllText(CachePath),
                JsonOptions);
            return catalogs is null
                ? new(StringComparer.OrdinalIgnoreCase)
                : new(catalogs, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static IReadOnlyList<AgentModelInfo>? Normalize(IEnumerable<AgentModelInfo>? models)
    {
        if (models is null)
            return null;

        var result = new List<AgentModelInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in models)
        {
            if (model is null || string.IsNullOrWhiteSpace(model.Id) || !seen.Add(model.Id))
                continue;

            var id = model.Id.Trim();
            var displayName = string.IsNullOrWhiteSpace(model.DisplayName)
                ? id
                : model.DisplayName.Trim();
            var efforts = (model.ReasoningEfforts ?? [])
                .Where(e => !string.IsNullOrWhiteSpace(e))
                .Select(e => e.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            result.Add(new AgentModelInfo(id, displayName, model.IsDefault, efforts));
        }

        return result.Count == 0 ? null : result;
    }
}
