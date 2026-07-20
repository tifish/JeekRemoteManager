using System;
using Avalonia;
using Avalonia.Input;
using SvcSystems.UI.Terminal;

namespace JeekRemoteManager.Views;

/// <summary>
/// Terminal surface used by embedded agent CLIs.
/// Normal-buffer agents such as Codex use the host scrollback even when they
/// temporarily enable mouse tracking; alternate-buffer TUIs keep native wheel handling.
/// </summary>
public sealed class AgentTerminalControl : TerminalControl
{
    /// <summary>Raised after a successful host-history wheel scroll (Codex normal buffer).</summary>
    public event Action? HostHistoryScrolled;

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (ScrollHostHistory(e.Delta))
        {
            e.Handled = true;
            return;
        }

        base.OnPointerWheelChanged(e);
    }

    /// <summary>
    /// Scrolls normal-buffer host history. Public so Debug MCP can exercise the
    /// same branch without synthesizing a platform pointer device.
    /// </summary>
    public bool ScrollHostHistory(Vector delta)
    {
        if (Model is null
            || Model.Terminal.IsAlternateBufferActive
            || delta.Y == 0)
        {
            return false;
        }

        Model.HandlePointerWheel(delta);
        HostHistoryScrolled?.Invoke();
        return true;
    }
}
