using System.Globalization;

namespace JeekRemoteManager.Services;

public readonly record struct TerminalTabTitleParts(string LeadingText, string TrailingText);

public readonly record struct TerminalTabTitleEmphasis(int Start, int Length)
{
    public int End => Start + Length;
    public bool IsEmpty => Length <= 0;
}

public static class TerminalTabTitle
{
    public const int TrailingTextElementCount = 6;
    public const int NumericEmphasisMaxDigits = 4;
    private const double MinimumAdjacentSimilarity = 0.6;

    public static TerminalTabTitleParts Split(string title)
    {
        ArgumentNullException.ThrowIfNull(title);

        var textElementStarts = StringInfo.ParseCombiningCharacters(title);
        if (textElementStarts.Length <= TrailingTextElementCount)
            return new TerminalTabTitleParts(title, "");

        var trailingStart = textElementStarts[^TrailingTextElementCount];
        return new TerminalTabTitleParts(title[..trailingStart], title[trailingStart..]);
    }

    public static bool IsPureNumber(string text) =>
        text.Length > 0 && text.All(char.IsDigit);

    public static TerminalTabTitleEmphasis FindNumericDisplayRange(
        string title,
        TerminalTabTitleEmphasis emphasis)
    {
        if (emphasis.IsEmpty
            || emphasis.Start < 0
            || emphasis.End > title.Length
            || !IsPureNumber(title.Substring(emphasis.Start, emphasis.Length)))
        {
            return emphasis;
        }

        var numericStart = emphasis.Start;
        while (numericStart > 0 && char.IsDigit(title[numericStart - 1]))
            numericStart--;

        var numericEnd = emphasis.End;
        while (numericEnd < title.Length && char.IsDigit(title[numericEnd]))
            numericEnd++;

        var displayedDifferenceLength = Math.Min(
            emphasis.Length,
            NumericEmphasisMaxDigits);
        var contextCapacity = NumericEmphasisMaxDigits - displayedDifferenceLength;
        var leftContextLength = Math.Min(contextCapacity, emphasis.Start - numericStart);
        var rightContextLength = Math.Min(
            contextCapacity - leftContextLength,
            numericEnd - emphasis.End);

        return new TerminalTabTitleEmphasis(
            emphasis.Start - leftContextLength,
            displayedDifferenceLength + leftContextLength + rightContextLength);
    }

    public static TerminalTabTitleEmphasis FindEmphasis(
        string title,
        IEnumerable<string> adjacentTitles)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(adjacentTitles);

        var titleStarts = StringInfo.ParseCombiningCharacters(title);
        if (titleStarts.Length == 0)
            return default;

        var titleElements = GetTextElements(title, titleStarts);
        var similarAdjacent = new List<string[]>();
        foreach (var adjacentTitle in adjacentTitles)
        {
            if (string.IsNullOrWhiteSpace(adjacentTitle))
                continue;

            var adjacentStarts = StringInfo.ParseCombiningCharacters(adjacentTitle);
            var adjacentElements = GetTextElements(adjacentTitle, adjacentStarts);
            if (AreSimilar(titleElements, adjacentElements))
                similarAdjacent.Add(adjacentElements);
        }

        if (similarAdjacent.Count == 0)
            return default;

        var commonPrefix = titleElements.Length;
        foreach (var adjacent in similarAdjacent)
            commonPrefix = Math.Min(commonPrefix, CommonPrefixLength(titleElements, adjacent));

        var commonSuffix = titleElements.Length - commonPrefix;
        foreach (var adjacent in similarAdjacent)
        {
            commonSuffix = Math.Min(
                commonSuffix,
                CommonSuffixLength(titleElements, adjacent, commonPrefix));
        }

        // If combining multiple adjacent comparisons leaves less than half of
        // this title in common, they are not one near-identical name family.
        if ((commonPrefix + commonSuffix) * 2 < titleElements.Length)
            return default;

        var distinctEndElement = titleElements.Length - commonSuffix;
        if (distinctEndElement <= commonPrefix)
            return default;

        var start = titleStarts[commonPrefix];
        var end = distinctEndElement < titleStarts.Length
            ? titleStarts[distinctEndElement]
            : title.Length;
        return new TerminalTabTitleEmphasis(start, end - start);
    }

    private static bool AreSimilar(string[] title, string[] adjacent)
    {
        if (title.Length == 0 || adjacent.Length == 0)
            return false;

        var commonPrefix = CommonPrefixLength(title, adjacent);
        var commonSuffix = CommonSuffixLength(title, adjacent, commonPrefix);
        var common = commonPrefix + commonSuffix;
        var longest = Math.Max(title.Length, adjacent.Length);
        return common < longest
               && common >= 2
               && (double)common / longest >= MinimumAdjacentSimilarity;
    }

    private static int CommonPrefixLength(string[] left, string[] right)
    {
        var limit = Math.Min(left.Length, right.Length);
        var length = 0;
        while (length < limit
               && string.Equals(left[length], right[length], StringComparison.OrdinalIgnoreCase))
        {
            length++;
        }

        return length;
    }

    private static int CommonSuffixLength(string[] left, string[] right, int commonPrefix)
    {
        var limit = Math.Min(left.Length, right.Length) - commonPrefix;
        var length = 0;
        while (length < limit
               && string.Equals(
                   left[^(length + 1)],
                   right[^(length + 1)],
                   StringComparison.OrdinalIgnoreCase))
        {
            length++;
        }

        return length;
    }

    private static string[] GetTextElements(string text, int[] starts)
    {
        var elements = new string[starts.Length];
        for (var i = 0; i < starts.Length; i++)
        {
            var end = i + 1 < starts.Length ? starts[i + 1] : text.Length;
            elements[i] = text[starts[i]..end];
        }

        return elements;
    }
}
