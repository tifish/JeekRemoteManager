using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Jeek.Avalonia.Localization;
using JeekRemoteManager.Controls;
using JeekRemoteManager.Models;
using JeekRemoteManager.ViewModels;
using JeekTools;
using JeekRemoteManager.Views;
using Microsoft.Extensions.Logging;
using Renci.SshNet;
using Renci.SshNet.Common;
using ZLogger;

namespace JeekRemoteManager.Services;

/// <summary>SSH probes: authentication, host keys, SFTP, jump hosts and port forwarding.</summary>
internal static partial class DebugMcpServer
{
    private static async Task<JsonObject> SftpRetryPolicyCheckAsync()
    {
        var session = new RetryPolicyProbeSession();
        var viewModel = await OnUiAsync(() => new FileBrowserViewModel(
            () => session,
            _ => { },
            "probe@localhost"));

        async Task Run(string label, Func<Task> action)
        {
            session.CurrentLabel = label;
            try
            {
                await action();
            }
            catch
            {
                // The probe transport always fails the first attempt; what matters is
                // the recorded policy and attempt count, not the surfaced error.
            }
        }

        await Run("listing", () => OnUiAsync(() => viewModel.RefreshCommand.ExecuteAsync(null)).Unwrap());
        await Run(
            "delete",
            () => OnUiAsync(() => viewModel.DebugRunBrowseOperationAsync(
                ops => ops.DeleteFile("/tmp/probe"))).Unwrap());
        await Run(
            "rename",
            () => OnUiAsync(() => viewModel.DebugRunBrowseOperationAsync(
                ops => ops.RenameFile("/tmp/a", "/tmp/b"))).Unwrap());
        await Run(
            "mkdir",
            () => OnUiAsync(() => viewModel.DebugRunBrowseOperationAsync(
                ops => ops.CreateDirectory("/tmp/probe-dir"))).Unwrap());

        await OnUiAsync(() => { viewModel.Dispose(); return true; });

        var calls = session.Calls;
        var failures = new List<string>();
        void Require(string label, FileSystemRetry expected)
        {
            var matching = calls.Where(call => call.Label == label).ToArray();
            if (matching.Length == 0)
            {
                failures.Add($"{label}: no session call recorded");
                return;
            }

            foreach (var call in matching)
            {
                if (call.Retry != expected)
                    failures.Add($"{label}: expected {expected} but got {call.Retry}");
                var expectedAttempts = expected == FileSystemRetry.Idempotent ? 2 : 1;
                if (call.Attempts != expectedAttempts)
                    failures.Add($"{label}: expected {expectedAttempts} attempt(s), saw {call.Attempts}");
            }
        }

        Require("listing", FileSystemRetry.Idempotent);
        Require("delete", FileSystemRetry.Once);
        Require("rename", FileSystemRetry.Once);
        Require("mkdir", FileSystemRetry.Once);

        var passed = failures.Count == 0;
        var report =
            $"{(passed ? "PASS" : "FAIL")}: SFTP reconnect replays only idempotent operations\n"
            + string.Join(
                "\n",
                calls.Select(call => $"{call.Label}: retry={call.Retry} attempts={call.Attempts}"))
            + $"\nfailures={failures.Count}"
            + (passed ? "" : "\n" + string.Join("\n", failures));
        return ToolText(report, isError: !passed);
    }

