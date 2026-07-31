using System.Globalization;

namespace JeekRemoteManager.Services;

public readonly record struct TerminalTabTitleParts(string LeadingText, string TrailingText);

public static class TerminalTabTitle
{
    public const int TrailingTextElementCount = 6;

    public static TerminalTabTitleParts Split(string title)
    {
        ArgumentNullException.ThrowIfNull(title);

        var textElementStarts = StringInfo.ParseCombiningCharacters(title);
        if (textElementStarts.Length <= TrailingTextElementCount)
            return new TerminalTabTitleParts(title, "");

        var trailingStart = textElementStarts[^TrailingTextElementCount];
        return new TerminalTabTitleParts(title[..trailingStart], title[trailingStart..]);
    }
}
