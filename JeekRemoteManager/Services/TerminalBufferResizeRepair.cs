using XTerm.Buffer;

namespace JeekRemoteManager.Services;

/// <summary>
/// Works around XTerm.NET <see cref="TerminalBuffer.Resize"/> cursor bugs.
/// When the viewport loses rows the library only clamps the relative cursor row,
/// leaving it on older screen text. When the viewport gains rows (e.g. maximize)
/// it also shrinks <c>YBase</c> without advancing the relative row, so the cursor
/// lands on history that still has content; the shell's prompt redraw then
/// overwrites that text.
/// </summary>
public static class TerminalBufferResizeRepair
{
    /// <summary>
    /// Restores the cursor to the buffer line it occupied before a resize.
    /// </summary>
    /// <param name="buffer">Buffer after the library has already resized it.</param>
    /// <param name="previousAbsoluteCursorRow">
    /// <c>YBase + Y</c> captured before the resize (or after the last feed).
    /// </param>
    /// <param name="previousRelativeCursorRow">
    /// Relative <c>Y</c> captured before the resize; used only as a fallback.
    /// </param>
    /// <param name="newRows">Viewport row count after the resize.</param>
    /// <returns><c>true</c> when the buffer was modified.</returns>
    public static bool TryRepair(
        TerminalBuffer buffer,
        int previousAbsoluteCursorRow,
        int previousRelativeCursorRow,
        int newRows)
    {
        if (newRows <= 0)
            return false;

        var desiredAbsolute = previousAbsoluteCursorRow;
        var currentAbsolute = buffer.YBase + buffer.Y;
        if (desiredAbsolute == currentAbsolute)
            return false;

        // Keep the cursor on the same absolute line when that line is still inside
        // the viewport (typical maximize / grow case).
        var targetY = desiredAbsolute - buffer.YBase;
        if (targetY >= 0 && targetY < newRows)
        {
            buffer.SetCursorRaw(buffer.X, targetY);
            return true;
        }

        // Absolute line sits below the viewport (typical shrink): scroll it to the
        // bottom row, matching the old relative-shift workaround.
        if (targetY >= newRows)
        {
            buffer.ScrollUp(targetY - (newRows - 1));
            buffer.SetCursorRaw(buffer.X, newRows - 1);
            return true;
        }

        // Absolute line is above the viewport; fall back to the pre-resize relative
        // row so a shrink still recovers the prompt line when YBase moved.
        var relativeShift = previousRelativeCursorRow - (newRows - 1);
        if (relativeShift > 0)
        {
            buffer.ScrollUp(relativeShift);
            buffer.SetCursorRaw(buffer.X, newRows - 1);
            return true;
        }

        return false;
    }
}
