using SvcSystems.UI.Terminal;

namespace JeekRemoteManager.Services;

/// <summary>Restores a terminal buffer to one blank viewport with no scrollback.</summary>
public static class TerminalScrollbackReset
{
    public static void Reset(TerminalControlModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var terminal = model.Terminal;
        var buffer = terminal.Buffer;

        terminal.Selection.ClearSelection();
        buffer.Lines.Clear();
        buffer.Resize(terminal.Cols, terminal.Rows);
        buffer.SetCursor(0, 0);
        buffer.ResetScrollRegion();
        model.UpdateDisplay();
    }
}
