using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.Media;
using SvcSystems.UI.Terminal;

namespace JeekRemoteManager.Views;

/// <summary>
/// Minimal Avalonia IME client for <see cref="TerminalControl"/>. Without a registered
/// <see cref="TextInputMethodClient"/>, Windows never attaches Chinese/Japanese/Korean IMEs
/// to the control, so Shift language toggle and composition fail. Committed characters still
/// flow through the control's existing <c>OnTextInput</c> → <c>Model.Send</c> path.
/// </summary>
internal sealed class TerminalTextInputMethodClient : TextInputMethodClient
{
    private readonly TerminalControl _term;

    public TerminalTextInputMethodClient(TerminalControl term)
    {
        _term = term ?? throw new ArgumentNullException(nameof(term));
    }

    public override Visual TextViewVisual => _term;

    public override bool SupportsPreedit => true;

    public override bool SupportsSurroundingText => false;

    public override string SurroundingText => string.Empty;

    public override Rect CursorRectangle
    {
        get
        {
            var model = _term.Model;
            if (model is null)
                return default;

            var cellW = Math.Max(_term.FontSize * 0.6, 6.0);
            var cellH = Math.Max(_term.FontSize * 1.4, 10.0);
            var col = Math.Max(0, model.CaretColumn);
            var row = Math.Max(0, model.CaretRow);
            // Candidate / composition window anchors near the terminal caret cell.
            return new Rect(col * cellW, row * cellH, Math.Max(cellW, 1), cellH);
        }
    }

    public override TextSelection Selection
    {
        get => default;
        set { }
    }

    public override void SetPreeditText(string? preeditText) =>
        SetPreeditText(preeditText, null);

    public override void SetPreeditText(string? preeditText, int? cursorPos)
    {
        // TerminalControl has no preedit underline surface. Platform IME still shows its
        // candidate UI; committed text arrives via TextInput → Model.Send (UTF-8).
        RaiseCursorRectangleChanged();
    }

    /// <summary>
    /// Enables system IME on a terminal and answers
    /// <see cref="InputElement.TextInputMethodClientRequestedEvent"/>.
    /// Call once per control instance (main shell and AI CLI).
    /// </summary>
    public static void Attach(TerminalControl term)
    {
        ArgumentNullException.ThrowIfNull(term);

        InputMethod.SetIsInputMethodEnabled(term, true);
        TextInputOptions.SetContentType(term, TextInputContentType.Normal);
        TextInputOptions.SetMultiline(term, true);

        var client = new TerminalTextInputMethodClient(term);
        term.AddHandler(
            InputElement.TextInputMethodClientRequestedEvent,
            (_, e) => { e.Client = client; },
            handledEventsToo: true);

        term.GotFocus += (_, _) => client.RaiseCursorRectangleChanged();
        term.PointerPressed += (_, _) =>
        {
            client.RaiseInputPaneActivationRequested();
            client.RaiseCursorRectangleChanged();
        };
    }
}
