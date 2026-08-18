using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using JeekRemoteManager.Models;
using JeekTools;
using Microsoft.Extensions.Logging;
using Renci.SshNet;
using Renci.SshNet.Common;
using SshNet.Agent;
using ZLogger;

namespace JeekRemoteManager.Services;

/// <summary>
/// Builds an SSH.NET <see cref="ConnectionInfo"/> from a <see cref="Connection"/>,
/// authenticating programmatically with the master-password-decrypted credentials
/// so the user never has to type a password. A keyboard-interactive OTP or other
/// second factor is answered in the GUI via <see cref="PromptUser"/>. Shared by
/// the interactive terminal and the non-interactive script runner so both use
/// one auth path.
/// </summary>
public static class SshConnectionFactory
{
    private static readonly ILogger Log = LogManager.CreateLogger(nameof(SshConnectionFactory));

    /// <summary>
    /// Called from the SSH handshake thread for any keyboard-interactive prompt
    /// the stored password cannot answer (OTP, second factor, extra PAM fields).
    /// Return the response, or null to cancel the login.
    /// </summary>
    public static Func<KeyboardInteractiveChallenge, string?>? PromptUser { get; set; }

    /// <summary>One keyboard-interactive prompt the stored password cannot fill.</summary>
    public readonly record struct KeyboardInteractiveChallenge(
        string Host,
        int Port,
        string Username,
        string Request,
        bool IsEchoed,
        string Instruction);

    // Default key file names tried under ~/.ssh, in preference order, when a
    // connection has neither a password nor an explicit key path — mirrors the
    // OpenSSH client's default lookup convention.
    private static readonly string[] DefaultKeyNames = { "id_ed25519", "id_ecdsa", "id_rsa" };

    /// <summary>
    /// Produces a <see cref="ConnectionInfo"/>. Tries, in order: an explicit private
    /// key, then a stored password (+ keyboard-interactive); failing a password, it
    /// falls back to ssh-agent / Pageant identities and the default <c>~/.ssh</c> keys.
    /// </summary>
    /// <exception cref="InvalidOperationException">When no username is set, or no usable credential is available.</exception>
    public static ConnectionInfo Build(Connection connection)
    {
        if (string.IsNullOrWhiteSpace(connection.Host))
            throw new InvalidOperationException("Host is empty.");
        if (string.IsNullOrWhiteSpace(connection.Username))
            throw new InvalidOperationException("Username is required for SSH.");

        var host = connection.Host.Trim();
        var port = connection.Port > 0 ? connection.Port : 22;
        var user = connection.Username.Trim();
        var password = PasswordProtector.Decrypt(connection.EncryptedPassword);
        var passphrase = PasswordProtector.Decrypt(connection.EncryptedPrivateKeyPassphrase);

        var methods = new List<AuthenticationMethod>();
        var hasExplicitKey = !string.IsNullOrWhiteSpace(connection.PrivateKeyPath);
        var explicitKeyProblem = DescribeUnusableExplicitKey(connection);

        // 1. Explicit private key (with optional passphrase).
        if (hasExplicitKey && explicitKeyProblem is null)
        {
            var keyFile = TryLoadKey(connection.PrivateKeyPath, passphrase);
            if (keyFile is null)
            {
                // Present but unreadable: wrong passphrase, or a format SSH.NET cannot
                // parse. Same class of configuration mistake as a missing file.
                explicitKeyProblem =
                    $"Private key file could not be loaded: {connection.PrivateKeyPath}";
            }
            else
            {
                methods.Add(new PrivateKeyAuthenticationMethod(user, keyFile));
            }
        }

        // Report it even when another method will carry the connection. Falling back to
        // a password or an agent key without a word means a broken key path stays hidden
        // until the day it is the only credential left, and then it fails at connect time
        // with no history of having ever been wrong.
        if (explicitKeyProblem is not null)
            Log.ZLogWarning($"{explicitKeyProblem} (connection '{connection.Name}')");

        if (!string.IsNullOrEmpty(password))
        {
            // 2. Stored password. Many sshd setups expose password auth only via
            //    PAM/keyboard-interactive, so that method is attached below.
            methods.Add(new PasswordAuthenticationMethod(user, password));
        }
        else
        {
            // 3. No password: fall back to key-based auth following the OpenSSH client
            //    convention — ssh-agent / Pageant identities, then the default
            //    ~/.ssh keys. Gated on "no
            //    password" so a connection that does use a password isn't sprayed
            //    with extra key attempts (which can trip the server's MaxAuthTries).
            var agentKeys = TryGetAgentKeys();
            if (agentKeys.Count > 0)
                methods.Add(new PrivateKeyAuthenticationMethod(user, agentKeys.ToArray()));

            if (!hasExplicitKey)
            {
                foreach (var keyFile in LoadDefaultKeys(passphrase))
                    methods.Add(new PrivateKeyAuthenticationMethod(user, keyFile));
            }
        }

        if (methods.Count == 0)
        {
            throw new InvalidOperationException(
                explicitKeyProblem
                ?? "No usable credential: set a password or private key, "
                   + "or load a key into ssh-agent / Pageant.");
        }

        // Keyboard-interactive last so a working key or password is tried first.
        // Jump hosts then ask for an OTP on a later KI round (or as extra prompts
        // in the same round); those are answered in the GUI, never with the
        // stored password.
        var keyboard = new KeyboardInteractiveAuthenticationMethod(user);
        var conversation = new KeyboardInteractiveConversation();
        var context = new KeyboardInteractiveChallenge(host, port, user, "", false, "");
        keyboard.AuthenticationPrompt += (_, e) =>
            HandleAuthenticationPrompt(e, password, conversation, context, PromptUser);
        methods.Add(keyboard);

        return new ConnectionInfo(host, port, user, methods.ToArray());
    }

