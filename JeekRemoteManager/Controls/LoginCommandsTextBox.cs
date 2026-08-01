using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using JeekRemoteManager.Services;

namespace JeekRemoteManager.Controls;

/// <summary>
/// Multi-line login-command editor that offers popup autocomplete for <c>#</c> directives
/// at the start of a line (leading whitespace allowed).
/// </summary>
public sealed class LoginCommandsTextBox : TextBox
{
    private readonly ListBox _completionList;
    private readonly Border _completionBorder;
    private readonly Popup _completionPopup;

    private LoginCommandCompletion[] _matches = [];
    private int _completionStart = -1;
    private int _completionEnd = -1;

    /// <summary>
    /// True while <see cref="AcceptDirectiveCompletion"/> rewrites Text/caret so nested
    /// change handlers do not reopen the popup for the just-inserted token.
    /// </summary>
    private bool _suppressCompletion;

    /// <summary>True while an arrow/Home/End key is moving the caret (must not open the list).</summary>
    private bool _navigationCaretMove;

    /// <summary>Generation token so deferred text-change updates can be cancelled.</summary>
    private int _textChangeVersion;

    protected override Type StyleKeyOverride => typeof(TextBox);

    public bool IsDirectiveCompletionOpen => _completionPopup.IsOpen;

    public bool IsDirectiveCompletionUsingOverlayLayer => _completionPopup.IsUsingOverlayLayer;

    public bool HasDirectiveCompletionBackground => _completionBorder.Background is not null;

    public Rect DirectiveCompletionBounds => _completionBorder.Bounds;

    public int DirectiveCompletionRenderedItemCount =>
        _completionList.GetVisualDescendants()
            .OfType<TextBlock>()
            .Count(text => !string.IsNullOrEmpty(text.Text) && text.Bounds.Width > 0);

    public string DirectiveCompletionRenderedItems =>
        string.Join(
            " | ",
            _completionList.GetVisualDescendants()
                .OfType<TextBlock>()
                .Where(text => !string.IsNullOrEmpty(text.Text))
                .Select(text => $"{text.Text}:{text.Foreground}"));

    public string[] DirectiveCompletionItems =>
        _matches.Select(item => item.DisplayText).ToArray();

    public LoginCommandsTextBox()
    {
        _completionList = new ListBox
        {
            MinWidth = 220,
            MaxHeight = 260,
            FontFamily = new FontFamily("Consolas"),
            ItemTemplate = new FuncDataTemplate<LoginCommandCompletion>(
                (item, _) => CreateCompletionItem(item),
                supportsRecycling: false),
        };
        _completionList.PointerReleased += OnCompletionPointerReleased;

        _completionBorder = CreatePopupBorder();
        _completionPopup = new Popup
        {
            PlacementTarget = this,
            Placement = PlacementMode.BottomEdgeAlignedLeft,
            VerticalOffset = 4,
            IsLightDismissEnabled = false,
            ShouldUseOverlayLayer = true,
            TakesFocusFromNativeControl = false,
            Child = _completionBorder,
        };
        LogicalChildren.Add(_completionPopup);

        TextChanged += OnEditorTextChanged;
        LostFocus += OnEditorLostFocus;
    }

