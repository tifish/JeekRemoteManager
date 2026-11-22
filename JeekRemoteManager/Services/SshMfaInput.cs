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
    internal bool MatchesPrompt(string? text) =>
        Mode == SshMfaInputMode.MatchPrompt && !string.IsNullOrWhiteSpace(Prompt)
        && (text ?? "").Contains(Prompt, StringComparison.OrdinalIgnoreCase);

    internal bool MatchesShellOutput(string output)
    {
        if (Mode != SshMfaInputMode.MatchPrompt || string.IsNullOrWhiteSpace(Prompt))
            return false;

        var text = LoginMenuSelection.CleanPtyText(output).AsSpan().TrimEnd();
        var lastLine = text[(text.LastIndexOf('\n') + 1)..];
        return lastLine.Contains(Prompt, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>The selected profile and MFA state for one fresh target connection.</summary>
internal sealed class SshMfaAttempt(SshMfaProfileStore.Profile? profile, TimeProvider? clock = null)
{
    private static readonly object ShellCodeGate = new();
    private static readonly Dictionary<Guid, long> ShellCodeExpiryByProfile = [];
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    internal SshMfaInput Input { get; } = profile?.Input ?? new();
    internal bool CodeSubmitted { get; set; }

    internal static SshMfaAttempt? Load(Connection connection) =>
        connection.SshMfaProfileId is { } id ? new(connection.SshMfaProfiles?.TryGetProfile(id)) : null;

    internal string? GenerateCode() => SshTotp.ReadCurrentCode(profile?.EncryptedCredential, _clock)?.Value;

    // A new terminal can dial the same bastion immediately after the last tab closes.
    // Keep the reservation across tabs so that no two shells replay the same code.
    internal SshTotp.TimedCode? ReserveShellCode(out bool waitForNextPeriod)
    {
        waitForNextPeriod = false;
        if (profile is null)
            return null;

        lock (ShellCodeGate)
        {
            var now = _clock.GetUtcNow().ToUnixTimeSeconds();
            if (ShellCodeExpiryByProfile.TryGetValue(profile.Id, out var expiry) && now < expiry)
            {
                waitForNextPeriod = true;
                return null;
            }

            var code = SshTotp.ReadCurrentCode(profile.EncryptedCredential, _clock);
            if (code is not null)
                ShellCodeExpiryByProfile[profile.Id] = code.ExpiresAt;
            return code;
        }
    }

    internal void ReleaseUnusedShellCode(SshTotp.TimedCode code)
    {
        if (profile is null)
            return;

        lock (ShellCodeGate)
        {
            if (ShellCodeExpiryByProfile.TryGetValue(profile.Id, out var expiry) && expiry == code.ExpiresAt)
                ShellCodeExpiryByProfile.Remove(profile.Id);
        }
    }
}