    /// <summary>
    /// Password auth on most sshd setups arrives as keyboard-interactive, and the prompt
    /// text is whatever PAM prints in the server's locale. Matching only the English word
    /// left non-English servers failing to authenticate with a correct stored password.
    /// </summary>
    private static JsonObject SshAuthPromptCheck()
    {
        const string secret = "s3cret";
        var failures = new List<string>();

        static AuthenticationPromptEventArgs Challenge(params (string Request, bool Echoed)[] prompts) =>
            new(
                username: "probe",
                instruction: "",
                language: "en-US",
                prompts
                    .Select((prompt, index) =>
                        new AuthenticationPrompt(index, prompt.Echoed, prompt.Request))
                    .ToList());

        void Verify(
            string name,
            SshConnectionFactory.KeyboardInteractiveConversation conversation,
            AuthenticationPromptEventArgs challenge,
            params string?[] expected)
        {
            SshConnectionFactory.AnswerPasswordPrompts(challenge, secret, conversation);
            var actual = challenge.Prompts.Select(prompt => prompt.Response).ToArray();
            if (actual.Length != expected.Length
                || actual.Where((response, i) => response != expected[i]).Any())
            {
                failures.Add(
                    $"{name}: expected [{string.Join(", ", expected.Select(v => v ?? "<null>"))}] "
                    + $"but got [{string.Join(", ", actual.Select(v => v ?? "<null>"))}]");
            }
        }

        // Each single-round case gets a fresh conversation, like a fresh connect would.
        void Expect(string name, AuthenticationPromptEventArgs challenge, params string?[] expected) =>
            Verify(name, new SshConnectionFactory.KeyboardInteractiveConversation(), challenge, expected);

        Expect("english", Challenge(("Password: ", false)), secret);
        Expect("chinese", Challenge(("密码：", false)), secret);
        Expect("german", Challenge(("Passwort: ", false)), secret);
        Expect("russian", Challenge(("Пароль: ", false)), secret);
        // No keyword at all, but a single hidden prompt is a password challenge.
        Expect("unlabelled single hidden prompt", Challenge(("(current) UNIX: ", false)), secret);
        // Echoed prompts ask for a user name or an OTP; answering leaks the password.
        Expect("echoed prompt is never answered", Challenge(("Username: ", true)), (string?)null);
        Expect(
            "two-factor keeps the token prompt empty",
            Challenge(("Password: ", false), ("Verification code: ", false)),
            secret,
            null);
        Expect(
            "echoed banner alongside a password prompt",
            Challenge(("Last login banner", true), ("密码：", false)),
            null,
            secret);

        // The real MFA shape: the second factor arrives as its own round, one hidden
        // prompt and nothing else — indistinguishable by shape from an unlabelled
        // password challenge. Answering it burns an attempt against the lockout counter.
        {
            var mfa = new SshConnectionFactory.KeyboardInteractiveConversation();
            Verify("mfa round 1 password", mfa, Challenge(("Password: ", false)), secret);
            Verify("mfa round 2 verification code", mfa, Challenge(("Verification code: ", false)),(string?)null);
        }

        // Same, with the first round unlabelled so only the fallback answered it.
        {
            var mfa = new SshConnectionFactory.KeyboardInteractiveConversation();
            Verify("unlabelled round 1", mfa, Challenge(("(current) UNIX: ", false)), secret);
            Verify("unlabelled round 2 is not answered", mfa, Challenge(("Enter response: ", false)),(string?)null);
        }

        // A lone OTP prompt with no preceding password round must still be refused
        // by the password filler — the connect-time handler asks the user instead.
        Expect("lone verification code prompt", Challenge(("Verification code: ", false)),(string?)null);
        Expect("lone duo passcode prompt", Challenge(("Passcode or option (1-3): ", false)),(string?)null);
        Expect("lone one-time password prompt", Challenge(("One-time password: ", false)),(string?)null);
        Expect("lone authenticator prompt", Challenge(("Authenticator code: ", false)),(string?)null);
        Expect("lone chinese otp prompt", Challenge(("验证码：", false)),(string?)null);
        Expect(
            "lone chinese second-factor password prompt",
            Challenge(("请输入二次验证密码：", false)),
            (string?)null);
        Expect("lone token prompt", Challenge(("Token: ", false)),(string?)null);
        Expect("lone bracketed otp prompt", Challenge(("[OTP Code]: ", false)),(string?)null);
        // A password prompt that merely mentions two-factor setup is still refused
        // rather than risk feeding the password into a second-factor field.
        Expect(
            "two-factor wording wins over password wording",
            Challenge(("Two-factor code (not your password): ", false)),
            (string?)null);

        const string otp = "123456";
        var context = new SshConnectionFactory.KeyboardInteractiveChallenge(
            "zhgate.example", 2222, "probe", "", false, "");

        void Handle(
            string name,
            SshConnectionFactory.KeyboardInteractiveConversation conversation,
            AuthenticationPromptEventArgs challenge,
            Func<SshConnectionFactory.KeyboardInteractiveChallenge, string?>? askUser,
            params string?[] expected)
        {
            SshConnectionFactory.HandleAuthenticationPrompt(
                challenge, secret, conversation, context, askUser);
            var actual = challenge.Prompts.Select(prompt => prompt.Response).ToArray();
            if (actual.Length != expected.Length
                || actual.Where((response, i) => response != expected[i]).Any())
            {
                failures.Add(
                    $"{name}: expected [{string.Join(", ", expected.Select(v => v ?? "<null>"))}] "
                    + $"but got [{string.Join(", ", actual.Select(v => v ?? "<null>"))}]");
            }
        }

        // Same-round password + OTP: password is filled, OTP comes from the user.
        {
            var asked = new List<string>();
            Handle(
                "same-round otp asked of the user",
                new SshConnectionFactory.KeyboardInteractiveConversation(),
                Challenge(("Password: ", false), ("[OTP Code]: ", false)),
                challenge =>
                {
                    asked.Add(challenge.Request);
                    return otp;
                },
                secret,
                otp);
            if (asked.Count != 1 || asked[0] != "[OTP Code]: ")
                failures.Add($"same-round otp asked: [{string.Join(", ", asked)}]");
        }

        // The real MFA shape: password round, then a lone OTP round.
        {
            var mfa = new SshConnectionFactory.KeyboardInteractiveConversation();
            var asked = new List<string>();
            Handle(
                "mfa handle round 1 password",
                mfa,
                Challenge(("Password: ", false)),
                challenge =>
                {
                    asked.Add(challenge.Request);
                    return "should-not-be-asked";
                },
                secret);
            Handle(
                "mfa handle round 2 otp",
                mfa,
                Challenge(("[OTP Code]: ", false)),
                challenge =>
                {
                    asked.Add(challenge.Request);
                    return otp;
                },
                otp);
            if (asked.Count != 1 || asked[0] != "[OTP Code]: ")
                failures.Add($"two-round otp asked: [{string.Join(", ", asked)}]");
        }

        // Cancelling the OTP dialog must not leave Response null for SSH.NET to trip on.
        try
        {
            SshConnectionFactory.HandleAuthenticationPrompt(
                Challenge(("[OTP Code]: ", false)),
                secret,
                new SshConnectionFactory.KeyboardInteractiveConversation(),
                context,
                _ => null);
            failures.Add("cancelled otp prompt did not throw");
        }
        catch (InvalidOperationException ex)
            when (ex.Message.Contains("cancelled", StringComparison.OrdinalIgnoreCase)
                  && ex.Message.Contains("[OTP Code]", StringComparison.Ordinal))
        {
        }
        catch (Exception ex)
        {
            failures.Add($"cancelled otp prompt: {ex.GetType().Name}: {ex.Message}");
        }

        // No GUI hook: name the leftover prompt instead of SSH.NET's null-Response error.
        try
        {
            SshConnectionFactory.HandleAuthenticationPrompt(
                Challenge(("[OTP Code]: ", false)),
                secret,
                new SshConnectionFactory.KeyboardInteractiveConversation(),
                context,
                askUser: null);
            failures.Add("missing otp callback did not throw");
        }
        catch (InvalidOperationException ex)
            when (ex.Message.Contains("[OTP Code]", StringComparison.Ordinal)
                  && !ex.Message.Contains("Response is null", StringComparison.Ordinal))
        {
        }
        catch (Exception ex)
        {
            failures.Add($"missing otp callback: {ex.GetType().Name}: {ex.Message}");
        }

        // A configured key path that does not exist must be named, not swallowed into a
        // generic "no usable credential" that sends the user hunting — and it must be
        // named even when the connection also has a password, which would otherwise carry
        // the login and leave the broken path unmentioned until it is the last credential.
        var missingKeyPath = Path.Combine(Path.GetTempPath(), "JeekRemoteManager.NoSuchKey.pem");
        Connection KeyProbe(string? encryptedPassword = null) => new()
        {
            Type = ConnectionType.Ssh,
            Host = "example.invalid",
            Port = 22,
            Username = "probe",
            PrivateKeyPath = missingKeyPath,
            EncryptedPassword = encryptedPassword ?? "",
        };

        // Drive the real Build path too, so the warning it logs is exercised rather than
        // only the helper the assertions read. It may or may not throw here depending on
        // whether this machine has agent or ~/.ssh keys; either outcome is fine.
        try
        {
            SshConnectionFactory.Build(KeyProbe());
        }
        catch (InvalidOperationException)
        {
        }

        var missingKeyMessage =
            SshConnectionFactory.DescribeUnusableExplicitKey(KeyProbe()) ?? "(no problem reported)";
        var namesMissingKey =
            missingKeyMessage.Contains(missingKeyPath, StringComparison.OrdinalIgnoreCase);
        if (!namesMissingKey)
            failures.Add($"missing key file: {missingKeyMessage}");

        // Reported independently of whether any other method would succeed.
        if (SshConnectionFactory.DescribeUnusableExplicitKey(KeyProbe("not-a-real-blob")) is null)
            failures.Add("missing key file is not reported when the connection has a password");
        if (SshConnectionFactory.DescribeUnusableExplicitKey(
                new Connection { Type = ConnectionType.Ssh, Host = "h", Username = "u" }) is not null)
            failures.Add("a connection without a key path reported a key problem");

        var promptUserWired = OnUiAsync(() =>
            (Desktop?.MainWindow?.DataContext as ViewModels.MainWindowViewModel)?.PromptUser is not null).GetAwaiter().GetResult();
        if (!promptUserWired)
            failures.Add("MainVm.PromptUser is not wired; OTP prompts on its dials would fail at connect time");

        foreach (var key in new[] { "SshAuthTitle", "SshAuthPrompt", "SshAuthResponse", "SshAuthHint", "SshAuthShow" })
        {
            var text = Localizer.Get(key);
            if (string.IsNullOrWhiteSpace(text) || text == key)
                failures.Add($"missing localization key {key}");
        }

        var passed = failures.Count == 0;
        var report =
            $"{(passed ? "PASS" : "FAIL")}: SSH keyboard-interactive and key-path diagnostics\n"
            + $"namesMissingKey={namesMissingKey}\n"
            + $"missingKeyMessage={missingKeyMessage}\n"
            + $"promptUserWired={promptUserWired}\n"
            + $"failures={failures.Count}"
            + (passed ? "" : "\n" + string.Join("\n", failures));
        return ToolText(report, isError: !passed);
    }

