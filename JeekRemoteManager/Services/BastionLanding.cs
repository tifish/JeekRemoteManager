using System.Text.RegularExpressions;

namespace JeekRemoteManager.Services;

/// <summary>Where a reused channel has to pick up the login workflow.</summary>
public enum BastionReuseStart
{
    /// <summary>Already inside the wanted target: post-arrival commands only.</summary>
    Duplicate,
    /// <summary>At the bastion, no target entered: run the target's <c>#reuse-enter</c>.</summary>
    Enter,
    /// <summary>Inside another or an uncertain target: leave it first, then enter.</summary>
    Switch,
}

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
        // Plain credential prompts count too: this bastion's second factor is just
        // "2nd Password:", which matches none of the words above.
        "password:",
        "password：",
        "passphrase",
        "密码：",
        "密码:",
    ];

    public static BastionLandingKind Classify(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return BastionLandingKind.Unknown;

        // This is PTY text: the prompt is colored, the window title arrives as an OSC
        // sequence, and bash turns on bracketed paste right before printing. Matching
        // the raw bytes would classify every real shell as Unknown.
        var clean = LoginMenuSelection.CleanPtyText(output);
        var lastLine = LastNonEmptyLine(clean);

        if (LooksLikeAuthPrompt(lastLine))
            return BastionLandingKind.AuthPrompt;
        if (LoginMenuSelection.ParseEntries(output).Count > 0)
            return BastionLandingKind.Menu;
        if (ShellPromptLine.IsMatch(lastLine))
            return BastionLandingKind.Shell;
        return BastionLandingKind.Unknown;
    }

    /// <summary>
    /// Corrects the pool's expectation with what the new channel actually landed on.
    /// An unreadable landing is not a reason to give up: the remembered route is still
    /// the best information there is, and dropping the transport would cost the user
    /// another two-factor login.
    /// </summary>
    public static IReadOnlyList<string[]> SelectReusePhases(
        BastionLandingKind landing,
        BastionReuseStart start,
        string sourceLoginCommands,
        string targetLoginCommands) =>
        landing switch
        {
            // At the menu nothing has to be left, whatever the pool remembered.
            BastionLandingKind.Menu => SelectReusePhases(
                BastionReuseStart.Enter,
                sourceLoginCommands,
                targetLoginCommands),
            // The bastion is asking for credentials again: this channel needs the whole
            // fresh workflow, starting at #input, not a menu command that would be typed
            // into a password field.
            BastionLandingKind.AuthPrompt =>
            [
                LoginCommandSequence.Select(targetLoginCommands, LoginCommandSection.Fresh),
            ],
            // A target shell (or unreadable output): only the pool knows which target
            // that is, so go with the route.
            _ => SelectReusePhases(start, sourceLoginCommands, targetLoginCommands),
        };

    /// <summary>
    /// Switch: old <c>#reuse-leave</c>, then new <c>#reuse-enter</c>.
    /// Entry: <c>#reuse-enter</c> only — there is nothing to leave.
    /// Same target: <c>#duplicate</c> only.
    /// </summary>
    public static IReadOnlyList<string[]> SelectReusePhases(
        BastionReuseStart start,
        string sourceLoginCommands,
        string targetLoginCommands) =>
        start switch
        {
            BastionReuseStart.Duplicate =>
            [
                LoginCommandSequence.Select(targetLoginCommands, LoginCommandSection.Duplicate),
            ],
            BastionReuseStart.Enter =>
            [
                LoginCommandSequence.Select(targetLoginCommands, LoginCommandSection.ReuseEnter),
            ],
            _ =>
            [
                LoginCommandSequence.Select(sourceLoginCommands, LoginCommandSection.ReuseLeave),
                LoginCommandSequence.Select(targetLoginCommands, LoginCommandSection.ReuseEnter),
            ],
        };

    /// <summary>
    /// Only the last line is examined: a credential prompt is what the screen is
    /// waiting on, while the same words scrolling by in a shell mean nothing.
    /// </summary>
    private static bool LooksLikeAuthPrompt(string lastLine)
    {
        foreach (var keyword in AuthPromptKeywords)
        {
            if (lastLine.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string LastNonEmptyLine(string output)
    {
        var lines = output.ReplaceLineEndings("\n").Split('\n');
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i].TrimEnd('\r', ' ', '\t');
            if (line.Length != 0)
                return line;
        }

        return "";
    }
}
