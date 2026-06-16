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

    // --- Master password (envelope encryption) ---
    //
    // Connection passwords are encrypted with a random data key (DEK). The DEK is
    // itself encrypted ("wrapped") with a key (KEK) derived from the user's master
    // password via PBKDF2. Storing only the wrapped DEK means changing the master
    // password just re-wraps the DEK — no connection file has to be rewritten. These
    // fields travel with settings.json, so the data is portable to another machine
    // (where the user re-enters the master password once). The DEK itself is cached
    // separately, machine-locally, via DPAPI so day-to-day startup needs no input.

    /// <summary>Base64 PBKDF2 salt used to derive the key-encryption key. Null = no master password set.</summary>
    public string? MasterSalt { get; set; }

    /// <summary>PBKDF2 iteration count used to derive the key-encryption key.</summary>
    public int MasterIterations { get; set; }

    /// <summary>Base64 AES-GCM blob: the data key wrapped by the password-derived key.</summary>
    public string? WrappedKey { get; set; }

    /// <summary>Base64 AES-GCM blob encrypting a known marker with the data key, to verify a candidate data key.</summary>
    public string? KeyCheck { get; set; }
}

/// <summary>Outcome of the Settings dialog. <see cref="Language"/> and <see cref="Theme"/>
/// are null when "follow system" is chosen.</summary>
public record SettingsDialogResult(StorageLocation StorageLocation, string? Language, string? Theme);