    private static JsonObject HostKeyTrustCheck()
    {
        var host = "debug-host-key-" + Guid.NewGuid().ToString("N") + ".invalid";
        const int port = 2222;
        const string first = "first-fingerprint";
        const string replacement = "replacement-fingerprint";
        var failures = new List<string>();
        var corruptDir = Path.Combine(Path.GetTempPath(), "jrm-known-hosts-" + Guid.NewGuid().ToString("N"));
        try
        {
            if (KnownHostsStore.Default.Check(host, port, "ssh-ed25519", first) != KnownHostsStore.Status.Unknown)
                failures.Add("new host was not unknown");

            var unexpectedPrompt = false;
            var firstAccepted = SshHostKey.Evaluate(
                KnownHostsStore.Default,
                host,
                port,
                "ssh-ed25519",
                first,
                onMismatch: (_, _, _) =>
                {
                    unexpectedPrompt = true;
                    return false;
                });
            if (!firstAccepted || unexpectedPrompt)
                failures.Add("first-seen key was not accepted silently");
            if (KnownHostsStore.Default.Check(host, port, "ssh-ed25519", first) != KnownHostsStore.Status.Match)
                failures.Add("first-seen key was not saved");
            if (!KnownHostsStore.Default.TryGetKeyType(host, port, out var firstFamily) || firstFamily != "ssh-ed25519")
                failures.Add($"first-seen key family was not recorded (got '{firstFamily}')");
            if (KnownHostsStore.Default.Check(host, port, "ssh-ed25519", replacement) != KnownHostsStore.Status.Mismatch)
                failures.Add("changed key was not detected as a mismatch");
            if (KnownHostsStore.Default.Check(host, port, "ecdsa-sha2-nistp256", replacement) != KnownHostsStore.Status.Mismatch)
                failures.Add("a key of another family was not treated as a mismatch");

            var prompted = false;
            var accepted = SshHostKey.Evaluate(
                KnownHostsStore.Default,
                host,
                port,
                "rsa-sha2-512",
                replacement,
                onMismatch: (_, saved, presented) =>
                {
                    prompted = true;
                    return saved == first && presented == replacement;
                });
            if (!prompted || !accepted)
                failures.Add("replacement decision was not accepted");
            if (KnownHostsStore.Default.Check(host, port, "rsa-sha2-256", replacement) != KnownHostsStore.Status.Match)
                failures.Add("replacement key was not stored");
            if (!KnownHostsStore.Default.TryGetKeyType(host, port, out var rsaFamily) || rsaFamily != "ssh-rsa")
                failures.Add($"rsa-sha2-512 was not recorded as the ssh-rsa family (got '{rsaFamily}')");

            // Remembered family goes first in the offer, whatever SSH.NET's default order is.
            var info = new Renci.SshNet.ConnectionInfo(host, port, "probe",
                new Renci.SshNet.PasswordAuthenticationMethod("probe", "probe"));
            SshHostKey.PreferRememberedKeyType(KnownHostsStore.Default, info, host, port);
            var offered = info.HostKeyAlgorithms.Keys.ToList();
            var firstOffered = offered.FirstOrDefault() ?? "";
            if (KnownHostsStore.KeyFamily(firstOffered) != "ssh-rsa")
                failures.Add($"remembered family was not offered first (got {string.Join(",", offered.Take(4))})");
            if (offered.Count != new Renci.SshNet.ConnectionInfo(host, port, "probe",
                    new Renci.SshNet.PasswordAuthenticationMethod("probe", "probe")).HostKeyAlgorithms.Count)
                failures.Add("reordering changed the set of offered algorithms");

            // An entry from before families were recorded learns its family on the next match.
            KnownHostsStore.Default.Trust(host, port, first);
            var legacyHasFamily = KnownHostsStore.Default.TryGetKeyType(host, port, out _);
            if (legacyHasFamily)
                failures.Add("a family-less trust still reported a family");
            KnownHostsStore.Default.Check(host, port, "ecdsa-sha2-nistp256", first);
            if (!KnownHostsStore.Default.TryGetKeyType(host, port, out var learned) || learned != "ecdsa-sha2-nistp256")
                failures.Add($"legacy entry did not learn its family on match (got '{learned}')");

            if (KnownHostsStore.Default.All().Any(entry => entry.Host.Contains('#')))
                failures.Add("All() exposed an internal family entry as a host");

            // A corrupt file is kept aside instead of being silently overwritten.
            Directory.CreateDirectory(corruptDir);
            var corruptPath = Path.Combine(corruptDir, "known_hosts.json");
            File.WriteAllText(corruptPath, "{ \"kept.example:22\": \"abc\", broken");
            // Its own store on its own file: the running app keeps using the real one.
            var corruptStore = new KnownHostsStore(corruptPath);
            var corruptStatus = corruptStore.Check("kept.example", 22, "ssh-ed25519", "abc");
            corruptStore.Trust("new.example", 22, "def", "ssh-ed25519");
            var backups = Directory.GetFiles(corruptDir, "known_hosts.json.corrupt-*");
            if (corruptStatus != KnownHostsStore.Status.Unknown)
                failures.Add($"corrupt file was not treated as empty (got {corruptStatus})");
            if (backups.Length != 1 || !File.ReadAllText(backups[0]).Contains("kept.example", StringComparison.Ordinal))
                failures.Add($"corrupt file was not backed up (backups={backups.Length})");
            if (corruptStore.Check("new.example", 22, "ssh-ed25519", "def") != KnownHostsStore.Status.Match)
                failures.Add("store did not recover after backing up a corrupt file");

            var passed = failures.Count == 0;
            var report = $"{(passed ? "PASS" : "FAIL")}: host-key trust flow\n"
                + $"unknownAutoAccepted={firstAccepted && !unexpectedPrompt}\nmatch=true\nmismatch=true\n"
                + $"replacement={prompted && accepted}\nfirstOffered={firstOffered}\n"
                + $"corruptBackups={backups.Length}\nfailures={failures.Count}"
                + (passed ? "" : "\n" + string.Join("\n", failures));
            return ToolText(report, isError: !passed);
        }
        finally
        {
            KnownHostsStore.Default.Forget(host, port);
            try { Directory.Delete(corruptDir, recursive: true); } catch { /* ignore */ }
        }
    }

