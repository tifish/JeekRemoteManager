using System.Text;
using SvcSystems.UI.Terminal;

namespace JeekRemoteManager.Services;

public static class TerminalClipboardText
{
    public static string? BuildSelectedTextWithoutSoftWraps(Terminal terminal)
    {
        var selection = terminal.Selection;
        if (!selection.HasSelection)
            return null;

        var buffer = terminal.Buffer;
        var sb = new StringBuilder();
        var previousSelectedRow = -1;
        var hasSelectedRow = false;

        for (var y = 0; y < buffer.Length; y++)
        {
            var line = buffer.GetLine(y);
            if (line is null)
                continue;

            var firstSelectedColumn = -1;
            var lastSelectedColumn = -1;

            for (var x = 0; x < line.Length; x++)
            {
                if (!selection.IsCellSelected(x, y))
                    continue;

                if (firstSelectedColumn < 0)
                    firstSelectedColumn = x;
                lastSelectedColumn = x;
            }

            if (firstSelectedColumn < 0)
                continue;

            if (hasSelectedRow)
            {
                var continuesSoftWrappedLine = line.IsWrapped && y == previousSelectedRow + 1;
                if (!continuesSoftWrappedLine)
                    sb.Append("\r\n");
            }

            sb.Append(line.TranslateToString(false, firstSelectedColumn, lastSelectedColumn + 1));

            hasSelectedRow = true;
            previousSelectedRow = y;
        }

        return hasSelectedRow ? sb.ToString() : null;
    }
}
