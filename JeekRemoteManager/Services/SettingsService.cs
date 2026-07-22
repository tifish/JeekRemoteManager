using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using JeekRemoteManager.Models;
using JeekTools;

namespace JeekRemoteManager.Services;

/// <summary>
/// Loads and saves app settings split by roaming behavior, on top of the
/// generic <see cref="SettingsStorage"/> path scheme and
/// <see cref="JsonSettingsFile"/> merge/write machinery from JeekTools.
///
/// Machine-local state always lives under %LOCALAPPDATA%\JeekRemoteManager\Config.
/// Roaming preferences live in the active storage Config folder, alongside
/// connection and custom script data.
/// </summary>
public class SettingsService
{
    private static readonly SettingsStorage Storage = new("JeekRemoteManager");

    /// <summary>The machine-local settings file.</summary>
    public static string DefaultMachineSettingsPath => Storage.MachineSettingsPath;

    /// <summary>Compatibility alias for the machine-local settings file.</summary>
    public static string DefaultSettingsPath => DefaultMachineSettingsPath;

    /// <summary>The default roaming settings file under %APPDATA%.</summary>
    public static string DefaultRoamingSettingsPath => ResolveSettingsPath(StorageLocation.UserDirectory);

    /// <summary>True when startup will use the executable directory for roaming data.</summary>
    public static bool IsPortable => Storage.IsPortable;

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
            ?? ResolveSettingsPath(Storage.ResolveEffectiveLocation(machineSettings.StorageLocation), machineSettings.CustomStoragePath);
        var roamingSettings = LoadRoamingSettings(RoamingSettingsPath, fallbackRoamingSettings);
        NormalizeRoamingSettings(roamingSettings);

        var preMigrationMachineSettings = JsonSettingsFile.Clone(machineSettings);
        var migratedMachineSettings = TryAdoptLegacyRoamingLayoutSettings(
            machineSettings, MachineSettingsPath, RoamingSettingsPath);
        if (migratedMachineSettings is not null)
        {
            NormalizeMachineSettings(migratedMachineSettings);
            machineSettings = migratedMachineSettings;
        }

        Settings = MergeSettings(machineSettings, roamingSettings);
        NormalizeSettings(Settings);

        // After a migration the baseline stays at the pre-migration disk state so
        // the immediate save below writes the adopted values through the merge.
        _baseMachineSettings = migratedMachineSettings is not null
            ? preMigrationMachineSettings
            : ToMachineSettings(Settings);
        _baseRoamingSettings = ToRoamingSettings(Settings);
        _lastSavedMachineJson = JsonSettingsFile.Serialize(_baseMachineSettings);
        _lastSavedRoamingPath = CurrentRoamingSettingsPath();
        _lastSavedRoamingJson = JsonSettingsFile.Serialize(_baseRoamingSettings);

