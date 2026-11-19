using System.Text.Json.Serialization;
using JeekRemoteManager.Models;

namespace JeekRemoteManager.Services;

[JsonConverter(typeof(JsonStringEnumConverter<SshMfaInputMode>))]
internal enum SshMfaInputMode
{
    SshAuthentication,
    MatchPrompt,
    OnShellOpen,
}

internal sealed record SshMfaInput(SshMfaInputMode Mode = SshMfaInputMode.SshAuthentication, string Prompt = "")
{
    internal bool MatchesPrompt(string text) =>
        Mode == SshMfaInputMode.MatchPrompt && !string.IsNullOrWhiteSpace(Prompt)
        && text.Contains(Prompt, StringComparison.OrdinalIgnoreCase);

    internal bool MatchesShellOutput(string output)
    {
        var text = LoginMenuSelection.CleanPtyText(output).AsSpan().TrimEnd();
        var lastLine = text[(text.LastIndexOf('\n') + 1)..];
        return Mode == SshMfaInputMode.MatchPrompt && !string.IsNullOrWhiteSpace(Prompt)
            && lastLine.Contains(Prompt, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>The selected profile and MFA state for one fresh target connection.</summary>
internal sealed class SshMfaAttempt(SshMfaProfileStore.Profile? profile)
{
    internal SshMfaInput Input { get; } = profile?.Input ?? new();
    internal bool CodeSubmitted { get; set; }

    internal static SshMfaAttempt? Load(Connection connection) =>
        connection.SshMfaProfileId is { } id ? new(connection.SshMfaProfiles?.TryGetProfile(id)) : null;

    internal string? GenerateCode() => SshTotp.ReadCurrentCode(profile?.EncryptedCredential)?.Value;
}
