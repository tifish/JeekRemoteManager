using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Jeek.Avalonia.Localization;
using JeekRemoteManager.Models;
using JeekRemoteManager.Views;
using Renci.SshNet;

namespace JeekRemoteManager.Services;

internal static partial class DebugMcpServer
{
    private static async Task<JsonObject> SshMfaInputCheckAsync(JsonObject args)
    {
        if (!DebugInstanceContext.IsDebugBuild)
            return ToolText("Run this probe in a Debug instance.", true);
        var port = args["loopback_port"]?.GetValue<int>();
        if (port is < 1 or > 65535)
            return ToolText("Invalid loopback fixture port.", true);

        var encrypted = PasswordProtector.Encrypt("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ");
        var checks = new Dictionary<string, bool>();

        Task ProbeAsync(SshMfaInput input, string commands, Func<TerminalView, MfaProbeChannel, Task> check,
            bool alreadySubmitted = false, bool failWrite = false, string totpReply = "\r\n1: fixture-host\r\n")
        {
            var channel = new MfaProbeChannel(failWrite, totpReply);
            var attempt = new SshMfaAttempt(new(Guid.NewGuid(), "Fixture", encrypted, input))
            {
                CodeSubmitted = alreadySubmitted,
            };
            return ProbeChannelAsync(channel, commands, attempt, view => check(view, channel));
        }

        async Task ProbeChannelAsync(ITerminalChannel channel, string commands, SshMfaAttempt attempt,
            Func<TerminalView, Task> check)
        {
            var state = await OnUiAsync(() =>
            {
                var view = new TerminalView();
                var window = new Window { Width = 720, Height = 400, ShowInTaskbar = false, Content = view };
                window.Show();
                view.DebugStartMfaLogin(channel, commands, attempt);
                return (Window: window, View: view);
            });
            try
            {
                await check(state.View);
            }
            finally
            {
                await OnUiAsync(() =>
                {
                    state.View.Close();
                    state.Window.Close();
                    return true;
                });
            }
        }

        static async Task<bool> WaitForAsync(Func<bool> ready)
        {
            var deadline = Environment.TickCount64 + 3_000;
            while (!ready() && Environment.TickCount64 < deadline)
                await Task.Delay(25);
            return ready();
        }

        var match = new SshMfaInput(SshMfaInputMode.MatchPrompt, "2nd Password:");
        var immediate = new SshMfaInput(SshMfaInputMode.OnShellOpen);
        await ProbeAsync(match, "#input\n#select fixture-host\n#input\nfinish", async (view, channel) =>
        {
            await WaitForAsync(() => view.IsLoginManualInputPending);
            channel.Feed("2nd Password:\r\nUnrelated banner\r\n");
            channel.Feed("\u001b[31m2nd Pa");
            await Task.Delay(150);
            checks["ignoresOlderLinesAndPartialPrompt"] = channel.Writes.IsEmpty;
            channel.Feed("ssword:\u001b[0m");
            await Task.Delay(120);
            checks["waitsForMatchingPromptToSettle"] = channel.Writes.IsEmpty;
            checks["preservesImmediateMenuOutputAfterTotp"] =
                await WaitForAsync(() => channel.Writes.Contains("1\r"));
            checks["onlyFirstInputIsSatisfiedByTotp"] = await WaitForAsync(() => view.IsLoginManualInputPending)
                && !channel.Writes.Contains("finish\r");
            checks["laterManualInputResumesCommands"] = await OnUiAsync(() => view.DebugSubmitLoginInput("manual"))
                && await WaitForAsync(() => view.LoginSequenceState == "ready")
                && channel.Writes.Contains("finish\r");
            checks["matchesSplitColoredPromptAndSendsOnce"] =
                channel.Writes.Count(MfaProbeChannel.IsTotp) == 1;
        });

        await ProbeAsync(immediate, "#input", async (view, channel) =>
        {
            checks["immediateModeSendsWithoutOutput"] = await WaitForAsync(() => view.LoginSequenceState == "ready")
                && channel.Writes.TryPeek(out var input) && MfaProbeChannel.IsTotp(input);
        });

        await ProbeAsync(immediate, "#input\nfinish", async (view, channel) =>
        {
            channel.Feed("Authenticated\r\n");
            checks["handshakeTotpSkipsFirstInputAndPreventsShellResend"] =
                await WaitForAsync(() => view.LoginSequenceState == "ready")
                && channel.Writes.SequenceEqual(["finish\r"]);
        }, alreadySubmitted: true);

        await ProbeAsync(immediate, "#input", async (view, channel) =>
        {
            checks["failedWriteLeavesFirstInputPending"] = await WaitForAsync(() => view.IsLoginManualInputPending)
                && channel.Writes.IsEmpty;
            checks["manualInputWorksAfterFailedWrite"] = await OnUiAsync(() => view.DebugSubmitLoginInput("manual"))
                && await WaitForAsync(() => view.LoginSequenceState == "ready");
        }, failWrite: true);

        await ProbeAsync(match, "", async (view, channel) =>
        {
            channel.Feed("2nd Password:");
            checks["matchesPromptWithNoLoginCommands"] = await WaitForAsync(() => view.LoginSequenceState == "ready")
                && channel.Writes.TryPeek(out var input) && MfaProbeChannel.IsTotp(input);
        });

        await ProbeAsync(match, "#input\nfinish", async (view, channel) =>
        {
            channel.Feed("2nd Password:");
            var sent = await WaitForAsync(() => channel.Writes.Any(MfaProbeChannel.IsTotp));
            await Task.Delay(700); // The masked echo arrives well before the server's answer.
            checks["maskedEchoDoesNotCompleteMfa"] = sent && view.LoginSequenceState == "running"
                && !channel.Writes.Contains("finish\r");
            channel.Feed("\r\n2nd Password:");
            checks["repeatedPromptWaitsForManualInput"] = await WaitForAsync(() => view.IsLoginManualInputPending)
                && !channel.Writes.Contains("finish\r") && channel.Writes.Count(MfaProbeChannel.IsTotp) == 1;
            checks["manualRetryResumesLoginAfterServerReply"] =
                await OnUiAsync(() => view.DebugSubmitLoginInput("manual"))
                && await WaitForAsync(() => view.LoginSequenceState == "ready")
                && channel.Writes.Contains("finish\r");
        }, totpReply: "******\r\n");

        foreach (var inputMode in new[] { match, immediate })
        {
            var profile = new SshMfaProfileStore.Profile(Guid.NewGuid(), "Fixture", encrypted, inputMode);
            var clock = new MfaCodeProbeClock { Now = DateTimeOffset.FromUnixTimeSeconds(59) };
            var firstChannel = new MfaProbeChannel(false, "\r\n1: fixture-host\r\n");
            await ProbeChannelAsync(firstChannel, "#input", new(profile, clock), async view =>
            {
                if (inputMode == match)
                    firstChannel.Feed("2nd Password:");
                checks[$"{inputMode.Mode}FirstTabSendsCurrentCode"] =
                    await WaitForAsync(() => view.LoginSequenceState == "ready")
                    && firstChannel.Writes.SequenceEqual(["287082\r"]);
            });

            var reopenedChannel = new MfaProbeChannel(false, "\r\n1: fixture-host\r\n");
            await ProbeChannelAsync(reopenedChannel, "#input\nfinish", new(profile, clock), async view =>
            {
                if (inputMode == match)
                    reopenedChannel.Feed("2nd Password:");
                var waitingMessage = Localizer.Get("SshMfaWaitingForFreshCode");
                var deadline = Environment.TickCount64 + 3_000;
                var waiting = false;
                while (!waiting && Environment.TickCount64 < deadline)
                {
                    waiting = await OnUiAsync(() => view.IsLoginManualInputPending
                        && view.DebugVisibleTerminalText.Contains(waitingMessage, StringComparison.Ordinal));
                    await Task.Delay(25);
                }
                checks[$"{inputMode.Mode}ReopenedTabWaitsForNewCode"] = waiting && reopenedChannel.Writes.IsEmpty;
                clock.Now = DateTimeOffset.FromUnixTimeSeconds(60);
                checks[$"{inputMode.Mode}ReopenedTabSendsNextCodeAndContinues"] =
                    await WaitForAsync(() => view.LoginSequenceState == "ready")
                    && reopenedChannel.Writes.SequenceEqual(["359152\r", "finish\r"]);
            });
        }

        if (port.HasValue)
        {
            var root = Path.Combine(Path.GetTempPath(), "jrm-mfa-ssh-" + Guid.NewGuid().ToString("N"));
            try
            {
                var attempt = new SshMfaAttempt(new(Guid.NewGuid(), "Fixture", encrypted, match));
                var connection = new Connection
                {
                    Host = "127.0.0.1", Port = port.Value, Username = "mfa-input",
                    EncryptedPassword = PasswordProtector.Encrypt("jrm-test-password"),
                };
                var options = new SshDialOptions(KnownHosts: new KnownHostsStore(Path.Combine(root, "known_hosts.json")))
                {
                    TargetMfa = attempt,
                };
                var (client, _) = await Task.Run(() => SshDialer.Connect(connection, info => new SshClient(info),
                    options, configure: info => info.Timeout = TimeSpan.FromSeconds(5)));
                var shared = new SharedSshClient(client);
                try
                {
                    using var shell = await shared.CreateShellStreamAsync("xterm", 80, 24, 0, 0, 4096);
                    await ProbeChannelAsync(new SshTerminalChannel(shell), "#input", attempt, async view =>
                    {
                        var deadline = Environment.TickCount64 + 3_000;
                        var received = false;
                        while (!received && Environment.TickCount64 < deadline)
                        {
                            received = await OnUiAsync(() => view.LoginSequenceState == "ready"
                                && view.DebugVisibleTerminalText.Contains("MFA fixture passed", StringComparison.Ordinal));
                            if (!received)
                                await Task.Delay(25);
                        }
                        checks["receivesEarlyPromptOnActualSshChannel"] = received;
                    });
                }
                finally
                {
                    shared.Release();
                }
            }
            catch (Exception ex)
            {
                return ToolText($"FAIL: SSH MFA loopback fixture ({ex.GetType().Name}).", true);
            }
            finally
            {
                if (Directory.Exists(root))
                    try { Directory.Delete(root, recursive: true); } catch (IOException) { }
            }
        }

        var passed = checks.Values.All(value => value);
        return ToolText($"{(passed ? "PASS" : "FAIL")}: SSH MFA input\n"
            + string.Join("\n", checks.Select(check => $"{check.Key}={check.Value}")), !passed);
    }

