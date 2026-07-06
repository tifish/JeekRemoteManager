using CommunityToolkit.Mvvm.ComponentModel;

namespace JeekRemoteManager.ViewModels;

public enum ChatRole
{
    User,
    Assistant,
    System,
    Tool,
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

    public bool HasText => !string.IsNullOrEmpty(Text);

    public bool ShowsThinking => IsAssistant && IsThinking && !HasText;

    public bool ShowsAssistantMarkdown => IsAssistant && !ShowsThinking;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasText))]
    [NotifyPropertyChangedFor(nameof(ShowsThinking))]
    [NotifyPropertyChangedFor(nameof(ShowsAssistantMarkdown))]
    private string _text;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsThinking))]
    [NotifyPropertyChangedFor(nameof(ShowsAssistantMarkdown))]
    private bool _isThinking;

    [ObservableProperty]
    private string _thinkingText = "Thinking...";
}
