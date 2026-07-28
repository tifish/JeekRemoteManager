using System.Text;

namespace JeekRemoteManager.Services;

/// <summary>
/// Parses the key spec of a "#pagekey" login directive into the bytes a terminal sends
/// for that key, e.g. "Ctrl-F" (the key long bastion menus print in their
/// "-- 共 51 条记录。Ctrl-F：下一页 --" footer) into 0x06.
/// </summary>
public static class LoginKeySequence
{
    public static bool TryParse(string spec, out string sequence, out string error)
    {
        sequence = "";
        error = "";
        spec = spec.Trim();
        if (spec.Length == 0)
        {
            error = "no key was given";
            return false;
        }

        // Backslash escapes let a spec carry anything the named keys don't cover,
        // e.g. "n\r" for menus that page with a letter plus Enter.
        if (spec.Contains('\\'))
        {
            if (!TryUnescape(spec, out sequence, out error))
                return false;
            return sequence.Length > 0;
        }

        var normalized = spec.Replace(" ", "").Replace("_", "-").Replace('+', '-');
        if (normalized.StartsWith("Ctrl-", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("Control-", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("^", StringComparison.Ordinal))
        {
            var rest = normalized.StartsWith("^", StringComparison.Ordinal)
                ? normalized[1..]
                : normalized[(normalized.IndexOf('-') + 1)..];
            if (rest.Length != 1 || !char.IsLetter(rest[0]))
            {
                error = $"\"{spec}\" is not a Ctrl key combination";
                return false;
            }

            sequence = ((char)(char.ToUpperInvariant(rest[0]) - 64)).ToString();
            return true;
        }

        // Cursor and paging keys in the terminal's normal (non-application) mode.
        var named = normalized.ToLowerInvariant() switch
        {
            "pagedown" or "pgdn" or "next" => "\u001b[6~",
            "pageup" or "pgup" or "prior" => "\u001b[5~",
            "down" => "\u001b[B",
            "up" => "\u001b[A",
            "right" => "\u001b[C",
            "left" => "\u001b[D",
            "home" => "\u001b[H",
            "end" => "\u001b[F",
            "enter" or "return" => "\r",
            "tab" => "\t",
            "space" => " ",
            "esc" or "escape" => "\u001b",
            _ => null,
        };
        if (named is not null)
        {
            sequence = named;
            return true;
        }

        if (spec.Length == 1)
        {
            sequence = spec;
            return true;
        }

        error = $"\"{spec}\" is not a key this app knows";
        return false;
    }

    private static bool TryUnescape(string spec, out string sequence, out string error)
    {
        var sb = new StringBuilder();
        error = "";
        for (var i = 0; i < spec.Length; i++)
        {
            if (spec[i] != '\\')
            {
                sb.Append(spec[i]);
                continue;
            }

            if (++i >= spec.Length)
            {
                sequence = "";
                error = $"\"{spec}\" ends with a dangling backslash";
                return false;
            }

            switch (spec[i])
            {
                case 'r': sb.Append('\r'); break;
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case 'e': sb.Append('\u001b'); break;
                case '\\': sb.Append('\\'); break;
                case 'x' when i + 2 < spec.Length
                              && int.TryParse(spec.AsSpan(i + 1, 2),
                                  System.Globalization.NumberStyles.HexNumber, null, out var code):
                    sb.Append((char)code);
                    i += 2;
                    break;
                default:
                    sequence = "";
                    error = $"\"\\{spec[i]}\" is not a known escape";
                    return false;
            }
        }

        sequence = sb.ToString();
        return true;
    }
}

/// <summary>
/// Walks a paged bastion menu looking for the entry a "#select" directive names: matches
/// the page on screen, and while a "#pagekey" is configured, presses it and matches the
/// next page. Stops as soon as a page stops bringing new entries, so a menu whose last
/// page repeats itself can't loop forever.
/// </summary>
public static class LoginMenuPager
{
    public const int MaxPages = 200;

    /// <param name="snapshot">Returns the output captured since the last key was sent.</param>
    /// <param name="pressPageKey">Resets the capture, sends the paging key, and waits for
    /// the next page to be drawn. Returns false when the session went away mid-walk.</param>
    public static async Task<LoginMenuSelectionResult> SelectAsync(
        string keyword,
        string? pageKeySequence,
        Func<string> snapshot,
        Func<Task<bool>> pressPageKey)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        LoginMenuSelectionResult result;

        for (var page = 1; ; page++)
        {
            var text = snapshot();
            result = LoginMenuSelection.Resolve(text, keyword);
            // An ambiguous page is a real answer: paging past it would silently pick a
            // machine from a later page over the ones already matching here.
            if (result.Success || result.Ambiguous || pageKeySequence is null)
                return result;

            var isNewPage = false;
            foreach (var entry in LoginMenuSelection.ParseEntries(text))
                isNewPage |= seen.Add($"{entry.Choice}|{entry.Label}");

            if (!isNewPage)
                return result with
                {
                    Failure = $"{result.Failure} (searched {page - 1} page(s) of the menu)",
                };

            if (page >= MaxPages)
                return result with { Failure = $"{result.Failure} (stopped after {MaxPages} pages)" };

            if (!await pressPageKey())
                return new LoginMenuSelectionResult(null, null, "the session ended while paging the menu");
        }
    }
}