    private static async Task<JsonObject> SshMfaPoolCheckAsync(JsonObject args)
    {
        if (!DebugInstanceContext.IsDebugBuild)
            return ToolText("Run this probe in a Debug instance.", true);
        var port = args["loopback_port"]?.GetValue<int>();
        if (port is not (>= 1 and <= 65535))
            return ToolText("Invalid loopback fixture port.", true);

        var root = Path.Combine(Path.GetTempPath(), "jrm-mfa-pool-" + Guid.NewGuid().ToString("N"));
        var profiles = new SshMfaProfileStore(root);
        var hosts = new KnownHostsStore(Path.Combine(root, "known_hosts.json"));
        var profile = profiles.Save(null, "Fixture",
            PasswordProtector.Encrypt("otpauth://totp/Fixture?secret=GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ&period=5"),
            new(SshMfaInputMode.MatchPrompt, "2nd Password:"));
        if (profile is null)
            return ToolText("Could not save the synthetic MFA fixture profile.", true);

        using var pool = new BastionSessionPool();
        var windows = new List<(Window Window, TerminalView View)>();
        Connection CreateConnection(string username) => new()
        {
            ConnectionId = Guid.NewGuid().ToString(),
            Name = "MFA fixture",
            Host = "127.0.0.1",
            Port = port.Value,
            Username = username,
            EncryptedPassword = PasswordProtector.Encrypt("jrm-test-password"),
            SshMfaProfileId = profile,
            SshMfaProfiles = profiles,
        };

        async Task<(string SessionId, string PoolState, bool FreshMfa, bool Reused)> OpenAsync()
        {
            var view = await OnUiAsync(() =>
            {
                var terminal = new TerminalView { BastionSessionPool = pool, DebugSshKnownHosts = hosts };
                var window = new Window { Width = 720, Height = 400, ShowInTaskbar = false, Content = terminal };
                windows.Add((window, terminal));
                window.Show();
                terminal.Start(CreateConnection("mfa-pool"), sourcePath: null);
                return terminal;
            });
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await view.WaitUntilConnectedAsync(timeout.Token);
            while (view.LoginSequenceState is not ("ready" or "failed"))
                await Task.Delay(25, timeout.Token);

            return await OnUiAsync(() =>
            {
                var client = view.ShareClientForDuplicate();
                try
                {
                    var output = view.DebugVisibleTerminalText;
                    return (client?.SessionId ?? "", view.BastionSessionState,
                        output.Contains("Fixture MFA accepted", StringComparison.Ordinal),
                        output.Contains("Fixture reused", StringComparison.Ordinal));
                }
                finally
                {
                    client?.Release();
                }
            });
        }

        try
        {
            var first = await OpenAsync();
            var second = await OpenAsync();
            var overflow = await OpenAsync();
            var fourth = await OpenAsync();
            var checks = new Dictionary<string, bool>
            {
                ["freshAuthenticatesAndRegisters"] = first.SessionId.Length > 0
                    && first.FreshMfa && first.PoolState == "fresh-pooled",
                ["secondShellReusesWithoutMfa"] = second.SessionId == first.SessionId
                    && second.Reused && !second.FreshMfa && second.PoolState == "pooled-ready",
                ["fullTransportOpensAnotherWithMfa"] = overflow.SessionId.Length > 0
                    && overflow.SessionId != first.SessionId && overflow.FreshMfa && overflow.PoolState == "fresh-pooled",
                ["nextShellUsesAvailableTransport"] = fourth.SessionId == overflow.SessionId
                    && fourth.Reused && !fourth.FreshMfa && fourth.PoolState == "pooled-ready" && pool.SessionCount == 2,
                ["anotherSshIdentityCannotBorrow"] = !pool.HasKnownSession(CreateConnection("mfa-pool/another-target")),
            };
            var passed = checks.Values.All(value => value);
            return ToolText($"{(passed ? "PASS" : "FAIL")}: SSH MFA TCP pooling\n"
                + string.Join("\n", checks.Select(check => $"{check.Key}={check.Value}")), !passed);
        }
        catch (Exception ex)
        {
            return ToolText($"FAIL: SSH MFA TCP pooling fixture ({ex.GetType().Name}).", true);
        }
        finally
        {
            await OnUiAsync(() =>
            {
                foreach (var (window, view) in windows)
                {
                    view.Close();
                    window.Close();
                }
                return true;
            });
            if (Directory.Exists(root))
                try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private sealed class MfaProbeChannel(bool failWrite, string totpReply) : ITerminalChannel
    {
        public event Action<byte[]>? DataReceived;
        public event Action<string>? ErrorMessage { add { } remove { } }
        public event Action? Closed { add { } remove { } }
        public bool SupportsBinaryTransfers => true;
        internal ConcurrentQueue<string> Writes { get; } = new();

        internal void Feed(string text) => DataReceived?.Invoke(Encoding.UTF8.GetBytes(text));
        internal static bool IsTotp(string input) =>
            input.Length == 7 && input[^1] == '\r' && input[..6].All(char.IsAsciiDigit);

        public void Write(byte[] data)
        {
            var text = Encoding.UTF8.GetString(data);
            if (IsTotp(text) && failWrite)
                throw new IOException("Fixture write failed.");
            Writes.Enqueue(text);
            Feed(IsTotp(text) ? totpReply : "\r\nOK\r\n");
        }

        public void Resize(uint cols, uint rows) { }
        public void Dispose() { }
    }
}
