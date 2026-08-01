namespace JeekRemoteManager.Services;

/// <summary>One login-command <c>#</c> directive offered by the editor autocomplete.</summary>
public sealed record LoginCommandCompletion(
    string DisplayText,
    string InsertText,
    string HelpLocalizationKey)
{
    /// <summary>The bare directive token (text before the first space in <see cref="DisplayText"/>).</summary>
    public string Directive => DisplayText.Split(' ', 2)[0];

    public override string ToString() => DisplayText;
}
