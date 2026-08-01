using System.Text;
using JeekRemoteManager.Models;

namespace JeekRemoteManager.Services;

/// <summary>The part of a structured login-command workflow to execute.</summary>
public enum LoginCommandSection
{
    /// <summary>Initial connection: authentication prefix followed by the target-entry section.</summary>
    Fresh,
    /// <summary>Commands that enter this connection's target from the bastion menu.</summary>
    Enter,
    /// <summary>Commands for another channel that already defaults to this same target.</summary>
    Duplicate,
    /// <summary>Commands that leave this target and return a reused channel to the bastion menu.</summary>
    Leave,
}

/// <summary>Parses the directives embedded in a connection's login commands.</summary>
public static class LoginCommandSequence
{
    public const string ManualInputDirective = "#input";
    public const string EnterDirective = "#enter";
    public const string DuplicateStartDirective = "#duplicate";
    public const string LeaveDirective = "#leave";
    public const string KeyDirective = "#key";
    public const string MenuSelectDirective = "#select";
    public const string MenuPageKeyDirective = "#pagekey";
    public const string TemplateDirective = "#template";

    /// <summary>
    /// Selects commands for a fresh or duplicated session. Existing configurations that
    /// have no #enter/#leave sections keep their legacy #duplicate behavior unchanged.
    /// </summary>
    public static string[] Select(string commands, bool isDuplicatedSession) =>
        Select(
            commands,
            isDuplicatedSession ? LoginCommandSection.Duplicate : LoginCommandSection.Fresh);

    /// <summary>
    /// Selects one execution section. A structured workflow is:
    /// authentication prefix, #enter target-entry commands, #duplicate same-target channel
    /// commands, and #leave commands that return a reused channel to the bastion menu.
    /// A fresh connection executes both the prefix and #enter section.
    /// </summary>
    public static string[] Select(string commands, LoginCommandSection section)
    {
        var lines = Lines(commands);
        if (!HasStructuredReuseWorkflow(lines))
            return SelectLegacy(lines, section);

        var selected = new List<string>();
        var current = LoginCommandSection.Fresh;
        foreach (var line in lines)
        {
            if (IsEnterDirective(line))
            {
                current = LoginCommandSection.Enter;
                continue;
            }
            if (IsDuplicateStartDirective(line))
            {
                current = LoginCommandSection.Duplicate;
                continue;
            }
            if (IsLeaveDirective(line))
            {
                current = LoginCommandSection.Leave;
                continue;
            }

            if (current == section
                || section == LoginCommandSection.Fresh
                   && current is LoginCommandSection.Fresh or LoginCommandSection.Enter)
            {
                selected.Add(line);
            }
        }

        return selected.ToArray();
    }

    /// <summary>
    /// True only when both cross-target boundaries are present. A partial workflow is
    /// never pooled because guessing where to leave or enter can reach the wrong server.
    /// </summary>
    public static bool HasStructuredReuseWorkflow(string commands) =>
        HasStructuredReuseWorkflow(Lines(commands));

    /// <summary>True when the command text contains one or more #template directives.</summary>
    public static bool ContainsTemplateDirective(string commands) =>
        commands.Split('\n')
            .Select(line => line.Trim().TrimEnd('\r'))
            .Any(line => StartsWithDirective(line, TemplateDirective));

    /// <summary>Removes blank lines only from the beginning and end of a command block.</summary>
    public static string TrimSurroundingBlankLines(string commands)
    {
        var lines = commands.ReplaceLineEndings("\n").Split('\n');
        var first = 0;
        while (first < lines.Length && string.IsNullOrWhiteSpace(lines[first]))
            first++;
        var last = lines.Length - 1;
        while (last >= first && string.IsNullOrWhiteSpace(lines[last]))
            last--;
        return first > last
            ? ""
            : string.Join(Environment.NewLine, lines[first..(last + 1)]);
    }

    /// <summary>
    /// Expands #template 1 through #template 4 before the normal login-command parser
    /// interprets any directives. Template fragments may contain every directive except
    /// #template itself, preventing recursive or cyclic expansion.
    /// </summary>
    public static bool TryExpandTemplate(
        string commands,
        BastionLoginProfile? template,
        out string expanded,
        out string error)
    {
        var output = new List<string>();
        var sourceLines = commands.ReplaceLineEndings("\n").Split('\n');
        for (var index = 0; index < sourceLines.Length; index++)
        {
            var sourceLine = sourceLines[index].TrimEnd('\r');
            var trimmed = sourceLine.Trim();
            if (!StartsWithDirective(trimmed, TemplateDirective))
            {
                output.Add(sourceLine);
                continue;
            }

            var argument = TryGetDirectiveArgument(trimmed, TemplateDirective) ?? "";
            if (argument.Length != 1
                || argument[0] is < '1' or > '4')
            {
                expanded = "";
                error =
                    $"Line {index + 1}: #template requires a fragment id from 1 to "
                    + $"{BastionLoginProfile.SegmentCount}.";
                return false;
            }
            var segmentId = argument[0] - '0';

            if (template is null)
            {
                expanded = "";
                error =
                    $"Line {index + 1}: #template {segmentId} requires a bastion login template.";
                return false;
            }

            var fragment = template.GetSegment(segmentId);
            if (ContainsTemplateDirective(fragment))
            {
                expanded = "";
                error = $"Template fragment {segmentId} cannot contain #template.";
                return false;
            }

            output.AddRange(fragment.ReplaceLineEndings("\n").Split('\n'));
        }

        expanded = string.Join(Environment.NewLine, output);
        error = "";
        return true;
    }

