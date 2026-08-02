using System.Collections.Generic;

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
/// Single ordered action list shared by the main-window overflow menu and the
/// tray menu. Platform-specific items such as tray "Show" stay outside this list.
/// </summary>
public static class ApplicationMenuDefinition
{
    /// <summary>
    /// Shared application menu items. Both the main window and the tray iterate
    /// this list so order, labels, and actions stay in lockstep.
    /// </summary>
    public static IReadOnlyList<ApplicationMenuEntry> Items { get; } =
    [
        new(
            ApplicationMenuAction.Settings,
            "Settings",
            "\uE713",
            ToolTipLocalizationKey: "SettingsTooltip"),
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
        new(ApplicationMenuAction.ImportFromFinalShell, "ImportFromFinalShell", "\uE8B5"),
        new(ApplicationMenuAction.ImportFromSecureCrt, "ImportFromSecureCrt", "\uE8B5"),
        new(ApplicationMenuAction.ImportFromXshell, "ImportFromXshell", "\uE8B5"),
        new(ApplicationMenuAction.CheckForUpdates, "CheckForUpdates", "\uE895", IsAccent: true),
        new(ApplicationMenuAction.About, "About", "\uE946"),
        new(ApplicationMenuAction.Exit, "TrayExit", "\uE7E8"),
    ];
}
