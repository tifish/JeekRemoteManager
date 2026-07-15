using System.Collections.Generic;

namespace JeekRemoteManager.Models;

public enum ApplicationMenuAction
{
    Settings,
    ImportFromFinalShell,
    ImportFromSecureCrt,
    ImportFromXshell,
    CheckForUpdates,
    Exit,
}

public sealed record ApplicationMenuEntry(
    ApplicationMenuAction Action,
    string LocalizationKey,
    string IconGlyph,
    bool IsAccent = false,
    string? ToolTipLocalizationKey = null);

/// <summary>
/// Shared action order and presentation metadata for the main-window overflow
/// menu and the tray menu. Platform-specific items such as "Show" stay outside
/// this list.
/// </summary>
public static class ApplicationMenuDefinition
{
    public static IReadOnlyList<ApplicationMenuEntry> CommonItems { get; } =
    [
        new(
            ApplicationMenuAction.Settings,
            "Settings",
            "\uE713",
            ToolTipLocalizationKey: "SettingsTooltip"),
        new(ApplicationMenuAction.ImportFromFinalShell, "ImportFromFinalShell", "\uE8B5"),
        new(ApplicationMenuAction.ImportFromSecureCrt, "ImportFromSecureCrt", "\uE8B5"),
        new(ApplicationMenuAction.ImportFromXshell, "ImportFromXshell", "\uE8B5"),
        new(ApplicationMenuAction.CheckForUpdates, "CheckForUpdates", "\uE895", IsAccent: true),
        new(ApplicationMenuAction.Exit, "TrayExit", "\uE7E8"),
    ];
}
