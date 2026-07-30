using System.Text;
using System.Text.RegularExpressions;

namespace JeekRemoteManager.Services;

/// <summary>One numbered entry of a bastion menu, e.g. "   3: 10.11.13.42   机甲-linux构建".</summary>
public sealed record LoginMenuEntry(string Choice, string Label, IReadOnlyList<string> Fields);

/// <summary>Outcome of matching a "#select &lt;name&gt;" directive against the menu on screen.</summary>
public sealed record LoginMenuSelectionResult(
    string? Choice,
    string? MatchedLabel,
    string? Failure,
    bool Ambiguous = false)
{
    public bool Success => Choice is not null;
}

/// <summary>
/// Resolves a bastion menu choice by machine name instead of by number. Menu numbers
/// shift whenever an asset is added or removed, so a login-command line of
/// "#select 机甲-linux构建" finds the entry whose text matches and types its number.
/// </summary>
public static class LoginMenuSelection
{
    // Menus arrive through a PTY, so colors and cursor moves can be interleaved with text.
    private static readonly Regex AnsiAndControlChars = new(
        "\u001b\\[[0-9;?]*[ -/]*[@-~]" +
        "|\u001b\\][\\s\\S]*?(?:\u0007|\u001b\\\\)" +
        "|\u001b[@-_]" +
        "|[\u0000-\u0008\u000b\u000c\u000e-\u001f\u007f]",
        RegexOptions.Compiled);

    // "   1: 10.11.177.209    10.11.177.209(机甲文件服务器)" — also tolerates "1." / "1)" / "1、"
    // and the full-width colon some bastions print.
    private static readonly Regex MenuLine = new(
        @"^[\s>*\[]*(\d{1,4})\s*[:：.、)\]]\s*(\S.*)$",
        RegexOptions.Compiled);

    private static readonly Regex ColumnGap = new(@"\s{2,}", RegexOptions.Compiled);

    /// <summary>
    /// Finds the menu entry matching <paramref name="keyword"/> in the output the remote
    /// printed since the previous login command. An exact match on a whole column (the IP
    /// or the name) wins; otherwise a single substring match is used. Anything ambiguous or
    /// missing fails rather than guessing, so a shifted menu never selects the wrong machine.
    /// </summary>
    public static LoginMenuSelectionResult Resolve(string output, string keyword)
    {
        keyword = Normalize(keyword);
        if (keyword.Length == 0)
            return new LoginMenuSelectionResult(null, null, "the #select directive has no name to match");

        var entries = ParseEntries(output);
        if (entries.Count == 0)
            return new LoginMenuSelectionResult(null, null, $"no numbered menu was found on screen for \"{keyword}\"");

        var exact = entries
            .Where(entry => entry.Fields.Any(field => field.Equals(keyword, StringComparison.OrdinalIgnoreCase))
                            || entry.Label.Equals(keyword, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var matches = exact.Count > 0
            ? exact
            : entries
                .Where(entry => entry.Label.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                .ToList();

        if (matches.Count == 1)
            return new LoginMenuSelectionResult(matches[0].Choice, matches[0].Label, null);

        if (matches.Count == 0)
            return new LoginMenuSelectionResult(null, null, $"no menu entry matches \"{keyword}\"");

        var listed = string.Join(", ", matches.Take(5).Select(entry => $"{entry.Choice}: {entry.Label}"));
        return new LoginMenuSelectionResult(
            null, null, $"\"{keyword}\" matches {matches.Count} menu entries ({listed})", Ambiguous: true);
    }

    /// <summary>
    /// Parses the numbered entries of the menu that is currently on screen: the last run of
    /// consecutive numbered lines, so an earlier menu still in the captured output (asset
    /// categories before assets) can't be matched by mistake. Falls back to every numbered
    /// line when the last block holds no match for the caller.
    /// </summary>
    public static IReadOnlyList<LoginMenuEntry> ParseEntries(string output)
    {
        var all = new List<LoginMenuEntry>();
        var lastBlock = new List<LoginMenuEntry>();
        var block = new List<LoginMenuEntry>();

        foreach (var rawLine in Clean(output).Split('\n'))
        {
            var match = MenuLine.Match(rawLine);
            if (!match.Success)
            {
                if (block.Count > 0)
                {
                    lastBlock = block;
                    block = [];
                }
                continue;
            }

            var label = Normalize(match.Groups[2].Value);
            if (label.Length == 0)
                continue;

            var fields = ColumnGap
                .Split(match.Groups[2].Value.Trim())
                .Select(field => field.Trim())
                .Where(field => field.Length > 0)
                .ToArray();

            var entry = new LoginMenuEntry(match.Groups[1].Value, label, fields);
            block.Add(entry);
            all.Add(entry);
        }

        if (block.Count > 0)
            lastBlock = block;

        return lastBlock.Count > 0 ? lastBlock : all;
    }

    private static string Clean(string text) =>
        AnsiAndControlChars.Replace(text, string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');

    /// <summary>Column padding is layout, not content: compare on single-spaced text.</summary>
    private static string Normalize(string text) => ColumnGap.Replace(text.Trim(), " ");
}

/// <summary>
/// Rolling capture of the raw shell output produced since the last login command was
/// typed, so a "#select" directive can read the menu that is currently on screen. Fed
/// from the shell's data callback and read from the login-sequence task, so every member
/// is thread-safe.
/// </summary>
public sealed class LoginMenuOutputCapture
{
    private const int MaxChars = 64 * 1024;

    private readonly object _gate = new();
    private readonly Utf8StreamDecoder _decoder = new();
    private readonly StringBuilder _text = new();

    public void Append(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
            return;

        lock (_gate)
        {
            // Decode inside the lock: the decoder carries the split multi-byte tail.
            _text.Append(_decoder.Decode(data));
            if (_text.Length > MaxChars)
                _text.Remove(0, _text.Length - MaxChars);
        }
    }

    /// <summary>Drops what was captured so far; the caller is about to type a new command.</summary>
    public void Reset()
    {
        lock (_gate)
            _text.Clear();
    }

    public string Snapshot()
    {
        lock (_gate)
            return _text.ToString();
    }
}
