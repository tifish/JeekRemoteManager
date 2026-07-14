using System;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;

namespace JeekRemoteManager.ViewModels;

public enum ChatRole
{
    User,
    Assistant,
    System,
    Tool,
    /// <summary>A dangerous command waiting for the user's run/skip decision.</summary>
    Confirmation,
}

/// <summary>One bubble in the AI chat transcript. <see cref="Text"/> is observable so streamed
/// tokens append live.</summary>
public sealed partial class ChatMessageViewModel : ObservableObject
{
    public ChatMessageViewModel(ChatRole role, string text)
    {
        Role = role;
        _text = text;
    }

    public ChatRole Role { get; }

    public bool IsUser => Role == ChatRole.User;

    public bool IsAssistant => Role == ChatRole.Assistant;

    public bool IsSystem => Role == ChatRole.System;

    public bool IsTool => Role == ChatRole.Tool;

    public bool IsConfirmation => Role == ChatRole.Confirmation;

    /// <summary>Rendered as a plain selectable text bubble (user/system/tool); assistant
    /// messages use Markdown and confirmations use their own card layout.</summary>
    public bool IsPlainBubble => Role is ChatRole.User or ChatRole.System or ChatRole.Tool;

    public bool HasText => !string.IsNullOrEmpty(Text);

    public bool ShowsThinking => IsAssistant && IsThinking && !HasText;

    public bool ShowsAssistantMarkdown => IsAssistant && !ShowsThinking;

    /// <summary>Markdown used by the assistant bubble. Some agents emit an opening
    /// fenced-code marker immediately after prose; Markdown requires that marker to
    /// start a line, so repair that boundary for display without changing <see cref="Text"/>.</summary>
    public string RenderedMarkdown => NormalizeMarkdown(Text);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasText))]
    [NotifyPropertyChangedFor(nameof(ShowsThinking))]
    [NotifyPropertyChangedFor(nameof(ShowsAssistantMarkdown))]
    [NotifyPropertyChangedFor(nameof(RenderedMarkdown))]
    private string _text;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsThinking))]
    [NotifyPropertyChangedFor(nameof(ShowsAssistantMarkdown))]
    private bool _isThinking;

    [ObservableProperty]
    private string _thinkingText = "Thinking...";

    /// <summary>Confirmation bubbles only: true while the run/skip buttons are shown.</summary>
    [ObservableProperty]
    private bool _isAwaitingDecision;

    /// <summary>Confirmation bubbles only: the outcome line shown once decided.</summary>
    [ObservableProperty]
    private string? _decisionText;

    /// <summary>Repairs attached opening code fences such as "prose```bash\n".
    /// Public so SmokeTest and Debug MCP can inspect the exact display value.</summary>
    public static string NormalizeMarkdown(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        var output = new StringBuilder(text.Length + 8);
        var position = 0;
        var inFence = false;
        var fenceMarker = '\0';
        var fenceLength = 0;

        while (position < text.Length)
        {
            var newline = text.IndexOf('\n', position);
            var lineEnd = newline >= 0 ? newline : text.Length;
            var contentEnd = lineEnd > position && text[lineEnd - 1] == '\r' ? lineEnd - 1 : lineEnd;
            var line = text.AsSpan(position, contentEnd - position);

            if (inFence)
            {
                if (IsFenceAtLineStart(line, fenceMarker, fenceLength))
                    inFence = false;
            }
            else if (TryFindOpeningFence(line, out var fenceIndex, out fenceMarker, out fenceLength))
            {
                if (fenceIndex > 0)
                    output.Append(line[..fenceIndex]).Append('\n').Append(line[fenceIndex..]);
                else
                    output.Append(line);
                inFence = true;
                goto AppendLineEnding;
            }

            output.Append(line);

        AppendLineEnding:
            if (contentEnd < lineEnd)
                output.Append('\r');
            if (newline >= 0)
                output.Append('\n');
            position = newline >= 0 ? newline + 1 : text.Length;
        }

        return output.ToString();
    }

    private static bool TryFindOpeningFence(ReadOnlySpan<char> line, out int index, out char marker,
        out int length)
    {
        index = -1;
        marker = '\0';
        length = 0;

        for (var i = 0; i <= line.Length - 3; i++)
        {
            if (line[i] is not ('`' or '~'))
                continue;

            var runLength = 1;
            while (i + runLength < line.Length && line[i + runLength] == line[i])
                runLength++;
            if (runLength < 3)
            {
                i += runLength - 1;
                continue;
            }

            // A backtick fence's info string cannot itself contain a backtick.
            if (line[i] == '`' && line[(i + runLength)..].IndexOf('`') >= 0)
            {
                i += runLength - 1;
                continue;
            }

            var prefixIsOnlyIndentation = line[..i].Trim().IsEmpty;
            if (prefixIsOnlyIndentation && i > 3)
            {
                i += runLength - 1;
                continue;
            }

            // Up to three leading spaces already form a valid Markdown fence and
            // must not gain an extra newline. Only a fence attached to prose does.
            index = prefixIsOnlyIndentation ? 0 : i;
            marker = line[i];
            length = runLength;
            return true;
        }

        return false;
    }

    private static bool IsFenceAtLineStart(ReadOnlySpan<char> line, char marker, int openingLength)
    {
        var index = 0;
        while (index < line.Length && index < 3 && line[index] == ' ')
            index++;

        var runLength = 0;
        while (index + runLength < line.Length && line[index + runLength] == marker)
            runLength++;
        if (runLength < openingLength)
            return false;

        return line[(index + runLength)..].Trim().IsEmpty;
    }
}
