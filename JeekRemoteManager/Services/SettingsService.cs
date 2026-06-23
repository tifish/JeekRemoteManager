using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using JeekRemoteManager.Models;

namespace JeekRemoteManager.Services;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as a machine-local JSON file.
///
/// settings.json always lives under %LOCALAPPDATA%\JeekRemoteManager\Config
/// because it stores preferences for this Windows account and machine. The
/// storage-location setting only controls where connection and custom script
/// data live.
/// </summary>
public class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static string ProgramDir => AppContext.BaseDirectory;

    private static string LocalDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JeekRemoteManager");

    private static string LocalConfigDir => Path.Combine(LocalDir, "Config");

    private static string RoamingDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "JeekRemoteManager");

    private static string RoamingConfigDir => Path.Combine(RoamingDir, "Config");

    private static string ProgramConfigDir => Path.Combine(ProgramDir, "Config");

    /// <summary>The single supported location for app settings.</summary>
    public static string DefaultSettingsPath => Path.Combine(LocalConfigDir, "settings.json");

    /// <summary>True when startup will use the executable directory for user data.</summary>
    public static bool IsPortable => ProgramConfigRootExists();

    private string _lastSavedJson;

    public SettingsService(string? settingsPath = null)
    {
        SettingsPath = settingsPath ?? DefaultSettingsPath;
        Settings = Load(SettingsPath);
        NormalizeSettings(Settings);
        _lastSavedJson = Serialize(Settings);
    }

    public string SettingsPath { get; }

    public AppSettings Settings { get; private set; }

    public StorageLocation CurrentStorageLocation =>
        ResolveEffectiveStorageLocation(Settings.StorageLocation);

    private static string StorageBaseDirFor(StorageLocation location, string? customPath) => location switch
    {
        StorageLocation.ProgramDirectory => ProgramConfigDir,
        StorageLocation.CustomDirectory when !string.IsNullOrWhiteSpace(customPath) =>
            Path.Combine(customPath!, "Config"),
        _ => RoamingConfigDir,
    };

    private static AppSettings Load(string path)
    {
        if (TryLoadSettingsFile(path, out var settings))
            return settings;

        return new AppSettings();
    }

    private static bool TryLoadSettingsFile(string path, out AppSettings settings)
    {
        settings = new AppSettings();

        try
        {
            if (!File.Exists(path))
                return false;

            settings = JsonSerializer.Deserialize<AppSettings>(
                File.ReadAllText(path),
                JsonOptions) ?? new AppSettings();
            NormalizeSettings(settings);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void NormalizeSettings(AppSettings settings)
    {
        settings.StorageLocation = NormalizeStorageLocation(settings.StorageLocation);
        if (string.IsNullOrWhiteSpace(settings.CustomStoragePath))
            settings.CustomStoragePath = null;
        // A custom location without a usable path falls back to the user directory.
        if (settings.StorageLocation == StorageLocation.CustomDirectory && settings.CustomStoragePath is null)
            settings.StorageLocation = StorageLocation.UserDirectory;
        // ProgramDirectory is valid only when the app-side Config marker exists.
        // Otherwise a stale setting would create the marker during startup and
        // make the app portable forever.
        if (settings.StorageLocation == StorageLocation.ProgramDirectory && !ProgramConfigRootExists())
            settings.StorageLocation = StorageLocation.UserDirectory;
        settings.RecentConnectionPaths ??= new List<string>();
        settings.CollapsedFolderPaths ??= new List<string>();
        if (string.IsNullOrWhiteSpace(settings.LastSelectedConnectionPath))
            settings.LastSelectedConnectionPath = null;
        if (!IsValidWindowDimension(settings.MainWindowWidth))
            settings.MainWindowWidth = null;
        if (!IsValidWindowDimension(settings.MainWindowHeight))
            settings.MainWindowHeight = null;
    }

    private static StorageLocation NormalizeStorageLocation(StorageLocation location) =>
        Enum.IsDefined(location) ? location : StorageLocation.UserDirectory;

    private static StorageLocation ResolveEffectiveStorageLocation(StorageLocation location)
    {
        if (ProgramConfigRootExists())
            return StorageLocation.ProgramDirectory;

        var normalized = NormalizeStorageLocation(location);
        return normalized == StorageLocation.ProgramDirectory
            ? StorageLocation.UserDirectory
            : normalized;
    }

    private static bool IsValidWindowDimension(double? value) =>
        value is { } number && double.IsFinite(number) && number > 0;

    private static bool ProgramConfigRootExists()
    {
        try
        {
            return Directory.Exists(ProgramConfigDir);
        }
        catch
        {
            return false;
        }
    }

    private static string Serialize(AppSettings settings) =>
        JsonSerializer.Serialize(settings, JsonOptions);

    /// <summary>Persists the current settings only when they differ from the last saved snapshot.</summary>
    public bool SaveIfChanged()
    {
        NormalizeSettings(Settings);
        var json = Serialize(Settings);
        if (string.Equals(json, _lastSavedJson, StringComparison.Ordinal))
            return true;

        try
        {
            var directory = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllText(SettingsPath, json);
            _lastSavedJson = json;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Resolves the connections root folder for the current setting.</summary>
    public string ResolveConnectionsRoot() =>
        ResolveConnectionsRoot(CurrentStorageLocation, Settings.CustomStoragePath);

    /// <summary>Resolves the script-suite root folder for the current setting.</summary>
    public string ResolveScriptsRoot() =>
        ResolveScriptsRoot(CurrentStorageLocation, Settings.CustomStoragePath);

    /// <summary>Resolves the app-bundled script-suite root folder.</summary>
    public static string ResolveBuiltInScriptsRoot() =>
        Path.Combine(ProgramDir, "Data", "Scripts");

    /// <summary>Resolves the connections root folder for a given storage location.
    /// <paramref name="customPath"/> is the base directory used when
    /// <paramref name="location"/> is <see cref="StorageLocation.CustomDirectory"/>.</summary>
    public static string ResolveConnectionsRoot(StorageLocation location, string? customPath = null) =>
        Path.Combine(StorageBaseDirFor(location, customPath), "Connections");

    /// <summary>Resolves the script-suite root folder for a given storage location.
    /// <paramref name="customPath"/> is the base directory used when
    /// <paramref name="location"/> is <see cref="StorageLocation.CustomDirectory"/>.</summary>
    public static string ResolveScriptsRoot(StorageLocation location, string? customPath = null) =>
        Path.Combine(StorageBaseDirFor(location, customPath), "Scripts");
}
