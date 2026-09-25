using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using SvcSystems.UI.Terminal;

namespace JeekRemoteManager.Services;

/// <summary>A terminal palette: the 16 ANSI colors plus caret and selection.</summary>
/// <remarks>
/// The terminal control has no separate default foreground/background: it paints the
/// background with palette entry 0 and default text with entry 15. So every scheme here is
/// dark, with entry 0 set to the scheme's background and entry 15 to its foreground — a
/// light scheme would have to make ANSI "black" light and break every program that uses it.
/// </remarks>
public sealed record TerminalColorScheme(string Name, IReadOnlyList<string> Palette, string Caret, string Selection);

/// <summary>User-facing terminal appearance: font, colors, and history size.</summary>
public sealed record TerminalAppearanceSettings(string? FontFamily, string ColorScheme, int ScrollbackLines);

/// <summary>
/// Terminal fonts, color schemes and scrollback. Colors are application resources the
/// terminal control looks up while rendering (<c>SvcSystems.UI.TerminalColorN</c>), so a
/// scheme change reaches every open terminal — main shells and AI panels alike — once
/// their render caches are cleared. The font is set per control, like the font size.
/// Scrollback is fixed when a terminal buffer is created, so it applies to new tabs.
/// </summary>
public static class TerminalAppearance
{
    public const string DefaultFontFamily = "Cascadia Mono";
    public const string DefaultSchemeName = "Default";
    public const int DefaultScrollbackLines = 10000;

    public static readonly int[] ScrollbackChoices = [1000, 5000, 10000, 50000, 100000];

    public static readonly IReadOnlyList<TerminalColorScheme> Schemes =
    [
        new(DefaultSchemeName,
        [
            "#151B22", "#D66F7B", "#8DCB6E", "#DDB858", "#63A4DC", "#C27CC8", "#58BFD0", "#D6DEE8",
            "#66717F", "#EA8791", "#A4DA82", "#E8C96E", "#82BDF0", "#D794D8", "#76D5E2", "#F0F4F8",
        ], "#D8E2EA", "#405E81AC"),
        new("Campbell",
        [
            "#0C0C0C", "#C50F1F", "#13A10E", "#C19C00", "#0037DA", "#881798", "#3A96DD", "#CCCCCC",
            "#767676", "#E74856", "#16C60C", "#F9F1A5", "#3B78FF", "#B4009E", "#61D6D6", "#F2F2F2",
        ], "#FFFFFF", "#50FFFFFF"),
        new("One Half Dark",
        [
            "#282C34", "#E06C75", "#98C379", "#E5C07B", "#61AFEF", "#C678DD", "#56B6C2", "#DCDFE4",
            "#5A6374", "#E06C75", "#98C379", "#E5C07B", "#61AFEF", "#C678DD", "#56B6C2", "#DCDFE4",
        ], "#DCDFE4", "#50DCDFE4"),
        new("Solarized Dark",
        [
            "#002B36", "#DC322F", "#859900", "#B58900", "#268BD2", "#D33682", "#2AA198", "#EEE8D5",
            "#586E75", "#CB4B16", "#859900", "#B58900", "#268BD2", "#6C71C4", "#2AA198", "#93A1A1",
        ], "#93A1A1", "#40839496"),
        new("Dracula",
        [
            "#282A36", "#FF5555", "#50FA7B", "#F1FA8C", "#BD93F9", "#FF79C6", "#8BE9FD", "#BFBFBF",
            "#6272A4", "#FF6E6E", "#69FF94", "#FFFFA5", "#D6ACFF", "#FF92DF", "#A4FFFF", "#F8F8F2",
        ], "#F8F8F2", "#8044475A"),
        new("Monokai",
        [
            "#272822", "#F92672", "#A6E22E", "#F4BF75", "#66D9EF", "#AE81FF", "#A1EFE4", "#F8F8F2",
            "#75715E", "#F92672", "#A6E22E", "#F4BF75", "#66D9EF", "#AE81FF", "#A1EFE4", "#F9F8F5",
        ], "#F8F8F0", "#6049483E"),
    ];

    public static TerminalColorScheme FindScheme(string? name) =>
        Schemes.FirstOrDefault(scheme => string.Equals(scheme.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? Schemes[0];

    public static string NormalizeSchemeName(string? name) => FindScheme(name).Name;

    public static int NormalizeScrollback(int lines) =>
        lines <= 0 ? DefaultScrollbackLines : Math.Clamp(lines, 100, 200_000);

    /// <summary>
    /// The chosen font with the default mono font behind it. CJK glyphs still come from the
    /// application-wide fallback chain set at startup.
    /// </summary>
    public static FontFamily ResolveFontFamily(string? name) =>
        new(string.IsNullOrWhiteSpace(name) || name.Trim() == DefaultFontFamily
            ? DefaultFontFamily
            : $"{name.Trim()}, {DefaultFontFamily}");

    /// <summary>Writes a scheme into the resources the terminal control renders from.</summary>
    public static void ApplyColorScheme(IResourceDictionary resources, TerminalColorScheme scheme)
    {
        for (var i = 0; i < scheme.Palette.Count; i++)
            resources[$"SvcSystems.UI.TerminalColor{i}"] = new SolidColorBrush(Color.Parse(scheme.Palette[i]));
        resources["SvcSystems.UI.TerminalCaretBrush"] = new SolidColorBrush(Color.Parse(scheme.Caret));
        resources["SvcSystems.UI.TerminalSelectionBrush"] = new SolidColorBrush(Color.Parse(scheme.Selection));
    }

    private static readonly MethodInfo? ClearFormattedTextCache = typeof(TerminalControl).GetMethod(
        "ClearFormattedTextCache", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly FieldInfo? SurfaceField = typeof(TerminalControl).GetField(
        "_surface", BindingFlags.Instance | BindingFlags.NonPublic);

    /// <summary>
    /// Repaints a terminal after its palette resources changed. The control caches each
    /// formatted text run together with its resolved brush and only drops that cache on a
    /// font change, so without this the old colors stay on screen until the text changes.
    /// There is no public hook for it, hence reflection; if a library update renames the
    /// members the terminal still repaints, just with stale colors on cached runs.
    /// </summary>
    public static void RefreshRendering(TerminalControl control)
    {
        ClearFormattedTextCache?.Invoke(control, null);
        (SurfaceField?.GetValue(control) as Visual)?.InvalidateVisual();
        control.InvalidateVisual();
    }

    /// <summary>True when the reflection hooks <see cref="RefreshRendering"/> relies on exist.</summary>
    public static bool CanClearRenderCache => ClearFormattedTextCache is not null && SurfaceField is not null;
}