    /// <summary>
    /// Describes a configured private key that cannot be used, or null when the
    /// connection has no explicit key path or the file is there. Path-only, so it is
    /// deterministic and safe to call without the master password unlocked.
    /// </summary>
    public static string? DescribeUnusableExplicitKey(Connection connection)
    {
        var path = connection.PrivateKeyPath;
        if (string.IsNullOrWhiteSpace(path))
            return null;

        return File.Exists(path) ? null : $"Private key file not found: {path}";
    }

    /// <summary>Per-authentication state: keyboard-interactive is a multi-round
    /// conversation, and what is safe to answer depends on what came before.</summary>
    internal sealed class KeyboardInteractiveConversation
    {
        public bool PasswordSupplied { get; set; }
    }

    /// <summary>
    /// Connect-time keyboard-interactive handler: fill password prompts from the
    /// stored secret, then ask the user for anything left (OTP, second factor).
    /// Throws instead of returning with a null <see cref="AuthenticationPrompt.Response"/>,
    /// which SSH.NET otherwise surfaces as an unreadable ArgumentNullException.
    /// </summary>
    internal static void HandleAuthenticationPrompt(
        AuthenticationPromptEventArgs e,
        string password,
        KeyboardInteractiveConversation conversation,
        KeyboardInteractiveChallenge context,
        Func<KeyboardInteractiveChallenge, string?>? askUser)
    {
        if (!string.IsNullOrEmpty(password))
            AnswerPasswordPrompts(e, password, conversation);

        CompleteRemainingPrompts(
            e,
            context with { Instruction = e.Instruction ?? "" },
            askUser);
    }

    /// <summary>
    /// Fills every still-unanswered prompt through <paramref name="askUser"/>.
    /// A null return (or a missing callback) cancels the login with a message
    /// that names the server's prompt, rather than leaving Response null.
    /// </summary>
    internal static void CompleteRemainingPrompts(
        AuthenticationPromptEventArgs e,
        KeyboardInteractiveChallenge context,
        Func<KeyboardInteractiveChallenge, string?>? askUser)
    {
        foreach (var prompt in e.Prompts)
        {
            if (prompt.Response is not null)
                continue;

            var request = prompt.Request ?? "";
            if (askUser is null)
            {
                throw new InvalidOperationException(
                    $"The server asked for \"{request.Trim()}\" during SSH authentication.");
            }

            var response = askUser(
                context with
                {
                    Request = request,
                    IsEchoed = prompt.IsEchoed,
                });
            if (response is null)
            {
                throw new InvalidOperationException(
                    $"SSH authentication cancelled ({request.Trim()}).");
            }

            prompt.Response = response;
        }
    }

    /// <summary>
    /// Fills in keyboard-interactive prompts. Matching the English word "password" is
    /// only a heuristic: a server running under a non-English locale asks for "密码" or
    /// "Passwort", and the request text is whatever PAM was configured to print. So the
    /// keyword is a strong hint, and an unlabelled single hidden prompt is answered too.
    ///
    /// Two things are never answered with the password:
    ///
    /// Echoed prompts, which ask for a user name or an OTP — sending the password there
    /// puts it in the server's logs.
    ///
    /// Anything that reads as a second factor. MFA's second round is usually one hidden
    /// "Verification code:" prompt and nothing else, which is shaped exactly like the
    /// unlabelled password challenge the fallback exists for. Submitting the password as
    /// the code fails the login and burns an attempt against the lockout counter, so
    /// second-factor wording is refused outright, and the fallback additionally gives up
    /// once the password has already been supplied in this conversation.
    /// </summary>
    internal static void AnswerPasswordPrompts(
        AuthenticationPromptEventArgs e,
        string password,
        KeyboardInteractiveConversation conversation)
    {
        var prompts = e.Prompts.ToArray();
        var answered = false;
        foreach (var prompt in prompts)
        {
            if (prompt.IsEchoed
                || LooksLikeSecondFactorPrompt(prompt.Request)
                || !LooksLikePasswordPrompt(prompt.Request))
            {
                continue;
            }

            prompt.Response = password;
            answered = true;
        }

        if (answered)
        {
            conversation.PasswordSupplied = true;
            return;
        }

        // The password has already gone out, so a further unlabelled hidden prompt is
        // the next factor, not a retry of the first one.
        if (conversation.PasswordSupplied)
            return;

        var hidden = prompts
            .Where(prompt => !prompt.IsEchoed && !LooksLikeSecondFactorPrompt(prompt.Request))
            .ToArray();
        if (hidden.Length != 1 || prompts.Any(prompt => LooksLikeSecondFactorPrompt(prompt.Request)))
            return;

        hidden[0].Response = password;
        conversation.PasswordSupplied = true;
    }