        if (migratedMachineSettings is not null)
            SaveIfChanged();
    }

    /// <summary>
    /// Machine-bound keys that historically lived in the roaming settings file
    /// (panel layout sizes, editor path, AI run modes). Used once at startup to
    /// carry existing values over to the machine-local file.
    /// </summary>
    private static readonly string[] LegacyRoamingLayoutKeys =
    [
        nameof(MachineAppSettings.AiPanelWidth),
        nameof(MachineAppSettings.FileBrowserPanelHeight),
        nameof(MachineAppSettings.MonitorPanelWidth),
        nameof(MachineAppSettings.FileBrowserEditorPath),
        nameof(MachineAppSettings.AiRunMode),
        nameof(MachineAppSettings.AiGrokRunMode),
        nameof(MachineAppSettings.AiHideSshTerminal),
    ];

    /// <summary>
    /// One-time upgrade for settings that moved from the roaming file to the
    /// machine-local file: any moved key the machine file does not define yet is
    /// adopted from the roaming file, so users keep their existing values.
    /// Returns the adopted settings, or null when nothing needed migrating.
    /// </summary>
    private static MachineAppSettings? TryAdoptLegacyRoamingLayoutSettings(
        MachineAppSettings machineSettings,
        string machineSettingsPath,
        string roamingSettingsPath)
    {
        var roamingJson = TryReadJsonObject(roamingSettingsPath);
        if (roamingJson is null)
            return null;

        var machineJson = TryReadJsonObject(machineSettingsPath);
        if (JsonSerializer.SerializeToNode(machineSettings, JsonSettingsFile.JsonOptions) is not JsonObject machineNode)
            return null;

        var adopted = false;
        foreach (var key in LegacyRoamingLayoutKeys)
        {
            if (machineJson?.ContainsKey(key) == true)
                continue;
            if (!roamingJson.TryGetPropertyValue(key, out var value) || value is null)
                continue;
            machineNode[key] = value.DeepClone();
            adopted = true;
        }

        if (!adopted)
            return null;

        try
        {
            return machineNode.Deserialize<MachineAppSettings>(JsonSettingsFile.JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static JsonObject? TryReadJsonObject(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;

            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Compatibility property for callers that report the settings path.</summary>
    public string SettingsPath => MachineSettingsPath;

    public string MachineSettingsPath { get; }

    public string RoamingSettingsPath { get; private set; }

    public AppSettings Settings { get; private set; }

    public long LastWriteTick { get; private set; }

    public StorageLocation CurrentStorageLocation =>
        Storage.ResolveEffectiveLocation(Settings.StorageLocation);

    private static AppSettings LoadAppSettings(string path, out bool loaded)
    {
        loaded = JsonSettingsFile.TryLoad(path, out AppSettings settings);
        return loaded ? settings : new AppSettings();
    }

    private static RoamingAppSettings LoadRoamingSettings(
        string path,
        RoamingAppSettings fallback)
    {
        if (JsonSettingsFile.TryLoad(path, out RoamingAppSettings roamingSettings))
            return roamingSettings;

        return fallback;
    }

    private static AppSettings MergeSettings(
        MachineAppSettings machineSettings,
        RoamingAppSettings roamingSettings) => new()
        {
            StorageLocation = machineSettings.StorageLocation,
            CustomStoragePath = machineSettings.CustomStoragePath,
            MainWindowWidth = machineSettings.MainWindowWidth,
            MainWindowHeight = machineSettings.MainWindowHeight,
            MainWindowX = machineSettings.MainWindowX,
            MainWindowY = machineSettings.MainWindowY,
            MainWindowMaximized = machineSettings.MainWindowMaximized,
            ConnectionPanelWidth = machineSettings.ConnectionPanelWidth,
            ConnectionPanelCollapsed = machineSettings.ConnectionPanelCollapsed,
            RecentConnectionPaths = machineSettings.RecentConnectionPaths,
            LastSelectedConnectionPath = machineSettings.LastSelectedConnectionPath,
            RecentExpanded = machineSettings.RecentExpanded,
            CollapsedFolderPaths = machineSettings.CollapsedFolderPaths,
            AiPanelWidth = machineSettings.AiPanelWidth,
            FileBrowserPanelHeight = machineSettings.FileBrowserPanelHeight,
            MonitorPanelWidth = machineSettings.MonitorPanelWidth,
            FileBrowserEditorPath = machineSettings.FileBrowserEditorPath,
            AiRunMode = machineSettings.AiRunMode,
            AiGrokRunMode = machineSettings.AiGrokRunMode,
            AiHideSshTerminal = machineSettings.AiHideSshTerminal,
            AiPanelOpen = machineSettings.AiPanelOpen,
            Language = roamingSettings.Language,
            Theme = roamingSettings.Theme,
            CheckUpdateOnStartup = roamingSettings.CheckUpdateOnStartup,
            UpdateCheckIntervalHours = roamingSettings.UpdateCheckIntervalHours,
            TerminalFontSize = roamingSettings.TerminalFontSize,
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
            MainWindowX = settings.MainWindowX,
            MainWindowY = settings.MainWindowY,
            MainWindowMaximized = settings.MainWindowMaximized,
            ConnectionPanelWidth = settings.ConnectionPanelWidth,
            ConnectionPanelCollapsed = settings.ConnectionPanelCollapsed,
            RecentConnectionPaths = settings.RecentConnectionPaths ?? new List<string>(),
            LastSelectedConnectionPath = settings.LastSelectedConnectionPath,
            RecentExpanded = settings.RecentExpanded,
            CollapsedFolderPaths = settings.CollapsedFolderPaths ?? new List<string>(),
            AiPanelWidth = settings.AiPanelWidth,
            FileBrowserPanelHeight = settings.FileBrowserPanelHeight,
            MonitorPanelWidth = settings.MonitorPanelWidth,
            FileBrowserEditorPath = settings.FileBrowserEditorPath,
            AiRunMode = settings.AiRunMode,
            AiGrokRunMode = settings.AiGrokRunMode,
            AiHideSshTerminal = settings.AiHideSshTerminal,
            AiPanelOpen = settings.AiPanelOpen,
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
        settings.MainWindowX = normalized.MainWindowX;
        settings.MainWindowY = normalized.MainWindowY;
        settings.MainWindowMaximized = normalized.MainWindowMaximized;
        settings.ConnectionPanelWidth = normalized.ConnectionPanelWidth;
        settings.ConnectionPanelCollapsed = normalized.ConnectionPanelCollapsed;
        settings.RecentConnectionPaths = normalized.RecentConnectionPaths;
        settings.LastSelectedConnectionPath = normalized.LastSelectedConnectionPath;
        settings.RecentExpanded = normalized.RecentExpanded;
        settings.CollapsedFolderPaths = normalized.CollapsedFolderPaths;
        settings.AiPanelWidth = normalized.AiPanelWidth;
        settings.FileBrowserPanelHeight = normalized.FileBrowserPanelHeight;
        settings.MonitorPanelWidth = normalized.MonitorPanelWidth;
        settings.FileBrowserEditorPath = normalized.FileBrowserEditorPath;
        settings.AiRunMode = normalized.AiRunMode;
        settings.AiGrokRunMode = normalized.AiGrokRunMode;
        settings.AiHideSshTerminal = normalized.AiHideSshTerminal;
        settings.AiPanelOpen = normalized.AiPanelOpen;
        settings.Language = normalized.Language;
        settings.Theme = normalized.Theme;
        settings.CheckUpdateOnStartup = normalized.CheckUpdateOnStartup;
        settings.UpdateCheckIntervalHours = normalized.UpdateCheckIntervalHours;
        settings.TerminalFontSize = normalized.TerminalFontSize;
        settings.AiProvider = normalized.AiProvider;
        settings.AiAutoRun = normalized.AiAutoRun;
        settings.AiAutoApproveDangerousCommands = normalized.AiAutoApproveDangerousCommands;
    }

    private static void NormalizeMachineSettings(MachineAppSettings settings)
    {
        settings.StorageLocation = Storage.NormalizeLocation(settings.StorageLocation);
        if (string.IsNullOrWhiteSpace(settings.CustomStoragePath))
            settings.CustomStoragePath = null;
        if (settings.StorageLocation == StorageLocation.CustomDirectory && settings.CustomStoragePath is null)
            settings.StorageLocation = StorageLocation.UserDirectory;
        if (settings.StorageLocation == StorageLocation.ProgramDirectory && !Storage.ProgramConfigRootExists())
            settings.StorageLocation = StorageLocation.UserDirectory;
        settings.RecentConnectionPaths ??= new List<string>();
        settings.CollapsedFolderPaths ??= new List<string>();
        if (string.IsNullOrWhiteSpace(settings.LastSelectedConnectionPath))
            settings.LastSelectedConnectionPath = null;
        if (!IsValidWindowDimension(settings.MainWindowWidth))
            settings.MainWindowWidth = null;
        if (!IsValidWindowDimension(settings.MainWindowHeight))
            settings.MainWindowHeight = null;
        // Position is only meaningful as a pair.
        if (settings.MainWindowX is null || settings.MainWindowY is null)
        {
            settings.MainWindowX = null;
            settings.MainWindowY = null;
        }
        settings.ConnectionPanelWidth = double.IsFinite(settings.ConnectionPanelWidth)
            ? Math.Clamp(settings.ConnectionPanelWidth, 180, 600)
            : 306;
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
        if (!Enum.IsDefined(settings.AiRunMode))
            settings.AiRunMode = AgentCliRunMode.Cli;
        // Grok has no Desktop protocol; never persist/restore Desktop for that slot.
        if (!Enum.IsDefined(settings.AiGrokRunMode) || settings.AiGrokRunMode == AgentCliRunMode.Desktop)
            settings.AiGrokRunMode = AgentCliRunMode.Cli;
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
        if (string.IsNullOrWhiteSpace(settings.AiProvider))
            settings.AiProvider = null;
    }

    private static bool IsValidWindowDimension(double? value) =>
        value is { } number && double.IsFinite(number) && number > 0;

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
        _lastSavedRoamingJson = JsonSettingsFile.Serialize(_baseRoamingSettings);
    }

    /// <summary>Persists changed settings to their machine-local and roaming files.</summary>
    public bool SaveIfChanged()
    {
        NormalizeSettings(Settings);

        var localMachine = ToMachineSettings(Settings);
        var localRoaming = ToRoamingSettings(Settings);
        var machineJson = JsonSettingsFile.Serialize(localMachine);
        var roamingPath = CurrentRoamingSettingsPath();
        var roamingJson = JsonSettingsFile.Serialize(localRoaming);

        var saved = true;
        var mergedMachine = localMachine;
        var mergedRoaming = localRoaming;

        if (!string.Equals(machineJson, _lastSavedMachineJson, StringComparison.Ordinal))
        {
            var machineSaved = JsonSettingsFile.TryMergeAndWrite<MachineAppSettings>(
                MachineSettingsPath, _baseMachineSettings, localMachine,
                NormalizeMachineSettings, forceAllLocal: false, out mergedMachine);
            saved &= machineSaved;
            if (machineSaved)
            {
                _baseMachineSettings = mergedMachine;
                _lastSavedMachineJson = JsonSettingsFile.Serialize(mergedMachine);
            }
        }

        var roamingPathChanged = !string.Equals(
            roamingPath, _lastSavedRoamingPath, StringComparison.OrdinalIgnoreCase);
        if (roamingPathChanged || !string.Equals(roamingJson, _lastSavedRoamingJson, StringComparison.Ordinal))
        {
            var roamingSaved = JsonSettingsFile.TryMergeAndWrite<RoamingAppSettings>(
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
                _lastSavedRoamingJson = JsonSettingsFile.Serialize(mergedRoaming);
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
        Path.Combine(Storage.ProgramDir, "Data", "Scripts");

    /// <summary>Resolves the Config folder for a given storage location.</summary>
    public static string ResolveConfigRoot(StorageLocation location, string? customPath = null) =>
        Storage.ResolveConfigRoot(location, customPath);

    /// <summary>Resolves settings.json under the Config folder for a given storage location.</summary>
    public static string ResolveSettingsPath(StorageLocation location, string? customPath = null) =>
        Storage.ResolveSettingsPath(location, customPath);

    /// <summary>Resolves the connections root folder for a given storage location.
    /// <paramref name="customPath"/> is the base directory used when
    /// <paramref name="location"/> is <see cref="StorageLocation.CustomDirectory"/>.</summary>
    public static string ResolveConnectionsRoot(StorageLocation location, string? customPath = null) =>
        Path.Combine(Storage.ResolveConfigRoot(location, customPath), "Connections");

    /// <summary>Resolves the script-suite root folder for a given storage location.
    /// <paramref name="customPath"/> is the base directory used when
    /// <paramref name="location"/> is <see cref="StorageLocation.CustomDirectory"/>.</summary>
    public static string ResolveScriptsRoot(StorageLocation location, string? customPath = null) =>
        Path.Combine(Storage.ResolveConfigRoot(location, customPath), "Scripts");

    /// <summary>
    /// Moves the whole roaming Config folder to a new root. Existing destination
    /// files with the same relative paths are replaced by the current Config.
    /// </summary>
    public static void MoveConfigRoot(string sourceRoot, string destRoot) =>
        SettingsStorage.MoveConfigRoot(sourceRoot, destRoot);

    /// <summary>Deletes the executable-side Config folder after leaving portable mode.</summary>
    public static bool TryDeleteProgramConfig(out string? error) =>
        Storage.TryDeleteProgramConfig(out error);
}
