using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using JeekRemoteManager.Models;

namespace JeekRemoteManager.Services;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as a small JSON file, and resolves
/// the connections root folder for a storage location.
///
/// Portability is decided purely by whether a "Connections" folder sits next to
/// the executable: if it does, the app runs portable (settings and data live
/// next to the exe); otherwise both live under the per-user roaming folder.
/// Switching storage location moves the data and removes the old location, so
/// the folder-presence rule keeps matching the chosen mode on the next launch.
/// </summary>
public class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static string ProgramDir => AppContext.BaseDirectory;

    private static string UserDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "JeekRemoteManager");

    /// <summary>
    /// True when a "Connections" folder sits next to the executable. Evaluated
    /// live, so it reflects the current on-disk state after a storage switch.
    /// </summary>
    public static bool IsPortable =>
        Directory.Exists(Path.Combine(ProgramDir, "Connections"));

    public SettingsService()
    {
        var location = IsPortable ? StorageLocation.ProgramDirectory : StorageLocation.UserDirectory;
        SettingsPath = SettingsPathFor(location);
        Settings = Load();
        // The folder-presence rule is authoritative for which mode we're in.
        Settings.StorageLocation = location;
    }

    public string SettingsPath { get; private set; }

    public AppSettings Settings { get; private set; }

    private static string SettingsPathFor(StorageLocation location) =>
        location == StorageLocation.ProgramDirectory
            ? Path.Combine(ProgramDir, "settings.json")
            : Path.Combine(UserDir, "settings.json");

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

    /// <summary>
    /// Re-points where settings.json lives to match a new storage location and
    /// removes the settings file at the old location. Call before <see cref="Save"/>.
    /// </summary>
    public void RelocateSettings(StorageLocation location)
    {
        var newPath = SettingsPathFor(location);
        if (string.Equals(
                Path.GetFullPath(newPath),
                Path.GetFullPath(SettingsPath),
                StringComparison.OrdinalIgnoreCase))
            return;

        var oldPath = SettingsPath;
        SettingsPath = newPath;
        try
        {
            if (File.Exists(oldPath))
                File.Delete(oldPath);
        }
        catch
        {
            // Leaving a stale settings file behind is harmless.
        }
    }

    /// <summary>Persists the current settings. Returns false if writing failed.</summary>
    public bool Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
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
            Path.Combine(ProgramDir, "Connections"),
        _ =>
            Path.Combine(UserDir, "Connections"),
    };
}
