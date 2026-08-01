using System.Text;
using System.Globalization;
using JeekRemoteManager.Models;

namespace JeekRemoteManager.Services;

/// <summary>The part of a structured login-command workflow to execute.</summary>
public enum LoginCommandSection
{
    /// <summary>Initial connection: authentication prefix followed by the target-entry section.</summary>
    Fresh,
    /// <summary>Commands that enter this connection's target while reusing a bastion transport.</summary>
    ReuseEnter,
    /// <summary>Commands for another channel that already defaults to this same target.</summary>
    Duplicate,
    /// <summary>Commands that leave this target while reusing a bastion transport.</summary>
    ReuseLeave,
}

/// <summary>Parses the directives embedded in a connection's login commands.</summary>
public static class LoginCommandSequence
{
    private const string RetiredEnterDirective = "#enter";
    private const string RetiredLeaveDirective = "#leave";

    public const string ManualInputDirective = "#input";
    public const string ReuseEnterDirective = "#reuse-enter";
    public const string DuplicateStartDirective = "#duplicate";
    public const string ReuseLeaveDirective = "#reuse-leave";
    public const string KeyDirective = "#key";
    public const string MenuSelectDirective = "#select";
    public const string MenuPageKeyDirective = "#pagekey";
    public const string TemplateDirective = "#template";

    /// <summary>
    /// Known <c>#</c> directives shown by the login-command editor autocomplete and help dialog.
    /// Directives that take an argument insert a trailing space so the user can keep typing.
    /// </summary>
    public static IReadOnlyList<LoginCommandCompletion> Completions { get; } =
    [
        new("#input", "#input", "LoginCommandsHelpInput"),
        new("#reuse-enter", "#reuse-enter", "LoginCommandsHelpReuseEnter"),
        new("#duplicate", "#duplicate", "LoginCommandsHelpDuplicate"),
        new("#reuse-leave", "#reuse-leave", "LoginCommandsHelpReuseLeave"),
        new("#select <name>", "#select ", "LoginCommandsHelpSelect"),
        new("#pagekey <key>", "#pagekey ", "LoginCommandsHelpPageKey"),
        new("#key <key>", "#key ", "LoginCommandsHelpKey"),
        new("#template <1-4>", "#template ", "LoginCommandsHelpTemplate"),
    ];

    /// <summary>
    /// Filters <see cref="Completions"/> by a caret-prefix that starts with <c>#</c>.
    /// Matching is case-insensitive against the bare directive token.
    /// </summary>
    public static LoginCommandCompletion[] CompleteDirective(string prefix)
    {
        if (string.IsNullOrEmpty(prefix) || prefix[0] != '#')
            return [];

        return Completions
            .Where(item => item.Directive.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    private sealed record SourceLine(string Text, string Location);

    /// <summary>
    /// Selects commands for a fresh or duplicated session. Existing configurations that
    /// have no #reuse-enter/#reuse-leave sections keep their legacy #duplicate behavior unchanged.
    /// </summary>
    public static string[] Select(string commands, bool isDuplicatedSession) =>
        Select(
            commands,
            isDuplicatedSession ? LoginCommandSection.Duplicate : LoginCommandSection.Fresh);

    /// <summary>
    /// Selects one execution section. A structured workflow is:
    /// authentication prefix, #reuse-enter target-entry commands, #duplicate same-target
    /// channel commands, and #reuse-leave commands that return a reused channel to the
    /// bastion menu. A fresh connection executes both the prefix and #reuse-enter section.
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
            if (IsReuseEnterDirective(line))
            {
                current = LoginCommandSection.ReuseEnter;
                continue;
            }
            if (IsDuplicateStartDirective(line))
            {
                current = LoginCommandSection.Duplicate;
                continue;
            }
            if (IsReuseLeaveDirective(line))
            {
                current = LoginCommandSection.ReuseLeave;
                continue;
            }

            if (current == section
                || section == LoginCommandSection.Fresh
                   && current is LoginCommandSection.Fresh or LoginCommandSection.ReuseEnter)
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
        if (!TryExpandTemplateLines(commands, template, out var output, out error))
        {
            expanded = "";
            return false;
        }

        expanded = string.Join(Environment.NewLine, output.Select(line => line.Text));
        return true;
    }

    /// <summary>
    /// Expands shared template fragments and then resolves the explicit, safe
    /// connection-variable whitelist before directives are parsed.
    /// </summary>
    public static bool TryResolve(
        string commands,
        BastionLoginProfile? template,
        Connection connection,
        out string resolved,
        out string error)
    {
        if (!TryExpandTemplateLines(commands, template, out var lines, out error))
        {
            resolved = "";
            return false;
        }

        var output = new List<string>(lines.Count);
        foreach (var line in lines)
        {
            if (!TryExpandVariables(line.Text, connection, line.Location, out var expanded, out error))
            {
                resolved = "";
                return false;
            }
            output.Add(expanded);
        }

        resolved = string.Join(Environment.NewLine, output);
        error = "";
        return true;
    }

    /// <summary>
    /// Validates only variable syntax and values. This is used by the shared-template
    /// editor so all four fragments are checked even when a connection references only
    /// some of them.
    /// </summary>
    public static IReadOnlyList<string> ValidateVariables(
        string commands,
        Connection connection)
    {
        var messages = new List<string>();
        var lines = commands.ReplaceLineEndings("\n").Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            if (!TryExpandVariables(
                    lines[index].TrimEnd('\r'),
                    connection,
                    $"Line {index + 1}",
                    out _,
                    out var error))
            {
                messages.Add(error);
            }
        }
        return messages;
    }

