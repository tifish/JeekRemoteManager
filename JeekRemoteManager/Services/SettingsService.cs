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
/// Within a storage location two sibling folders sit under the base: "Config"
/// (holding settings.json) and "Connections" (the data). Keeping them together
/// makes portable installs easy to carry as a folder.
///
/// A saved settings.json is authoritative for startup location. The older
/// "Connections next to the executable means portable" rule is kept only as a
/// fallback for installs that do not have a Config/settings.json yet; this lets
/// storage switches copy data while leaving the old Connections folder in place.
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

    /// <summary>True when startup will use the executable directory.</summary>
    public static bool IsPortable =>
        File.Exists(SettingsPathFor(StorageLocation.ProgramDirectory)) ||
        (!File.Exists(SettingsPathFor(StorageLocation.UserDirectory)) &&
         Directory.Exists(Path.Combine(ProgramDir, "Connections")));

    public SettingsService()
    {
        MigrateLegacySettingsFile(StorageLocation.UserDirectory);
        MigrateLegacySettingsFile(StorageLocation.ProgramDirectory);

        var location = ResolveStartupLocation();
        SettingsPath = SettingsPathFor(location);
        Settings = Load();
        // The resolved startup location is authoritative for this session.
        Settings.StorageLocation = location;

        RecentPath = Path.Combine(UserDir, "Config", "recent.json");
        Recent = LoadRecent();
        MigrateLegacyRecentFromSettings();
    }

    public string SettingsPath { get; private set; }

    public AppSettings Settings { get; private set; }

    /// <summary>Path to the per-user recent-connections file (always under %APPDATA%).</summary>
    public string RecentPath { get; }

    public RecentSettings Recent { get; private set; }

    /// <summary>The base folder for a storage location; its Config and Connections live here.</summary>
    private static string BaseDirFor(StorageLocation location) =>
        location == StorageLocation.ProgramDirectory ? ProgramDir : UserDir;

    private static string SettingsPathFor(StorageLocation location) =>
        Path.Combine(BaseDirFor(location), "Config", "settings.json");

    private static StorageLocation ResolveStartupLocation()
    {
        if (File.Exists(SettingsPathFor(StorageLocation.ProgramDirectory)))
            return StorageLocation.ProgramDirectory;

        if (File.Exists(SettingsPathFor(StorageLocation.UserDirectory)))
            return StorageLocation.UserDirectory;

        return Directory.Exists(Path.Combine(ProgramDir, "Connections"))
            ? StorageLocation.ProgramDirectory
            : StorageLocation.UserDirectory;
    }

    /// <summary>
    /// Moves a settings.json written by an older version (directly in the base
    /// folder) into the new Config subfolder, so existing settings are preserved
    /// across the upgrade.
    /// </summary>
    private void MigrateLegacySettingsFile(StorageLocation location)
    {
        try
        {
            var legacyPath = Path.Combine(BaseDirFor(location), "settings.json");
            var settingsPath = SettingsPathFor(location);
            if (File.Exists(legacyPath) && !File.Exists(settingsPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
                File.Move(legacyPath, settingsPath);
            }
        }
        catch
        {
            // If the move fails we just load whatever exists (or fall back to defaults).
        }
    }

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
    /// removes the settings file at the old location, while leaving connection
    /// data in place. Call before <see cref="Save"/>.
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

            // Remove the now-empty old Config folder so the old location is left clean.
            var oldDir = Path.GetDirectoryName(oldPath);
            if (oldDir != null && Directory.Exists(oldDir)
                && Directory.GetFileSystemEntries(oldDir).Length == 0)
                Directory.Delete(oldDir);
        }
        catch
        {
            // Leaving a stale settings file or folder behind is harmless.
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

    private RecentSettings LoadRecent()
    {
        try
        {
            if (File.Exists(RecentPath))
            {
                var json = File.ReadAllText(RecentPath);
                return JsonSerializer.Deserialize<RecentSettings>(json, JsonOptions) ?? new RecentSettings();
            }
        }
        catch
        {
            // Corrupt or unreadable: fall back to defaults.
        }

        return new RecentSettings();
    }

    /// <summary>
    /// On upgrade from a version that stored the recent list inside settings.json,
    /// move those values into the new per-user recent.json once.
    /// </summary>
    private void MigrateLegacyRecentFromSettings()
    {
        if (File.Exists(RecentPath))
            return;

        try
        {
            if (!File.Exists(SettingsPath))
                return;

            using var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            var root = doc.RootElement;
            var migrated = false;

            if (root.TryGetProperty("RecentConnectionPaths", out var pathsEl)
                && pathsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in pathsEl.EnumerateArray())
                {
                    if (p.ValueKind == JsonValueKind.String)
                        Recent.RecentConnectionPaths.Add(p.GetString()!);
                }
                migrated = true;
            }

            if (root.TryGetProperty("RecentExpanded", out var expEl)
                && (expEl.ValueKind == JsonValueKind.True || expEl.ValueKind == JsonValueKind.False))
            {
                Recent.RecentExpanded = expEl.GetBoolean();
                migrated = true;
            }

            if (migrated)
                SaveRecent();
        }
        catch
        {
            // Best-effort migration; defaults are fine if it fails.
        }
    }

    /// <summary>Persists the recent-connections list. Returns false if writing failed.</summary>
    public bool SaveRecent()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(RecentPath)!);
            var json = JsonSerializer.Serialize(Recent, JsonOptions);
            File.WriteAllText(RecentPath, json);
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