    public static bool IsManualInputDirective(string line) =>
        line.Trim().Equals(ManualInputDirective, StringComparison.OrdinalIgnoreCase);

    public static bool IsEnterDirective(string line) =>
        line.Trim().Equals(EnterDirective, StringComparison.OrdinalIgnoreCase);

    public static bool IsLeaveDirective(string line) =>
        line.Trim().Equals(LeaveDirective, StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads the key spec from "#key &lt;key&gt;", or null for an ordinary command.</summary>
    public static string? TryGetKey(string line) =>
        TryGetDirectiveArgument(line, KeyDirective);

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

    /// <summary>Returns user-facing validation messages with one-based source line numbers.</summary>
    public static IReadOnlyList<string> Validate(string commands) =>
        Validate(commands, template: null);

    /// <summary>Expands a template, then validates the resulting full workflow.</summary>
    public static IReadOnlyList<string> Validate(
        string commands,
        BastionLoginProfile? template)
    {
        if (!TryExpandTemplate(commands, template, out var expanded, out var error))
            return [error];
        return ValidateExpanded(expanded);
    }

    private static IReadOnlyList<string> ValidateExpanded(string commands)
    {
        var sourceLines = commands.Split('\n');
        var messages = new List<string>();
        var enterLines = new List<int>();
        var duplicateLines = new List<int>();
        var leaveLines = new List<int>();

        for (var index = 0; index < sourceLines.Length; index++)
        {
            var line = sourceLines[index].Trim().TrimEnd('\r');
            if (line.Length == 0)
                continue;

            if (IsEnterDirective(line))
                enterLines.Add(index + 1);
            else if (IsDuplicateStartDirective(line))
                duplicateLines.Add(index + 1);
            else if (IsLeaveDirective(line))
                leaveLines.Add(index + 1);
            else if (StartsWithDirective(line, KeyDirective))
                ValidateKeyDirective(line, index + 1, messages);
            else if (StartsWithDirective(line, MenuPageKeyDirective))
                ValidateNamedKeyDirective(line, MenuPageKeyDirective, index + 1, messages);
        }

        if (enterLines.Count != leaveLines.Count)
            messages.Add("#enter and #leave must be used together for safe bastion-session reuse.");
        if (enterLines.Count > 1)
            messages.Add($"#enter appears more than once (lines {string.Join(", ", enterLines)}).");
        if (duplicateLines.Count > 1)
            messages.Add($"#duplicate appears more than once (lines {string.Join(", ", duplicateLines)}).");
        if (leaveLines.Count > 1)
            messages.Add($"#leave appears more than once (lines {string.Join(", ", leaveLines)}).");

        if (enterLines.Count == 1 && leaveLines.Count == 1)
        {
            var enter = enterLines[0];
            var duplicate = duplicateLines.FirstOrDefault(int.MaxValue);
            var leave = leaveLines[0];
            if (!(enter < duplicate && duplicate < leave))
                messages.Add("Structured sections must be ordered as #enter, #duplicate, #leave.");
            else
            {
                if (Select(commands, LoginCommandSection.Enter).Length == 0)
                    messages.Add("#enter must contain at least one command or #key action.");
                if (Select(commands, LoginCommandSection.Leave).Length == 0)
                    messages.Add("#leave must contain at least one command or #key action.");
            }
        }

        return messages;
    }

    /// <summary>Compact deterministic preview used by the editor and Debug MCP.</summary>
    public static string BuildPreview(string commands)
    {
        static string Show(string[] lines) => lines.Length == 0 ? "(none)" : string.Join(" | ", lines);

        var sb = new StringBuilder();
        sb.Append("fresh: ").AppendLine(Show(Select(commands, LoginCommandSection.Fresh)));
        sb.Append("duplicate/monitor: ").AppendLine(Show(Select(commands, LoginCommandSection.Duplicate)));
        sb.Append("cross-target enter: ").AppendLine(Show(Select(commands, LoginCommandSection.Enter)));
        sb.Append("leave target: ").Append(Show(Select(commands, LoginCommandSection.Leave)));
        return sb.ToString();
    }

    private static string[] SelectLegacy(string[] lines, LoginCommandSection section)
    {
        if (section is LoginCommandSection.Enter or LoginCommandSection.Leave)
            return [];

        if (section == LoginCommandSection.Duplicate)
        {
            var start = Array.FindIndex(lines, IsDuplicateStartDirective);
            if (start >= 0)
                lines = lines[(start + 1)..];
        }

        return lines
            .Where(line => !IsDuplicateStartDirective(line))
            .ToArray();
    }

    private static string[] Lines(string commands) =>
        commands
            .Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();

    private static bool HasStructuredReuseWorkflow(string[] lines) =>
        lines.Any(IsEnterDirective) && lines.Any(IsLeaveDirective);

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

    private static bool StartsWithDirective(string line, string directive) =>
        line.StartsWith(directive, StringComparison.OrdinalIgnoreCase)
        && (line.Length == directive.Length || char.IsWhiteSpace(line[directive.Length]));

    private static void ValidateKeyDirective(string line, int lineNumber, List<string> messages) =>
        ValidateNamedKeyDirective(line, KeyDirective, lineNumber, messages);

    private static void ValidateNamedKeyDirective(
        string line,
        string directive,
        int lineNumber,
        List<string> messages)
    {
        var key = TryGetDirectiveArgument(line, directive) ?? "";
        if (!LoginKeySequence.TryParse(key, out _, out var error))
            messages.Add($"Line {lineNumber}: {directive} {error}.");
    }

    private static bool IsDuplicateStartDirective(string line) =>
        line.Trim().Equals(DuplicateStartDirective, StringComparison.OrdinalIgnoreCase);
}
