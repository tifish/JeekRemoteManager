using System.Text.RegularExpressions;

namespace JeekRemoteManager.Services;

/// <summary>Where a newly opened bastion shell is sitting before login commands run.</summary>
public enum BastionLandingKind
{
    Unknown,
    /// <summary>Numbered asset/account menu. Typical after SSH auth on a fortress host.</summary>
    Menu,
    /// <summary>A 2FA or password prompt. Sending menu commands here burns the code.</summary>
    AuthPrompt,
    /// <summary>Already inside a target shell. Only then is #reuse-leave valid.</summary>
    Shell,
}

/// <summary>
/// Picks login-command phases for a pooled bastion transport. A switch always
/// runs the previous target's <c>#reuse-leave</c> to completion, then the new
/// target's <c>#reuse-enter</c>. Same-target extra channels start at
/// <c>#duplicate</c>.
/// </summary>
public static class BastionLanding
{
    private static readonly Regex ShellPromptLine = new(
        @"(\S+@\S+[: ].*)?[$#%>]\s*$",
        RegexOptions.Compiled);

    private static readonly string[] AuthPromptKeywords =
    [
        "二次验证",
        "verification code",
        "verify code",
        "one-time",
        "one time",
        "onetime",
        "otp",
        "passcode",
        "authenticator",
        "two-factor",
        "two factor",
        "2fa",
        "动态口令",
        "动态码",
        "验证码",
        "令牌",
    ];

    public static BastionLandingKind Classify(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return BastionLandingKind.Unknown;

        if (LooksLikeAuthPrompt(output))
            return BastionLandingKind.AuthPrompt;
        if (LoginMenuSelection.ParseEntries(output).Count > 0)
            return BastionLandingKind.Menu;
        if (LooksLikeShellPrompt(output))
            return BastionLandingKind.Shell;
        return BastionLandingKind.Unknown;
    }

    /// <summary>
    /// Switch: old <c>#reuse-leave</c>, then new <c>#reuse-enter</c>.
    /// Same target: <c>#duplicate</c> only.
    /// </summary>
    public static IReadOnlyList<string[]> SelectReusePhases(
        bool requiresSwitch,
        string sourceLoginCommands,
        string targetLoginCommands)
    {
        if (requiresSwitch)
        {
            return
            [
                LoginCommandSequence.Select(sourceLoginCommands, LoginCommandSection.ReuseLeave),
                LoginCommandSequence.Select(targetLoginCommands, LoginCommandSection.ReuseEnter),
            ];
        }

        return
        [
            LoginCommandSequence.Select(targetLoginCommands, LoginCommandSection.Duplicate),
        ];
    }

    private static bool LooksLikeAuthPrompt(string output)
    {
        foreach (var keyword in AuthPromptKeywords)
        {
            if (output.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool LooksLikeShellPrompt(string output)
    {
        var lines = output.ReplaceLineEndings("\n").Split('\n');
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i].TrimEnd('\r', ' ', '\t');
            if (line.Length == 0)
                continue;
            return ShellPromptLine.IsMatch(line);
        }

        return false;
    }
}
