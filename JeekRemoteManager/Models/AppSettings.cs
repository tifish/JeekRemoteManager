using System.Collections.Generic;

namespace JeekRemoteManager.Models;

/// <summary>Where the roaming Config folder is stored.</summary>
public enum StorageLocation
{
    /// <summary>Under %APPDATA%\JeekRemoteManager\Config (roaming, per-user).</summary>
    UserDirectory,

    /// <summary>Under a "Config" folder next to the executable (portable).</summary>
    ProgramDirectory,

    /// <summary>Under a "Config" folder beneath a user-chosen base directory.</summary>
    CustomDirectory,
}

/// <summary>
/// How the AI panel launches the selected agent.
/// <see cref="Cli"/> embeds ConPTY in the side panel (default);
/// <see cref="WindowsTerminal"/> opens the CLI in Windows Terminal;
/// <see cref="Desktop"/> opens Claude/Codex via registered protocol handlers.
/// </summary>
public enum AgentCliRunMode
{
    /// <summary>In-app ConPTY side panel (default).</summary>
    Cli = 0,

    /// <summary>Launch the agent CLI inside Windows Terminal.</summary>
    WindowsTerminal = 1,

    /// <summary>Open Claude/Codex desktop via protocol URI (not Grok).</summary>
    Desktop = 2,
}

/// <summary>In-memory view of all persisted application settings.</summary>
public class AppSettings
{
    public StorageLocation StorageLocation { get; set; } = StorageLocation.UserDirectory;

    /// <summary>Base directory for <see cref="StorageLocation.CustomDirectory"/>.
    /// Config lives under this path, with connection and script data in
    /// "Config\Connections"/"Config\Scripts". Null/blank when no custom
    /// location has been chosen.</summary>
    public string? CustomStoragePath { get; set; }

    /// <summary>UI language code ("en", "zh"). Null = follow system culture.</summary>
    public string? Language { get; set; }

    /// <summary>UI theme ("Light", "Dark"). Null = follow system theme.</summary>
    public string? Theme { get; set; }

    /// <summary>Main window width in device-independent pixels. Null = default size.</summary>
    public double? MainWindowWidth { get; set; }

    /// <summary>Main window height in device-independent pixels. Null = default size.</summary>
    public double? MainWindowHeight { get; set; }

    /// <summary>Width of the connection tree panel, in device-independent pixels.</summary>
    public double ConnectionPanelWidth { get; set; } = 306;

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

    /// <summary>Terminal font size in points, adjustable from the toolbar.</summary>
    public int TerminalFontSize { get; set; } = 14;

    /// <summary>Width of the in-terminal AI assistant panel, in device-independent pixels.</summary>
    public double AiPanelWidth { get; set; } = 380;

    /// <summary>Height of the in-terminal SFTP file browser panel, in device-independent pixels.</summary>
    public double FileBrowserPanelHeight { get; set; } = 260;

    /// <summary>Width of the in-terminal server monitor panel, in device-independent pixels.</summary>
    public double MonitorPanelWidth { get; set; } = 260;

    /// <summary>Editor executable used by the file browser's remote editing (F4).
    /// Null/blank = open with the system file association.</summary>
    public string? FileBrowserEditorPath { get; set; }

    /// <summary>AI panel: last-used provider label ("Claude", "Codex", "Grok"). Null = first available.</summary>
    public string? AiProvider { get; set; }

    /// <summary>AI panel: how the selected agent is launched (CLI / Windows Terminal / Desktop).</summary>
    public AgentCliRunMode AiRunMode { get; set; } = AgentCliRunMode.Cli;

    /// <summary>AI panel: whether remote command tools run without the agent CLI asking first.</summary>
    public bool AiAutoRun { get; set; } = true;

    /// <summary>AI panel: whether potentially destructive remote commands bypass confirmation.</summary>
    public bool AiAutoApproveDangerousCommands { get; set; }
}

/// <summary>Settings that are bound to this Windows account and machine.</summary>
public class MachineAppSettings
{
    public StorageLocation StorageLocation { get; set; } = StorageLocation.UserDirectory;

    /// <summary>Base directory for <see cref="StorageLocation.CustomDirectory"/>.</summary>
    public string? CustomStoragePath { get; set; }

    /// <summary>Main window width in device-independent pixels. Null = default size.</summary>
    public double? MainWindowWidth { get; set; }

    /// <summary>Main window height in device-independent pixels. Null = default size.</summary>
    public double? MainWindowHeight { get; set; }

    /// <summary>Width of the connection tree panel, in device-independent pixels.</summary>
    public double ConnectionPanelWidth { get; set; } = 306;

    /// <summary>Recently-used connection file paths, most-recent first.</summary>
    public List<string> RecentConnectionPaths { get; set; } = new();

    /// <summary>Connection file path that was selected when the app last ran.</summary>
    public string? LastSelectedConnectionPath { get; set; }

    /// <summary>Whether the "Recent" group at the top of the tree is expanded.</summary>
    public bool RecentExpanded { get; set; } = true;

    /// <summary>Absolute paths of folders the user has collapsed in the tree.</summary>
    public List<string> CollapsedFolderPaths { get; set; } = new();

}

/// <summary>Machine-independent preferences stored with the selected storage mode.</summary>
public class RoamingAppSettings
{
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

    /// <summary>Terminal font size in points, adjustable from the toolbar.</summary>
    public int TerminalFontSize { get; set; } = 14;

    /// <summary>Width of the in-terminal AI assistant panel, in device-independent pixels.</summary>
    public double AiPanelWidth { get; set; } = 380;

    /// <summary>Height of the in-terminal SFTP file browser panel, in device-independent pixels.</summary>
    public double FileBrowserPanelHeight { get; set; } = 260;

    /// <summary>Width of the in-terminal server monitor panel, in device-independent pixels.</summary>
    public double MonitorPanelWidth { get; set; } = 260;

    /// <summary>Editor executable used by the file browser's remote editing (F4).
    /// Null/blank = open with the system file association.</summary>
    public string? FileBrowserEditorPath { get; set; }

    /// <summary>AI panel: last-used provider label ("Claude", "Codex", "Grok"). Null = first available.</summary>
    public string? AiProvider { get; set; }

    /// <summary>AI panel: how the selected agent is launched (CLI / Windows Terminal / Desktop).</summary>
    public AgentCliRunMode AiRunMode { get; set; } = AgentCliRunMode.Cli;

    /// <summary>AI panel: whether remote command tools run without the agent CLI asking first.</summary>
    public bool AiAutoRun { get; set; } = true;

    /// <summary>AI panel: whether potentially destructive remote commands bypass confirmation.</summary>
    public bool AiAutoApproveDangerousCommands { get; set; }
}

/// <summary>Outcome of the Settings dialog. <see cref="Language"/> and <see cref="Theme"/>
/// are null when "follow system" is chosen.</summary>
public record SettingsDialogResult(
    StorageLocation StorageLocation,
    string? CustomStoragePath,
    string? Language,
    string? Theme,
    bool CheckUpdateOnStartup,
    int UpdateCheckIntervalHours,
    string? FileBrowserEditorPath);