    private Border CreatePopupBorder()
    {
        var border = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(2),
            BoxShadow = new BoxShadows(
                new BoxShadow
                {
                    Blur = 10,
                    OffsetY = 3,
                    Color = Color.FromArgb(56, 0, 0, 0),
                }),
            Child = _completionList,
        };
        return border;
    }

    /// <summary>
    /// Inserts the selected completion at the active token range. Returns false when the
    /// popup is closed or nothing is selected.
    /// </summary>
    public bool AcceptDirectiveCompletion()
    {
        if (!_completionPopup.IsOpen
            || _completionList.SelectedItem is not LoginCommandCompletion selected
            || _completionStart < 0
            || _completionEnd < _completionStart)
        {
            return false;
        }

        var text = Text ?? "";
        if (_completionEnd > text.Length)
            return false;

        var insert = selected.InsertText;
        var insertAt = _completionStart;
        _suppressCompletion = true;
        try
        {
            Text = text[..insertAt] + insert + text[_completionEnd..];
            CaretIndex = insertAt + insert.Length;
            SelectionStart = CaretIndex;
            SelectionEnd = CaretIndex;
            CloseCompletion();
        }
        finally
        {
            _suppressCompletion = false;
        }

        Focus();
        return true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_completionPopup.IsOpen)
        {
            switch (e.Key)
            {
                case Key.Tab:
                case Key.Enter:
                    e.Handled = AcceptDirectiveCompletion();
                    if (e.Handled)
                        return;
                    break;
                case Key.Escape:
                    CloseCompletion();
                    e.Handled = true;
                    return;
                case Key.Up:
                    MoveSelection(-1);
                    e.Handled = true;
                    return;
                case Key.Down:
                    MoveSelection(1);
                    e.Handled = true;
                    return;
            }
        }

        var navigation = IsCaretNavigationKey(e.Key);
        if (navigation)
            _navigationCaretMove = true;
        try
        {
            base.OnKeyDown(e);
        }
        finally
        {
            if (navigation)
                _navigationCaretMove = false;
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != CaretIndexProperty || _suppressCompletion)
            return;

        // Arrow-key navigation never opens the list. Other caret moves only refresh when
        // the popup is already open (e.g. mouse click inside the current # token).
        if (_navigationCaretMove || _completionPopup.IsOpen)
            UpdateCompletion(allowOpen: false);
    }

    private void OnEditorTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppressCompletion)
            return;

        // Defer until after any companion CaretIndex update from the same text edit,
        // so the prefix is read with the final caret position.
        var version = ++_textChangeVersion;
        Dispatcher.UIThread.Post(
            () =>
            {
                if (version != _textChangeVersion || _suppressCompletion)
                    return;
                UpdateCompletion(allowOpen: true);
            },
            DispatcherPriority.Input);
    }

    private void OnEditorLostFocus(object? sender, RoutedEventArgs e)
    {
        if (!_completionList.IsKeyboardFocusWithin)
            CloseCompletion();
    }

    private static bool IsCaretNavigationKey(Key key) =>
        key is Key.Up or Key.Down or Key.Left or Key.Right
            or Key.Home or Key.End or Key.PageUp or Key.PageDown;

    /// <param name="allowOpen">
    /// When false, an already-closed popup stays closed (used for caret navigation).
    /// Text edits pass true so typing <c>#</c> can open the list.
    /// </param>
    private void UpdateCompletion(bool allowOpen)
    {
        if (!IsFocused)
        {
            CloseCompletion();
            return;
        }

        var text = Text ?? "";
        var caret = Math.Clamp(CaretIndex, 0, text.Length);
        if (!TryGetDirectiveAtCaret(text, caret, out var start, out var end, out var prefix))
        {
            CloseCompletion();
            return;
        }

        var matches = LoginCommandSequence.CompleteDirective(prefix);
        if (matches.Length == 0)
        {
            CloseCompletion();
            return;
        }

        // Fully typed a unique directive (e.g. after accepting #reuse-enter) — hide the list.
        if (matches.Length == 1
            && matches[0].Directive.Equals(prefix, StringComparison.OrdinalIgnoreCase))
        {
            CloseCompletion();
            return;
        }

        if (!allowOpen && !_completionPopup.IsOpen)
            return;

        _matches = matches;
        _completionStart = start;
        _completionEnd = end;
        ApplyPopupTheme();
        _completionList.ItemsSource = _matches;
        if (_completionList.SelectedIndex < 0 || _completionList.SelectedIndex >= _matches.Length)
            _completionList.SelectedIndex = 0;
        _completionPopup.IsOpen = true;
    }

    private void ApplyPopupTheme()
    {
        if (this.TryFindResource("PopupSurfaceBrush", out var surface)
            && surface is IBrush surfaceBrush)
        {
            _completionBorder.Background = surfaceBrush;
            _completionList.Background = surfaceBrush;
        }
        else
        {
            _completionBorder.Background = Background;
            _completionList.Background = Background;
        }

        if (this.TryFindResource("PopupBorderBrush", out var border)
            && border is IBrush borderBrush)
        {
            _completionBorder.BorderBrush = borderBrush;
        }
        else
        {
            _completionBorder.BorderBrush = BorderBrush;
        }

        if (this.TryFindResource("TextPrimaryBrush", out var foreground)
            && foreground is IBrush foregroundBrush)
        {
            _completionList.Foreground = foregroundBrush;
        }
        else
        {
            _completionList.Foreground = Foreground;
        }
    }

    private Control CreateCompletionItem(LoginCommandCompletion? item) =>
        new TextBlock
        {
            Text = item?.DisplayText ?? "",
            Foreground = _completionList.Foreground,
            FontFamily = _completionList.FontFamily,
            Padding = new Thickness(10, 6),
            VerticalAlignment = VerticalAlignment.Center,
        };

    /// <summary>
    /// Resolves the <c>#</c> token under the caret when it is the first non-whitespace token
    /// on the current line and the prefix has not yet reached an argument separator.
    /// </summary>
    internal static bool TryGetDirectiveAtCaret(
        string text,
        int caret,
        out int start,
        out int end,
        out string prefix)
    {
        start = -1;
        end = -1;
        prefix = "";
        if (caret < 0 || caret > text.Length)
            return false;

        var lineStart = caret == 0 ? 0 : text.LastIndexOf('\n', caret - 1) + 1;
        var hash = text.IndexOf('#', lineStart, caret - lineStart);
        if (hash < 0)
            return false;

        for (var i = lineStart; i < hash; i++)
        {
            if (!char.IsWhiteSpace(text[i]))
                return false;
        }

        prefix = text[hash..caret];
        if (prefix.Any(char.IsWhiteSpace))
            return false;

        start = hash;
        end = caret;
        while (end < text.Length && !char.IsWhiteSpace(text[end]))
            end++;
        return true;
    }

    private void MoveSelection(int delta)
    {
        if (_matches.Length == 0)
            return;

        var index = Math.Max(0, _completionList.SelectedIndex);
        _completionList.SelectedIndex = (index + delta + _matches.Length) % _matches.Length;
        if (_completionList.SelectedItem is { } selected)
            _completionList.ScrollIntoView(selected);
    }

    private void OnCompletionPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_completionList.SelectedItem is not null)
        {
            AcceptDirectiveCompletion();
            e.Handled = true;
        }
    }

    private void CloseCompletion()
    {
        _completionPopup.IsOpen = false;
        _completionStart = -1;
        _completionEnd = -1;
        _matches = [];
        _completionList.ItemsSource = null;
    }
}
