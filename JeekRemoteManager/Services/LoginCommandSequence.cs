namespace JeekRemoteManager.Services;

/// <summary>Parses the directives embedded in a connection's login commands.</summary>
public static class LoginCommandSequence
{
    public const string ManualInputDirective = "#input";
    public const string DuplicateStartDirective = "#duplicate";
    public const string MenuSelectDirective = "#select";
    public const string MenuPageKeyDirective = "#pagekey";

    public static string[] Select(string commands, bool isDuplicatedSession)
    {
        var lines = commands
            .Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();

        if (isDuplicatedSession)
        {
            var start = Array.FindIndex(lines, IsDuplicateStartDirective);
            if (start >= 0)
                lines = lines[(start + 1)..];
        }

        return lines
            .Where(line => !IsDuplicateStartDirective(line))
            .ToArray();
    }

    public static bool IsManualInputDirective(string line) =>
        line.Trim().Equals(ManualInputDirective, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads the machine name from a "#select &lt;name&gt;" line, or returns null when the
    /// line is an ordinary command. The name is matched against the menu on screen so a
    /// bastion selection survives renumbering when assets are added or removed.
    /// </summary>
    public static string? TryGetMenuSelectKeyword(string line) =>
        TryGetDirectiveArgument(line, MenuSelectDirective);

    /// <summary>
    /// Reads the key spec from a "#pagekey &lt;key&gt;" line (e.g. "Ctrl-F"), or returns null
    /// for an ordinary command. It applies to every "#select" that follows, so a menu too
    /// long for one screen is paged until the named entry shows up.
    /// </summary>
    public static string? TryGetMenuPageKey(string line) =>
        TryGetDirectiveArgument(line, MenuPageKeyDirective);

    private static string? TryGetDirectiveArgument(string line, string directive)
    {
        var trimmed = line.Trim();
        if (!trimmed.StartsWith(directive, StringComparison.OrdinalIgnoreCase))
            return null;

        var rest = trimmed[directive.Length..];
        // "#selection" is a command, not a directive: the argument must be separated.
        if (rest.Length > 0 && !char.IsWhiteSpace(rest[0]))
            return null;

        return rest.Trim();
    }

    private static bool IsDuplicateStartDirective(string line) =>
        line.Trim().Equals(DuplicateStartDirective, StringComparison.OrdinalIgnoreCase);
}
