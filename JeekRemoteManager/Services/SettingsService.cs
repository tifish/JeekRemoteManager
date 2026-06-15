using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using JeekRemoteManager.Models;

namespace JeekRemoteManager.Services;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as a small JSON file next to the
/// executable, and resolves the connections root folder for a storage location.
/// </summary>
public class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public SettingsService()
    {
        // Keep settings in a per-user writable location so the choice persists
        // even when the app is installed under %ProgramFiles% (read-only).
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "JeekRemoteManager");
        Directory.CreateDirectory(dir);
        SettingsPath = Path.Combine(dir, "settings.json");
        Settings = Load();
    }

    public string SettingsPath { get; }

    public AppSettings Settings { get; private set; }

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            }
        }
        catch
        {
            // Corrupt or unreadable settings: fall back to defaults.
        }

        return new AppSettings();
    }

    /// <summary>Persists the current settings. Returns false if writing failed.</summary>
    public bool Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(Settings, JsonOptions);
            File.WriteAllText(SettingsPath, json);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Resolves the connections root folder for the current setting.</summary>
    public string ResolveConnectionsRoot() => ResolveConnectionsRoot(Settings.StorageLocation);

    /// <summary>Resolves the connections root folder for a given storage location.</summary>
    public static string ResolveConnectionsRoot(StorageLocation location) => location switch
    {
        StorageLocation.ProgramDirectory =>
            Path.Combine(AppContext.BaseDirectory, "Connections"),
        _ =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "JeekRemoteManager",
                "Connections"),
    };
}