    /// <summary>
    /// The file browser dials its own SFTP transport. It used to skip the known-hosts
    /// check entirely, so a spoofed host received the credentials. Drives a real
    /// <see cref="SftpSession"/> against a reachable test server (the local WSL sshd rig by
    /// default): a planted wrong fingerprint must be rejected, a forgotten host must be
    /// trusted on first use. The host's original entry is restored afterwards.
    /// </summary>
    private static async Task<JsonObject> SftpHostKeyCheckAsync(JsonObject args)
    {
        var host = ArgString(args, "host") ?? "127.0.0.1";
        var port = ArgInt(args, "port") ?? 2222;
        var username = ArgString(args, "username") ?? "jrmtest";
        var connection = new Connection { Type = ConnectionType.Ssh, Host = host, Port = port, Username = username };
        var failures = new List<string>();
        var hadOriginal = KnownHostsStore.Default.TryGet(host, port, out var original);
        string rejectedMessage = "(none)";
        string trusted = "(none)";
        string rememberedFamily = "(none)";
        try
        {
            KnownHostsStore.Default.Trust(host, port, "planted-wrong-fingerprint", "ssh-ed25519");
            using (var session = new SftpSession(connection))
            {
                try
                {
                    await session.RunAsync(ops => ops.WorkingDirectory, FileSystemRetry.Idempotent);
                    failures.Add("SFTP connected despite a mismatched host key");
                }
                catch (Exception ex)
                {
                    rejectedMessage = ex.Message;
                    if (!ex.Message.Contains("host key changed", StringComparison.Ordinal))
                        failures.Add($"mismatch failed for another reason: {ex.Message}");
                }
            }

            KnownHostsStore.Default.Forget(host, port);
            using (var session = new SftpSession(connection))
            {
                try
                {
                    await session.RunAsync(ops => ops.WorkingDirectory, FileSystemRetry.Idempotent);
                }
                catch (Exception ex)
                {
                    failures.Add($"first-use SFTP dial failed: {ex.Message}");
                }
            }

            if (KnownHostsStore.Default.TryGet(host, port, out var saved))
                trusted = saved;
            else
                failures.Add("first-use SFTP dial did not record the host key");

            // Simulate a client whose default ranking now puts another family first (an
            // SSH.NET upgrade does exactly this). The remembered family must still win the
            // negotiation, so the host is recognised instead of raising a false alarm.
            KnownHostsStore.Default.TryGetKeyType(host, port, out rememberedFamily);
            using (var session = new SftpSession(connection, configure: info =>
                   {
                       foreach (var pair in info.HostKeyAlgorithms
                                    .Where(pair => KnownHostsStore.KeyFamily(pair.Key) != rememberedFamily)
                                    .Reverse()
                                    .ToList())
                       {
                           info.HostKeyAlgorithms.Remove(pair.Key);
                           info.HostKeyAlgorithms.Insert(0, pair.Key, pair.Value);
                       }
                   }))
            {
                try
                {
                    await session.RunAsync(ops => ops.WorkingDirectory, FileSystemRetry.Idempotent);
                }
                catch (Exception ex)
                {
                    failures.Add($"dial with a re-ranked algorithm offer failed: {ex.Message}");
                }
            }
        }
        finally
        {
            if (hadOriginal)
                KnownHostsStore.Default.Trust(host, port, original);
            else
                KnownHostsStore.Default.Forget(host, port);
        }

        var passed = failures.Count == 0;
        var report = $"{(passed ? "PASS" : "FAIL")}: SFTP dial verifies the host key\n"
            + $"target={username}@{host}:{port}\nmismatchRejected={rejectedMessage}\ntrustedOnFirstUse={trusted}\n"
            + $"rememberedFamily={rememberedFamily}\n"
            + $"failures={failures.Count}"
            + (passed ? "" : "\n" + string.Join("\n", failures));
        return ToolText(report, isError: !passed);
    }

