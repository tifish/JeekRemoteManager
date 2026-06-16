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
}

/// <summary>Outcome of the Settings dialog. <see cref="Language"/> is null when "follow system" is chosen.</summary>
public record SettingsDialogResult(StorageLocation StorageLocation, string? Language);
