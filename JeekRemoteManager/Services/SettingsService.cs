using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using JeekRemoteManager.Models;

namespace JeekRemoteManager.Services;

/// <summary>
/// Loads and saves app settings split by roaming behavior.
///
/// Machine-local state always lives under %LOCALAPPDATA%\JeekRemoteManager\Config.
/// Roaming preferences live in the active storage Config folder, alongside
/// connection and custom script data.
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

    /// <summary>The machine-local settings file.</summary>
    public static string DefaultMachineSettingsPath => Path.Combine(LocalConfigDir, "settings.json");

    /// <summary>Compatibility alias for the machine-local settings file.</summary>
    public static string DefaultSettingsPath => DefaultMachineSettingsPath;

    /// <summary>The default roaming settings file under %APPDATA%.</summary>
    public static string DefaultRoamingSettingsPath => ResolveSettingsPath(StorageLocation.UserDirectory);

    /// <summary>True when startup will use the executable directory for roaming data.</summary>
    public static bool IsPortable => ProgramConfigRootExists();

    private string _lastSavedMachineJson;
    private string _lastSavedRoamingJson;
    private string _lastSavedRoamingPath;
    private MachineAppSettings _baseMachineSettings;
    private RoamingAppSettings _baseRoamingSettings;
    private readonly string? _roamingSettingsPathOverride;

    public SettingsService(string? machineSettingsPath = null, string? roamingSettingsPath = null)
    {
        MachineSettingsPath = machineSettingsPath ?? DefaultMachineSettingsPath;

        var legacySettings = LoadAppSettings(MachineSettingsPath, out var machineFileLoaded);
        var machineSettings = machineFileLoaded
            ? ToMachineSettings(legacySettings)
            : new MachineAppSettings();
        NormalizeMachineSettings(machineSettings);

        var fallbackRoamingSettings = machineFileLoaded
            ? ToRoamingSettings(legacySettings)
            : new RoamingAppSettings();

        _roamingSettingsPathOverride = roamingSettingsPath;
        RoamingSettingsPath = roamingSettingsPath
            ?? ResolveSettingsPath(ResolveEffectiveStorageLocation(machineSettings.StorageLocation), machineSettings.CustomStoragePath);
        var roamingSettings = LoadRoamingSettings(RoamingSettingsPath, fallbackRoamingSettings);
        NormalizeRoamingSettings(roamingSettings);

        Settings = MergeSettings(machineSettings, roamingSettings);
        NormalizeSettings(Settings);

        _baseMachineSettings = ToMachineSettings(Settings);
        _baseRoamingSettings = ToRoamingSettings(Settings);
        _lastSavedMachineJson = Serialize(_baseMachineSettings);
        _lastSavedRoamingPath = CurrentRoamingSettingsPath();
        _lastSavedRoamingJson = Serialize(_baseRoamingSettings);
    }

    /// <summary>Compatibility property for callers that report the settings path.</summary>
    public string SettingsPath => MachineSettingsPath;

    public string MachineSettingsPath { get; }

    public string RoamingSettingsPath { get; private set; }

    public AppSettings Settings { get; private set; }

    public long LastWriteTick { get; private set; }

    public StorageLocation CurrentStorageLocation =>
        ResolveEffectiveStorageLocation(Settings.StorageLocation);

    private static string StorageBaseDirFor(StorageLocation location, string? customPath) => location switch
    {
        StorageLocation.ProgramDirectory => ProgramConfigDir,
        StorageLocation.CustomDirectory when !string.IsNullOrWhiteSpace(customPath) =>
            Path.Combine(customPath!, "Config"),
        _ => RoamingConfigDir,
    };

    private static AppSettings LoadAppSettings(string path, out bool loaded)
    {
        loaded = TryLoadSettingsFile(path, out AppSettings settings);
        return loaded ? settings : new AppSettings();
    }

    private static RoamingAppSettings LoadRoamingSettings(
        string path,
        RoamingAppSettings fallback)
    {
        if (TryLoadSettingsFile(path, out RoamingAppSettings roamingSettings))
            return roamingSettings;

        return fallback;
    }

    private static bool TryLoadSettingsFile<T>(string path, out T settings)
        where T : new()
    {
        settings = new T();

        try
        {
            if (!File.Exists(path))
                return false;

            settings = JsonSerializer.Deserialize<T>(
                File.ReadAllText(path),
                JsonOptions) ?? new T();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static AppSettings MergeSettings(
        MachineAppSettings machineSettings,
        RoamingAppSettings roamingSettings) => new()
        {
            StorageLocation = machineSettings.StorageLocation,
            CustomStoragePath = machineSettings.CustomStoragePath,
            MainWindowWidth = machineSettings.MainWindowWidth,
            MainWindowHeight = machineSettings.MainWindowHeight,
            ConnectionPanelWidth = machineSettings.ConnectionPanelWidth,
            RecentConnectionPaths = machineSettings.RecentConnectionPaths,
            LastSelectedConnectionPath = machineSettings.LastSelectedConnectionPath,
            RecentExpanded = machineSettings.RecentExpanded,
            CollapsedFolderPaths = machineSettings.CollapsedFolderPaths,
            Language = roamingSettings.Language,
            Theme = roamingSettings.Theme,
            CheckUpdateOnStartup = roamingSettings.CheckUpdateOnStartup,
            UpdateCheckIntervalHours = roamingSettings.UpdateCheckIntervalHours,
            TerminalFontSize = roamingSettings.TerminalFontSize,
            AiPanelWidth = roamingSettings.AiPanelWidth,
            FileBrowserPanelHeight = roamingSettings.FileBrowserPanelHeight,
            MonitorPanelWidth = roamingSettings.MonitorPanelWidth,
            FileBrowserEditorPath = roamingSettings.FileBrowserEditorPath,
            AiProvider = roamingSettings.AiProvider,
            AiAutoRun = roamingSettings.AiAutoRun,
            AiAutoApproveDangerousCommands = roamingSettings.AiAutoApproveDangerousCommands,
        };

    private static MachineAppSettings ToMachineSettings(AppSettings settings)
    {
        var machineSettings = new MachineAppSettings
        {
            StorageLocation = settings.StorageLocation,
            CustomStoragePath = settings.CustomStoragePath,
            MainWindowWidth = settings.MainWindowWidth,
            MainWindowHeight = settings.MainWindowHeight,
            ConnectionPanelWidth = settings.ConnectionPanelWidth,
            RecentConnectionPaths = settings.RecentConnectionPaths ?? new List<string>(),
            LastSelectedConnectionPath = settings.LastSelectedConnectionPath,
            RecentExpanded = settings.RecentExpanded,
            CollapsedFolderPaths = settings.CollapsedFolderPaths ?? new List<string>(),
        };
        NormalizeMachineSettings(machineSettings);
        return machineSettings;
    }

    private static RoamingAppSettings ToRoamingSettings(AppSettings settings)
    {
        var roamingSettings = new RoamingAppSettings
        {
            Language = settings.Language,
            Theme = settings.Theme,
            CheckUpdateOnStartup = settings.CheckUpdateOnStartup,
            UpdateCheckIntervalHours = settings.UpdateCheckIntervalHours,
            TerminalFontSize = settings.TerminalFontSize,
            AiPanelWidth = settings.AiPanelWidth,
            FileBrowserPanelHeight = settings.FileBrowserPanelHeight,
            MonitorPanelWidth = settings.MonitorPanelWidth,
            FileBrowserEditorPath = settings.FileBrowserEditorPath,
            AiProvider = settings.AiProvider,
            AiAutoRun = settings.AiAutoRun,
            AiAutoApproveDangerousCommands = settings.AiAutoApproveDangerousCommands,
        };
        NormalizeRoamingSettings(roamingSettings);
        return roamingSettings;
    }

    private static void NormalizeSettings(AppSettings settings)
    {
        var machineSettings = ToMachineSettings(settings);
        var roamingSettings = ToRoamingSettings(settings);
        var normalized = MergeSettings(machineSettings, roamingSettings);

        settings.StorageLocation = normalized.StorageLocation;
        settings.CustomStoragePath = normalized.CustomStoragePath;
        settings.MainWindowWidth = normalized.MainWindowWidth;
        settings.MainWindowHeight = normalized.MainWindowHeight;
        settings.ConnectionPanelWidth = normalized.ConnectionPanelWidth;
        settings.RecentConnectionPaths = normalized.RecentConnectionPaths;
        settings.LastSelectedConnectionPath = normalized.LastSelectedConnectionPath;
        settings.RecentExpanded = normalized.RecentExpanded;
        settings.CollapsedFolderPaths = normalized.CollapsedFolderPaths;
        settings.Language = normalized.Language;
        settings.Theme = normalized.Theme;
        settings.CheckUpdateOnStartup = normalized.CheckUpdateOnStartup;
        settings.UpdateCheckIntervalHours = normalized.UpdateCheckIntervalHours;
        settings.TerminalFontSize = normalized.TerminalFontSize;
        settings.AiPanelWidth = normalized.AiPanelWidth;
        settings.FileBrowserPanelHeight = normalized.FileBrowserPanelHeight;
        settings.MonitorPanelWidth = normalized.MonitorPanelWidth;
        settings.FileBrowserEditorPath = normalized.FileBrowserEditorPath;
        settings.AiProvider = normalized.AiProvider;
        settings.AiAutoRun = normalized.AiAutoRun;
        settings.AiAutoApproveDangerousCommands = normalized.AiAutoApproveDangerousCommands;
    }

    private static void NormalizeMachineSettings(MachineAppSettings settings)
    {
        settings.StorageLocation = NormalizeStorageLocation(settings.StorageLocation);
        if (string.IsNullOrWhiteSpace(settings.CustomStoragePath))
            settings.CustomStoragePath = null;
        if (settings.StorageLocation == StorageLocation.CustomDirectory && settings.CustomStoragePath is null)
            settings.StorageLocation = StorageLocation.UserDirectory;
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
        settings.ConnectionPanelWidth = double.IsFinite(settings.ConnectionPanelWidth)
            ? Math.Clamp(settings.ConnectionPanelWidth, 180, 600)
            : 306;
    }

    private static void NormalizeRoamingSettings(RoamingAppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Language))
            settings.Language = null;
        if (string.IsNullOrWhiteSpace(settings.Theme))
            settings.Theme = null;
        if (settings.UpdateCheckIntervalHours < 0)
            settings.UpdateCheckIntervalHours = 0;
        settings.TerminalFontSize = Math.Clamp(settings.TerminalFontSize, 8, 36);
        settings.AiPanelWidth = double.IsFinite(settings.AiPanelWidth)
            ? Math.Clamp(settings.AiPanelWidth, 240, 1200)
            : 380;
        settings.FileBrowserPanelHeight = double.IsFinite(settings.FileBrowserPanelHeight)
            ? Math.Clamp(settings.FileBrowserPanelHeight, 120, 1600)
            : 260;
        settings.MonitorPanelWidth = double.IsFinite(settings.MonitorPanelWidth)
            ? Math.Clamp(settings.MonitorPanelWidth, 180, 600)
            : 260;
        if (string.IsNullOrWhiteSpace(settings.FileBrowserEditorPath))
            settings.FileBrowserEditorPath = null;
        if (string.IsNullOrWhiteSpace(settings.AiProvider))
            settings.AiProvider = null;
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

    private static string Serialize<T>(T settings) =>
        JsonSerializer.Serialize(settings, JsonOptions);

    private void Touch() => LastWriteTick = Environment.TickCount64;

    private string CurrentRoamingSettingsPath() =>
        _roamingSettingsPathOverride
        ?? ResolveSettingsPath(CurrentStorageLocation, Settings.CustomStoragePath);

    /// <summary>Reloads the roaming settings file from the active storage Config folder.</summary>
    public void ReloadRoamingSettings()
    {
        NormalizeSettings(Settings);
        var machineSettings = ToMachineSettings(Settings);
        var path = CurrentRoamingSettingsPath();
        var roamingSettings = LoadRoamingSettings(path, ToRoamingSettings(Settings));
        NormalizeRoamingSettings(roamingSettings);

        Settings = MergeSettings(machineSettings, roamingSettings);
        NormalizeSettings(Settings);
        RoamingSettingsPath = CurrentRoamingSettingsPath();
        _lastSavedRoamingPath = RoamingSettingsPath;
        _baseRoamingSettings = ToRoamingSettings(Settings);
        _lastSavedRoamingJson = Serialize(_baseRoamingSettings);
    }

    /// <summary>Persists changed settings to their machine-local and roaming files.</summary>
    public bool SaveIfChanged()
    {
        NormalizeSettings(Settings);

        var localMachine = ToMachineSettings(Settings);
        var localRoaming = ToRoamingSettings(Settings);
        var machineJson = Serialize(localMachine);
        var roamingPath = CurrentRoamingSettingsPath();
        var roamingJson = Serialize(localRoaming);

        var saved = true;
        var mergedMachine = localMachine;
        var mergedRoaming = localRoaming;

        if (!string.Equals(machineJson, _lastSavedMachineJson, StringComparison.Ordinal))
        {
            var machineSaved = TryMergeAndWrite<MachineAppSettings>(
                MachineSettingsPath, _baseMachineSettings, localMachine,
                NormalizeMachineSettings, forceAllLocal: false, out mergedMachine);
            saved &= machineSaved;
            if (machineSaved)
            {
                _baseMachineSettings = mergedMachine;
                _lastSavedMachineJson = Serialize(mergedMachine);
            }
        }

        var roamingPathChanged = !string.Equals(
            roamingPath, _lastSavedRoamingPath, StringComparison.OrdinalIgnoreCase);
        if (roamingPathChanged || !string.Equals(roamingJson, _lastSavedRoamingJson, StringComparison.Ordinal))
        {
            var roamingSaved = TryMergeAndWrite<RoamingAppSettings>(
                roamingPath, _baseRoamingSettings, localRoaming,
                NormalizeRoamingSettings,
                forceAllLocal: roamingPathChanged && !File.Exists(roamingPath),
                out mergedRoaming);
            saved &= roamingSaved;
            if (roamingSaved)
            {
                RoamingSettingsPath = roamingPath;
                _lastSavedRoamingPath = roamingPath;
                _baseRoamingSettings = mergedRoaming;
                _lastSavedRoamingJson = Serialize(mergedRoaming);
            }
        }

        if (saved)
        {
            Settings = MergeSettings(mergedMachine, mergedRoaming);
            NormalizeSettings(Settings);
            Touch();
        }
        return saved;
    }

    private static bool TryMergeAndWrite<T>(
        string path,
        T baseline,
        T local,
        Action<T> normalize,
        bool forceAllLocal,
        out T merged)
        where T : class, new()
    {
        merged = local;
        try
        {
            using var lease = SharedDataFile.Acquire(path);
            var latest = TryLoadSettingsFile(path, out T disk) ? disk : Clone(baseline);
            normalize(latest);

            if (forceAllLocal)
            {
                merged = Clone(local);
            }
            else
            {
                var baselineNode = JsonSerializer.SerializeToNode(baseline, JsonOptions) as JsonObject ?? new();
                var localNode = JsonSerializer.SerializeToNode(local, JsonOptions) as JsonObject ?? new();
                var resultNode = JsonSerializer.SerializeToNode(latest, JsonOptions) as JsonObject ?? new();
                foreach (var property in localNode)
                {
                    baselineNode.TryGetPropertyValue(property.Key, out var baselineValue);
                    if (!JsonNode.DeepEquals(property.Value, baselineValue))
                        resultNode[property.Key] = property.Value?.DeepClone();
                }
                merged = resultNode.Deserialize<T>(JsonOptions) ?? new T();
            }

            normalize(merged);
            SharedDataFile.WriteAllTextAtomic(path, Serialize(merged));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static T Clone<T>(T value) where T : class, new() =>
        JsonSerializer.Deserialize<T>(Serialize(value), JsonOptions) ?? new T();

    /// <summary>Resolves the active Config folder for roaming settings and user data.</summary>
    public string ResolveConfigRoot() =>
        ResolveConfigRoot(CurrentStorageLocation, Settings.CustomStoragePath);

    /// <summary>Resolves the connections root folder for the current setting.</summary>
    public string ResolveConnectionsRoot() =>
        ResolveConnectionsRoot(CurrentStorageLocation, Settings.CustomStoragePath);

    /// <summary>Resolves the script-suite root folder for the current setting.</summary>
    public string ResolveScriptsRoot() =>
        ResolveScriptsRoot(CurrentStorageLocation, Settings.CustomStoragePath);

    /// <summary>Resolves the app-bundled script-suite root folder.</summary>
    public static string ResolveBuiltInScriptsRoot() =>
        Path.Combine(ProgramDir, "Data", "Scripts");

    /// <summary>Resolves the Config folder for a given storage location.</summary>
    public static string ResolveConfigRoot(StorageLocation location, string? customPath = null) =>
        StorageBaseDirFor(location, customPath);

    /// <summary>Resolves settings.json under the Config folder for a given storage location.</summary>
    public static string ResolveSettingsPath(StorageLocation location, string? customPath = null) =>
        Path.Combine(ResolveConfigRoot(location, customPath), "settings.json");

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

    /// <summary>
    /// Moves the whole roaming Config folder to a new root. Existing destination
    /// files with the same relative paths are replaced by the current Config.
    /// </summary>
    public static void MoveConfigRoot(string sourceRoot, string destRoot)
    {
        var source = NormalizeDirectoryPath(sourceRoot);
        var dest = NormalizeDirectoryPath(destRoot);
        using var lease = SharedDataFile.AcquireMany(source, dest);
        if (string.Equals(source, dest, StringComparison.OrdinalIgnoreCase))
            return;

        if (IsSameOrInside(source, dest) || IsSameOrInside(dest, source))
            throw new InvalidOperationException("Config cannot be moved into itself or a nested folder.");

        if (!Directory.Exists(source))
        {
            Directory.CreateDirectory(dest);
            return;
        }

        var destParent = Path.GetDirectoryName(dest);
        if (!string.IsNullOrEmpty(destParent))
            Directory.CreateDirectory(destParent);

        if (!Directory.Exists(dest) && TryRenameDirectory(source, dest))
            return;

        MoveDirectoryContents(source, dest);
        Directory.Delete(source, recursive: true);
    }

    /// <summary>Deletes the executable-side Config folder after leaving portable mode.</summary>
    public static bool TryDeleteProgramConfig(out string? error)
    {
        error = null;
        try
        {
            var configRoot = Path.GetFullPath(ProgramConfigDir).TrimEnd(Path.DirectorySeparatorChar);
            var programRoot = Path.GetFullPath(ProgramDir).TrimEnd(Path.DirectorySeparatorChar);
            if (!configRoot.StartsWith(programRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                error = "Program Config path is outside the executable directory.";
                return false;
            }

            if (Directory.Exists(configRoot))
                Directory.Delete(configRoot, recursive: true);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string NormalizeDirectoryPath(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool IsSameOrInside(string folder, string candidate)
    {
        if (string.Equals(folder, candidate, StringComparison.OrdinalIgnoreCase))
            return true;

        return candidate.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static void MoveDirectoryContents(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var target = Path.Combine(destDir, Path.GetFileName(file));
            if (File.Exists(target))
                File.Delete(target);
            File.Move(file, target);
        }

        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            var target = Path.Combine(destDir, Path.GetFileName(dir.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar)));
            if (!Directory.Exists(target) && TryRenameDirectory(dir, target))
                continue;

            MoveDirectoryContents(dir, target);
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// Attempts a fast same-volume directory rename. Returns <c>false</c> when the
    /// move crosses drives (<see cref="Directory.Move"/> cannot move across
    /// volumes), so the caller falls back to a recursive copy+delete.
    /// </summary>
    private static bool TryRenameDirectory(string source, string dest)
    {
        try
        {
            Directory.Move(source, dest);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