    /// <summary>
    /// Jump hosts and port forwarding, against a real sshd (the local WSL rig by default):
    /// dials the target through a jump connection and runs a command over it; starts L, D
    /// and R forwards and pushes bytes through each (an SSH banner over L, a SOCKS5 CONNECT
    /// over D, a remote /dev/tcp write back to a local listener over R); checks the forwards
    /// stop with the transport that owns them; and checks parse errors and an unknown jump
    /// host are reported. The rig's known-hosts entry is restored afterwards.
    /// </summary>
    private static async Task<JsonObject> SshJumpForwardCheckAsync(JsonObject args)
    {
        var host = ArgString(args, "host") ?? "127.0.0.1";
        var port = ArgInt(args, "port") ?? 2222;
        var username = ArgString(args, "username") ?? "jrmtest";
        var failures = new List<string>();
        var report = new List<string>();
        var hadOriginal = KnownHostsStore.Default.TryGet(host, port, out var originalFingerprint);
        KnownHostsStore.Default.TryGetKeyType(host, port, out var originalKeyType);

        // Parsing.
        try
        {
            var specs = SshPortForwarding.Parse("L 8080 db:5432\nL 0.0.0.0:8081:db:5432 # comment\nR 9000 localhost:3000\nD 1080\n");
            report.Add("parsed=" + string.Join(" | ", specs));
            if (specs.Count != 4 || specs[0].BindHost != "127.0.0.1" || specs[1].BindHost != "0.0.0.0"
                || specs[3].Kind != 'D' || specs[1].TargetPort != 5432)
            {
                failures.Add("valid forwarding lines parsed wrongly");
            }
        }
        catch (Exception ex)
        {
            failures.Add($"valid forwarding lines were rejected: {ex.Message}");
        }

        foreach (var bad in new[] { "L 8080", "X 1 a:2", "L 70000 a:1", "D 1080 extra" })
        {
            if (SshPortForwarding.Validate(bad) is null)
                failures.Add($"'{bad}' was accepted");
        }

        var jump = new Connection
        {
            ConnectionId = "jump-probe", Name = "jump", Type = ConnectionType.Ssh,
            Host = host, Port = port, Username = username,
        };
        var target = new Connection
        {
            ConnectionId = "target-probe", Name = "target", Type = ConnectionType.Ssh,
            Host = "127.0.0.1", Port = port, Username = username, JumpHost = "_probe/jump",
        };
        Connection? Resolve(string path) => path == "_probe/jump" ? jump : null;

        try
        {
            await Task.Run(() =>
            {
                // Through the jump host.
                var (viaJump, tunnel) = SshDialer.Connect(target, info => new SshClient(info), new SshDialOptions(), Resolve);
                using (tunnel)
                using (viaJump)
                {
                    var output = viaJump.RunCommand("echo via-jump").Result.Trim();
                    report.Add($"jump: localPort={tunnel?.LocalPort} output={output}");
                    if (tunnel is null || tunnel.LocalPort == port || tunnel.LocalPort <= 0)
                        failures.Add("the jump dial did not go through a local tunnel");
                    if (output != "via-jump")
                        failures.Add($"command over the jump failed: '{output}'");
                }

                try
                {
                    SshDialer.Connect(target, info => new SshClient(info), new SshDialOptions(), _ => null);
                    failures.Add("an unknown jump host was not reported");
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("not a saved connection", StringComparison.Ordinal))
                {
                    report.Add("unknownJump=reported");
                }

                // Forwards on a direct transport.
                var (client, _) = SshDialer.Connect(jump, info => new SshClient(info), new SshDialOptions());
                var shared = new SharedSshClient(client);
                var localPort = FreeTcpPort();
                var socksPort = FreeTcpPort();
                var remotePort = Random.Shared.Next(20000, 40000);
                using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
                listener.Start();
                var listenerPort = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
                var started = SshPortForwarding.StartAll(client, SshPortForwarding.Parse(
                    $"L {localPort} 127.0.0.1:{port}\nD {socksPort}\nR {remotePort} 127.0.0.1:{listenerPort}"));
                foreach (var result in started.Where(result => result.Error is not null))
                    failures.Add($"forward {result.Spec} did not start: {result.Error}");
                shared.AddOwnedResource(new PortForwardSet(started.Where(r => r.Port is not null).Select(r => r.Port!).ToList()));

                var banner = ReadBanner(localPort, socks: false, port);
                report.Add($"L banner={banner}");
                if (!banner.StartsWith("SSH-", StringComparison.Ordinal))
                    failures.Add("nothing came back through the local forward");

                var socksBanner = ReadBanner(socksPort, socks: true, port);
                report.Add($"D banner={socksBanner}");
                if (!socksBanner.StartsWith("SSH-", StringComparison.Ordinal))
                    failures.Add("nothing came back through the SOCKS forward");

                var accept = listener.AcceptTcpClientAsync();
                client.RunCommand($"bash -c 'echo hi-from-remote > /dev/tcp/127.0.0.1/{remotePort}'");
                if (accept.Wait(TimeSpan.FromSeconds(5)))
                {
                    using var inbound = accept.Result;
                    using var reader = new StreamReader(inbound.GetStream());
                    var line = reader.ReadLine();
                    report.Add($"R received={line}");
                    if (line != "hi-from-remote")
                        failures.Add($"the remote forward delivered '{line}'");
                }
                else
                {
                    failures.Add("the remote forward never connected back");
                }

                // Releasing the transport stops the forwards it owns.
                shared.Release();
                var stillListening = IsListening(localPort);
                report.Add($"afterRelease localListening={stillListening}");
                if (stillListening)
                    failures.Add("the local forward kept listening after its transport was released");
            });
        }
        catch (Exception ex)
        {
            failures.Add($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (hadOriginal)
                KnownHostsStore.Default.Trust(host, port, originalFingerprint, originalKeyType);
            else
                KnownHostsStore.Default.Forget(host, port);
        }

        var passed = failures.Count == 0;
        return ToolText(
            $"{(passed ? "PASS" : "FAIL")}: jump hosts and port forwarding\n"
            + string.Join("\n", report)
            + $"\nfailures={failures.Count}"
            + (passed ? "" : "\n" + string.Join("\n", failures)),
            isError: !passed);
    }

    private static int FreeTcpPort()
    {
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var free = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return free;
    }

    private static bool IsListening(int port)
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            return client.ConnectAsync(System.Net.IPAddress.Loopback, port).Wait(TimeSpan.FromSeconds(2))
                   && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Connects to a forward and returns the SSH banner that comes back through it.</summary>
    private static string ReadBanner(int forwardPort, bool socks, int sshPort)
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            client.Connect(System.Net.IPAddress.Loopback, forwardPort);
            var stream = client.GetStream();
            stream.ReadTimeout = 5000;
            if (socks)
            {
                stream.Write([5, 1, 0]);
                var greeting = new byte[2];
                stream.ReadExactly(greeting);
                stream.Write([5, 1, 0, 1, 127, 0, 0, 1, (byte)(sshPort >> 8), (byte)sshPort]);
                var reply = new byte[10];
                stream.ReadExactly(reply);
                if (reply[1] != 0)
                    return $"(socks error {reply[1]})";
            }

            var buffer = new byte[64];
            var read = stream.Read(buffer);
            return Encoding.ASCII.GetString(buffer, 0, read).Trim();
        }
        catch (Exception ex)
        {
            return $"({ex.GetType().Name}: {ex.Message})";
        }
    }

    private static string? ArgString(JsonObject args, string name) =>
        args[name] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0 ? text : null;

    /// <summary>MCP clients sometimes stringify scalars, so accept "2222" as well as 2222.</summary>
    private static int? ArgInt(JsonObject args, string name)
    {
        if (args[name] is not JsonValue value)
            return null;
        if (value.TryGetValue<int>(out var number))
            return number;
        return value.TryGetValue<string>(out var text) && int.TryParse(text, out number) ? number : null;
    }
}
