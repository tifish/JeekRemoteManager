namespace JeekRemoteManager.Services;

/// <summary>Parses the directives embedded in a connection's login commands.</summary>
public static class LoginCommandSequence
{
    public const string ManualInputDirective = "#input";
    public const string DuplicateStartDirective = "#duplicate";

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

    private static bool IsDuplicateStartDirective(string line) =>
        line.Trim().Equals(DuplicateStartDirective, StringComparison.OrdinalIgnoreCase);
}
