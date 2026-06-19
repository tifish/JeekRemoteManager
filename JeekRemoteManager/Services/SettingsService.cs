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
/// settings.json always lives under %LOCALAPPDATA%\JeekRemoteManager because it
/// stores preferences for this Windows account and machine. The storage-location
/// setting only controls where connection and custom script data live.
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

    private static string RoamingDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "JeekRemoteManager");

    /// <summary>The single supported location for app settings.</summary>
    public static string DefaultSettingsPath => Path.Combine(LocalDir, "settings.json");

    /// <summary>True when startup will use the executable directory for connection data.</summary>
    public static bool IsPortable => LoadStorageLocation() == StorageLocation.ProgramDirectory;

    public SettingsService()
    {
        SettingsPath = DefaultSettingsPath;
        Settings = Load();
        NormalizeSettings(Settings);
    }

    public string SettingsPath { get; }

    public AppSettings Settings { get; private set; }

    private static string StorageBaseDirFor(StorageLocation location, string? customPath) => location switch
    {
        StorageLocation.ProgramDirectory => ProgramDir,
        StorageLocation.CustomDirectory when !string.IsNullOrWhiteSpace(customPath) => customPath!,
        _ => RoamingDir,
    };

    private static StorageLocation LoadStorageLocation()
    {
        if (TryLoadSettingsFile(DefaultSettingsPath, out var settings))
            return NormalizeStorageLocation(settings.StorageLocation);

        return StorageLocation.UserDirectory;
    }

    private static AppSettings Load()
    {
        if (TryLoadSettingsFile(DefaultSettingsPath, out var settings))
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
        settings.RecentConnectionPaths ??= new List<string>();
        if (string.IsNullOrWhiteSpace(settings.LastSelectedConnectionPath))
            settings.LastSelectedConnectionPath = null;
        if (!IsValidWindowDimension(settings.MainWindowWidth))
            settings.MainWindowWidth = null;
        if (!IsValidWindowDimension(settings.MainWindowHeight))
            settings.MainWindowHeight = null;
    }

    private static StorageLocation NormalizeStorageLocation(StorageLocation location) =>
        Enum.IsDefined(location) ? location : StorageLocation.UserDirectory;

    private static bool IsValidWindowDimension(double? value) =>
        value is { } number && double.IsFinite(number) && number > 0;

    /// <summary>Persists the current settings. Returns false if writing failed.</summary>
    public bool Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            NormalizeSettings(Settings);
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
    public string ResolveConnectionsRoot() =>
        ResolveConnectionsRoot(Settings.StorageLocation, Settings.CustomStoragePath);

    /// <summary>Resolves the script-suite root folder for the current setting.</summary>
    public string ResolveScriptsRoot() =>
        ResolveScriptsRoot(Settings.StorageLocation, Settings.CustomStoragePath);

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
