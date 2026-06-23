using System.Collections.Generic;

namespace JeekRemoteManager.Models;

/// <summary>Where connection files are stored.</summary>
public enum StorageLocation
{
    /// <summary>Under %APPDATA%\JeekRemoteManager\Config\Connections (roaming, per-user).</summary>
    UserDirectory,

    /// <summary>Under a "Config\Connections" folder next to the executable (portable).</summary>
    ProgramDirectory,

    /// <summary>Under a "Config\Connections" folder beneath a user-chosen base directory.</summary>
    CustomDirectory,
}

/// <summary>Persisted application settings.</summary>
public class AppSettings
{
    public StorageLocation StorageLocation { get; set; } = StorageLocation.UserDirectory;

    /// <summary>Base directory for <see cref="StorageLocation.CustomDirectory"/>.
    /// Connection and script data live under "Config\Connections"/"Config\Scripts"
    /// subfolders of this path. Null/blank when no custom location has been chosen.</summary>
    public string? CustomStoragePath { get; set; }

    /// <summary>UI language code ("en", "zh"). Null = follow system culture.</summary>
    public string? Language { get; set; }

    /// <summary>UI theme ("Light", "Dark"). Null = follow system theme.</summary>
    public string? Theme { get; set; }

    /// <summary>Main window width in device-independent pixels. Null = default size.</summary>
    public double? MainWindowWidth { get; set; }

    /// <summary>Main window height in device-independent pixels. Null = default size.</summary>
    public double? MainWindowHeight { get; set; }

    /// <summary>Whether to silently check for updates a few seconds after launch.</summary>
    public bool CheckUpdateOnStartup { get; set; } = true;

    /// <summary>
    /// How often to check for updates while the app is running, in hours.
    /// 0 disables the periodic check; the startup check is independent.
    /// </summary>
    public int UpdateCheckIntervalHours { get; set; } = 24;

    // Saved connection passwords are self-contained jrm1 blobs in the connection
    // files. The DPAPI cache managed by MasterKeyService is machine-local only
    // and is intentionally not stored here.

    /// <summary>Recently-used connection file paths, most-recent first.</summary>
    public List<string> RecentConnectionPaths { get; set; } = new();

    /// <summary>Connection file path that was selected when the app last ran.</summary>
    public string? LastSelectedConnectionPath { get; set; }

    /// <summary>Whether the "Recent" group at the top of the tree is expanded.</summary>
    public bool RecentExpanded { get; set; } = true;

    /// <summary>Absolute paths of folders the user has collapsed in the tree.
    /// Folders not listed here default to expanded, so the set stays small and
    /// new folders appear open. Restored on startup to persist the tree's
    /// expand/collapse state across runs.</summary>
    public List<string> CollapsedFolderPaths { get; set; } = new();
}

/// <summary>Outcome of the Settings dialog. <see cref="Language"/> and <see cref="Theme"/>
/// are null when "follow system" is chosen.</summary>
public record SettingsDialogResult(
    StorageLocation StorageLocation,
    string? CustomStoragePath,
    string? Language,
    string? Theme,
    bool CheckUpdateOnStartup,
    int UpdateCheckIntervalHours);
