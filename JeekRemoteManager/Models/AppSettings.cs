using System.Collections.Generic;

namespace JeekRemoteManager.Models;

/// <summary>Where connection files are stored.</summary>
public enum StorageLocation
{
    /// <summary>Under %APPDATA%\JeekRemoteManager\Connections (roaming, per-user).</summary>
    UserDirectory,

    /// <summary>Under a "Connections" folder next to the executable (portable).</summary>
    ProgramDirectory,
}

/// <summary>Persisted application settings.</summary>
public class AppSettings
{
    public StorageLocation StorageLocation { get; set; } = StorageLocation.UserDirectory;

    /// <summary>UI language code ("en", "zh"). Null = follow system culture.</summary>
    public string? Language { get; set; }

    /// <summary>UI theme ("Light", "Dark"). Null = follow system theme.</summary>
    public string? Theme { get; set; }

    /// <summary>Whether to silently check for updates a few seconds after launch.</summary>
    public bool CheckUpdateOnStartup { get; set; } = true;

    /// <summary>
    /// How often to check for updates while the app is running, in hours.
    /// 0 disables the periodic check; the startup check is independent.
    /// </summary>
    public int UpdateCheckIntervalHours { get; set; } = 24;

    // The master-password vault (salt + wrapped data key) is NOT stored here. It
    // lives in a fixed per-user local folder managed by MasterKeyService, so these
    // secrets never travel with portable settings/connection data.
    //
    // The recently-used connection list also lives outside this file — it's a
    // per-user preference and stays under %APPDATA% even in portable mode.
}

/// <summary>
/// Per-user recently-used connections. Always persisted under %APPDATA%, so it
/// never travels with a portable folder.
/// </summary>
public class RecentSettings
{
    /// <summary>Recently-used connection file paths, most-recent first.</summary>
    public List<string> RecentConnectionPaths { get; set; } = new();

    /// <summary>Whether the "Recent" group at the top of the tree is expanded.</summary>
    public bool RecentExpanded { get; set; } = true;
}

/// <summary>Outcome of the Settings dialog. <see cref="Language"/> and <see cref="Theme"/>
/// are null when "follow system" is chosen.</summary>
public record SettingsDialogResult(
    StorageLocation StorageLocation,
    string? Language,
    string? Theme,
    bool CheckUpdateOnStartup,
    int UpdateCheckIntervalHours);
