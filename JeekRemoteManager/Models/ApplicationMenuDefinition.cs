using System.Collections.Generic;
using System.Linq;

namespace JeekRemoteManager.Models;

public enum ApplicationMenuAction
{
    Settings,
    LinkApplicationToProject,
    UnlinkApplicationFromProject,
    ImportFromFinalShell,
    ImportFromSecureCrt,
    ImportFromXshell,
    CheckForUpdates,
    About,
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
        new(ApplicationMenuAction.About, "About", "\uE946"),
        new(ApplicationMenuAction.Exit, "TrayExit", "\uE7E8"),
    ];

    /// <summary>
    /// Main-window menu actions. Application-wide MCP linking belongs here rather than in the
    /// tray menu because it opens a project-folder picker owned by the main window.
    /// </summary>
    public static IReadOnlyList<ApplicationMenuEntry> MainWindowItems { get; } =
    [
        CommonItems[0],
        new(
            ApplicationMenuAction.LinkApplicationToProject,
            "AiLinkApplicationProject",
            "\uE8B7",
            ToolTipLocalizationKey: "AiLinkApplicationProjectHint"),
        new(
            ApplicationMenuAction.UnlinkApplicationFromProject,
            "AiUnlinkApplicationProject",
            "\uE74D",
            ToolTipLocalizationKey: "AiUnlinkApplicationProjectHint"),
        .. CommonItems.Skip(1),
    ];
}