    private static bool TryExpandTemplateLines(
        string commands,
        BastionLoginProfile? template,
        out List<SourceLine> output,
        out string error)
    {
        output = [];
        var sourceLines = commands.ReplaceLineEndings("\n").Split('\n');
        for (var index = 0; index < sourceLines.Length; index++)
        {
            var sourceLine = sourceLines[index].TrimEnd('\r');
            var trimmed = sourceLine.Trim();
            if (!StartsWithDirective(trimmed, TemplateDirective))
            {
                output.Add(new SourceLine(sourceLine, $"Line {index + 1}"));
                continue;
            }

            var argument = TryGetDirectiveArgument(trimmed, TemplateDirective) ?? "";
            if (argument.Length != 1
                || argument[0] is < '1' or > '4')
            {
                error =
                    $"Line {index + 1}: #template requires a fragment id from 1 to "
                    + $"{BastionLoginProfile.SegmentCount}.";
                return false;
            }
            var segmentId = argument[0] - '0';

            if (template is null)
            {
                error =
                    $"Line {index + 1}: #template {segmentId} requires a bastion login template.";
                return false;
            }

            var fragment = template.GetSegment(segmentId);
            if (ContainsTemplateDirective(fragment))
            {
                error = $"Template fragment {segmentId} cannot contain #template.";
                return false;
            }

            var fragmentLines = fragment.ReplaceLineEndings("\n").Split('\n');
            for (var fragmentIndex = 0; fragmentIndex < fragmentLines.Length; fragmentIndex++)
            {
                output.Add(new SourceLine(
                    fragmentLines[fragmentIndex].TrimEnd('\r'),
                    $"Template fragment {segmentId}, line {fragmentIndex + 1}"));
            }
        }

        error = "";
        return true;
    }

    private static bool TryExpandVariables(
        string source,
        Connection connection,
        string location,
        out string expanded,
        out string error)
    {
        var output = new StringBuilder(source.Length);
        for (var index = 0; index < source.Length;)
        {
            if (source[index] == '\\'
                && index + 2 < source.Length
                && source[index + 1] == '{'
                && source[index + 2] == '{')
            {
                output.Append("{{");
                index += 3;
                continue;
            }

            if (source[index] != '{'
                || index + 1 >= source.Length
                || source[index + 1] != '{')
            {
                output.Append(source[index]);
                index++;
                continue;
            }

            var close = source.IndexOf("}}", index + 2, StringComparison.Ordinal);
            if (close < 0)
            {
                expanded = "";
                error = $"{location}: variable starting at column {index + 1} is not closed.";
                return false;
            }

            var name = source[(index + 2)..close];
            if (!TryGetVariableValue(connection, name, out var value))
            {
                expanded = "";
                error = $"{location}: unknown connection variable {{{{{name}}}}}.";
                return false;
            }
            if (value.Length == 0)
            {
                expanded = "";
                error = $"{location}: connection variable {{{{{name}}}}} is empty.";
                return false;
            }
            if (value.Contains('\r') || value.Contains('\n'))
            {
                expanded = "";
                error = $"{location}: connection variable {{{{{name}}}}} cannot contain a line break.";
                return false;
            }

            output.Append(value);
            index = close + 2;
        }

        expanded = output.ToString();
        error = "";
        return true;
    }