    private static readonly string[] PasswordPromptKeywords =
    [
        "password",   // English, and the default PAM prompt regardless of locale
        "passwort",   // German
        "mot de passe", // French
        "contraseña", // Spanish
        "senha",      // Portuguese
        "пароль",     // Russian
        "密码",        // Simplified Chinese
        "密碼",        // Traditional Chinese
        "パスワード",    // Japanese
        "비밀번호",      // Korean
    ];

    private static bool LooksLikePasswordPrompt(string request) =>
        PasswordPromptKeywords.Any(keyword =>
            request.Contains(keyword, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Wording used by the second factor of an MFA login. Deliberately specific phrases:
    /// a bare "code" would also match "passcode", and mis-classifying the first-round
    /// password prompt would break plain password logins.
    /// </summary>
    private static readonly string[] SecondFactorPromptKeywords =
    [
        "verification code",
        "verify code",
        "one-time",
        "one time",
        "onetime",
        "otp",
        "passcode",
        "security code",
        "authenticator",
        "two-factor",
        "two factor",
        "second factor",
        "2fa",
        "duo",
        "yubikey",
        "token:",             // "Token:" as a whole prompt, not "token" inside prose
        "验证码",              // Simplified/Traditional Chinese
        "二次验证",            // "二次验证密码" also contains 密码; this must win
        "动态口令",
        "动态码",
        "令牌",
        "確認コード",           // Japanese
        "ワンタイム",
        "인증 코드",            // Korean
        "일회용",
        "bestätigungscode",   // German
        "einmalcode",
        "code de vérification", // French
        "code à usage unique",
        "código de verificación", // Spanish
        "код подтверждения",  // Russian
        "одноразов",
    ];

    private static bool LooksLikeSecondFactorPrompt(string request) =>
        SecondFactorPromptKeywords.Any(keyword =>
            request.Contains(keyword, StringComparison.OrdinalIgnoreCase));

    private static PrivateKeyFile? TryLoadKey(string path, string? passphrase)
    {
        try
        {
            return string.IsNullOrEmpty(passphrase)
                ? new PrivateKeyFile(path)
                : new PrivateKeyFile(path, passphrase);
        }
        catch
        {
            // Encrypted key without the right passphrase, or an unsupported format.
            return null;
        }
    }

    private static IEnumerable<PrivateKeyFile> LoadDefaultKeys(string? passphrase)
    {
        string sshDir;
        try
        {
            sshDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        }
        catch
        {
            yield break;
        }

        foreach (var name in DefaultKeyNames)
        {
            var path = Path.Combine(sshDir, name);
            if (!File.Exists(path))
                continue;

            var keyFile = TryLoadKey(path, passphrase);
            if (keyFile is not null)
                yield return keyFile;
        }
    }

    private static List<IPrivateKeySource> TryGetAgentKeys()
    {
        var keys = new List<IPrivateKeySource>();
        AddAgentKeys(keys, () => new SshAgent().RequestIdentities());   // OpenSSH agent
        if (IsPageantRunning())
            AddAgentKeys(keys, () => new Pageant().RequestIdentities());    // PuTTY Pageant
        return keys;
    }

    private static void AddAgentKeys(List<IPrivateKeySource> keys, Func<IEnumerable<IPrivateKeySource>> fetch)
    {
        try
        {
            // Run the agent IPC on a worker and bound it: a missing or unresponsive
            // agent must not stall the connection. Materialize inside the task so the
            // actual IPC runs under the timeout, not later during AddRange.
            var task = Task.Run(() => new List<IPrivateKeySource>(fetch()));
            if (task.Wait(TimeSpan.FromSeconds(2)))
                keys.AddRange(task.Result);
            else
                _ = task.ContinueWith(static t => { _ = t.Exception; }, TaskScheduler.Default);
        }
        catch
        {
            // Agent not running, not available on this platform, or timed out — ignore
            // and let the other methods carry the connection.
        }
    }

    private static bool IsPageantRunning() =>
        OperatingSystem.IsWindows() && FindWindow("Pageant", "Pageant") != IntPtr.Zero;

    [DllImport("user32.dll", EntryPoint = "FindWindowA", CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);
}