    private static bool TryGetVariableValue(
        Connection connection,
        string name,
        out string value)
    {
        value = name.ToLowerInvariant() switch
        {
            "name" => connection.Name.Trim(),
            "host" => connection.Host.Trim(),
            "port" => (connection.Port > 0
                    ? connection.Port
                    : Connection.DefaultPort(connection.Type))
                .ToString(CultureInfo.InvariantCulture),
            "username" => connection.Username.Trim(),
            _ => "",
        };
        return name.Equals("name", StringComparison.OrdinalIgnoreCase)
               || name.Equals("host", StringComparison.OrdinalIgnoreCase)
               || name.Equals("port", StringComparison.OrdinalIgnoreCase)
               || name.Equals("username", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsManualInputDirective(string line) =>
        line.Trim().Equals(ManualInputDirective, StringComparison.OrdinalIgnoreCase);

    public static bool IsReuseEnterDirective(string line) =>
        line.Trim().Equals(ReuseEnterDirective, StringComparison.OrdinalIgnoreCase);

    public static bool IsReuseLeaveDirective(string line) =>
        line.Trim().Equals(ReuseLeaveDirective, StringComparison.OrdinalIgnoreCase);

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

    /// <summary>Expands templates and current-connection variables, then validates.</summary>
    public static IReadOnlyList<string> Validate(
        string commands,
        BastionLoginProfile? template,
        Connection connection)
    {
        if (!TryResolve(commands, template, connection, out var resolved, out var error))
            return [error];
        return ValidateExpanded(resolved);
    }

    private static IReadOnlyList<string> ValidateExpanded(string commands)
    {
        var sourceLines = commands.Split('\n');
        var messages = new List<string>();
        var reuseEnterLines = new List<int>();
        var duplicateLines = new List<int>();
        var reuseLeaveLines = new List<int>();

        for (var index = 0; index < sourceLines.Length; index++)
        {
            var line = sourceLines[index].Trim().TrimEnd('\r');
            if (line.Length == 0)
                continue;

            if (IsReuseEnterDirective(line))
                reuseEnterLines.Add(index + 1);
            else if (IsDuplicateStartDirective(line))
                duplicateLines.Add(index + 1);
            else if (IsReuseLeaveDirective(line))
                reuseLeaveLines.Add(index + 1);
            else if (line.Equals(RetiredEnterDirective, StringComparison.OrdinalIgnoreCase))
                messages.Add($"Line {index + 1}: #enter is no longer supported; use #reuse-enter.");
            else if (line.Equals(RetiredLeaveDirective, StringComparison.OrdinalIgnoreCase))
                messages.Add($"Line {index + 1}: #leave is no longer supported; use #reuse-leave.");
            else if (StartsWithDirective(line, KeyDirective))
                ValidateKeyDirective(line, index + 1, messages);
            else if (StartsWithDirective(line, MenuPageKeyDirective))
                ValidateNamedKeyDirective(line, MenuPageKeyDirective, index + 1, messages);
        }

        if (reuseEnterLines.Count != reuseLeaveLines.Count)
            messages.Add("#reuse-enter and #reuse-leave must be used together for safe bastion-session reuse.");
        if (reuseEnterLines.Count > 1)
            messages.Add($"#reuse-enter appears more than once (lines {string.Join(", ", reuseEnterLines)}).");
        if (duplicateLines.Count > 1)
            messages.Add($"#duplicate appears more than once (lines {string.Join(", ", duplicateLines)}).");
        if (reuseLeaveLines.Count > 1)
            messages.Add($"#reuse-leave appears more than once (lines {string.Join(", ", reuseLeaveLines)}).");

        if (reuseEnterLines.Count == 1 && reuseLeaveLines.Count == 1)
        {
            var reuseEnter = reuseEnterLines[0];
            var duplicate = duplicateLines.FirstOrDefault(int.MaxValue);
            var reuseLeave = reuseLeaveLines[0];
            if (!(reuseEnter < duplicate && duplicate < reuseLeave))
                messages.Add("Structured sections must be ordered as #reuse-enter, #duplicate, #reuse-leave.");
            else
            {
                if (Select(commands, LoginCommandSection.ReuseEnter).Length == 0)
                    messages.Add("#reuse-enter must contain at least one command or #key action.");
                if (Select(commands, LoginCommandSection.ReuseLeave).Length == 0)
                    messages.Add("#reuse-leave must contain at least one command or #key action.");
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
        sb.Append("reuse enter target: ").AppendLine(Show(Select(commands, LoginCommandSection.ReuseEnter)));
        sb.Append("reuse leave target: ").Append(Show(Select(commands, LoginCommandSection.ReuseLeave)));
        return sb.ToString();
    }

    private static string[] SelectLegacy(string[] lines, LoginCommandSection section)
    {
        if (section is LoginCommandSection.ReuseEnter or LoginCommandSection.ReuseLeave)
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
        lines.Any(IsReuseEnterDirective) && lines.Any(IsReuseLeaveDirective);

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
