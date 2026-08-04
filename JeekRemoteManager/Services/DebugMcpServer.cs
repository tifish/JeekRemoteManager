using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
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
using Renci.SshNet.Common;
using ZLogger;

namespace JeekRemoteManager.Services;

/// <summary>
/// App-specific configuration over the generic <see cref="McpHost"/> in
/// JeekTools: object-graph roots (App/Desktop/MainWindow/MainVm), '#Name'
/// visual-tree lookup, the Avalonia tools (visual_tree, screenshot), the app
/// probe tools, and the instance discovery file. Compiled into all
/// configurations so Debug and Release behave identically, but the listener
/// only starts in Debug builds. Agents reach it through the fixed per-user
/// <c>JeekRemoteManagerMcp.exe</c> (via the repo-root
/// <c>JeekRemoteManagerDebugMcp.cmd</c> with <c>--surface debug --app</c> this
/// worktree's exe), which forwards stdio to this instance's named pipe — the pipe
/// name carries the worktree's instance id, so parallel Debug builds never answer
/// for each other and there is no port to collide over. Agents must not launch
/// <c>bin\JeekRemoteManagerMcp.exe</c> directly; that path is build output
/// (installed to the fixed path on app startup) and would lock rebuilds if an
/// agent held it open.
/// </summary>
internal static class DebugMcpServer
{
    private static readonly ILogger Log = LogManager.CreateLogger(nameof(DebugMcpServer));

    // Runtime gate instead of #if DEBUG around the whole file: the code
    // compiles in every configuration, only Debug builds actually listen.
    private static readonly bool ListeningEnabled =
#if DEBUG
        true;
#else
        false;
#endif

    private static readonly JsonSerializerOptions PrettyOptions = new() { WriteIndented = true };

    private static readonly ObjectGraph Graph = new(new ObjectGraphOptions
    {
        ResolveRoot = ResolveRoot,
        RootNamesHelp = "App, Desktop, MainWindow, MainVm",
        FindNamedChild = (target, name) => target is Visual visual
            ? FindDescendantByName(visual, name)
            : throw new InvalidOperationException(
                $"'#{name}' requires a Visual; {target.GetType().Name} is not one."),
    });

    private static readonly McpHost Host = CreateHost();

    public static void Start()
    {
        Host.Start();
        OnEndpointChanged();
    }

    public static void Stop()
    {
        Host.Stop();
        OnEndpointChanged();
    }

    public static void RefreshDiscovery()
    {
        if (Host.PipeName.Length > 0)
            WriteDiscovery();
    }

    private static McpHost CreateHost()
    {
        var host = new McpHost(new McpHostOptions
        {
            ServerName = "jeek-remote-manager-debug",
            ServerTitle = "JeekRemoteManager Debug Server",
            Graph = Graph,
            GetVersion = () => $"{AutoUpdateService.GetLocalCommitCount()}",
            Enabled = ListeningEnabled,
            // Named pipe only: no port to collide over between worktree instances, and
            // nothing for the JRM_MCP_PORT workaround to disambiguate any more.
            PipeName = DebugInstanceContext.DebugMcpPipeName,
            DefaultPort = 0,
            UiInvoker = func => Dispatcher.UIThread.InvokeAsync(func).GetTask()
                .WaitAsync(TimeSpan.FromSeconds(15)),
            Describe = BuildDescribeText,
            ToolListProvider = DebugMcpContract.BuildToolList,
        });

        host.AddTool("visual_tree", VisualTreeAsync);
        host.AddTool("screenshot", _ => ScreenshotAsync());
        host.AddTool("about_dialog_probe", _ => AboutDialogProbeAsync());
        host.AddTool("ai_runtime_snapshot", _ => AiRuntimeSnapshotAsync());
        host.AddTool("terminal_tab_title_check", _ => TerminalTabTitleCheckAsync());
        host.AddTool("terminal_tab_focus_check", _ => TerminalTabFocusCheckAsync());
        host.AddTool("terminal_tab_lifecycle_check", _ => TerminalTabLifecycleCheckAsync());
        host.AddTool("terminal_connection_actions_check", _ => TerminalConnectionActionsCheckAsync());
        host.AddTool("terminal_output_coalescing_check", _ => TerminalOutputCoalescingCheckAsync());
        host.AddTool(
            "terminal_output_backpressure_check",
            _ => Task.FromResult(TerminalOutputBackpressureCheck()));
        host.AddTool("zmodem_subpacket_limit_check", _ => ZmodemSubpacketLimitCheckAsync());
        host.AddTool("ssh_auth_prompt_check", _ => Task.FromResult(SshAuthPromptCheck()));
        host.AddTool("sftp_retry_policy_check", _ => SftpRetryPolicyCheckAsync());
        host.AddTool("connection_write_watcher_check", _ => ConnectionWriteWatcherCheckAsync());
        host.AddTool("monitor_suspend_check", _ => MonitorSuspendCheckAsync());
        host.AddTool("terminal_font_sync_check", _ => TerminalFontSyncCheckAsync());
        host.AddTool("ai_panel_lifecycle_check", _ => AiPanelLifecycleCheckAsync());
        host.AddTool("file_browser_session_lifecycle_check", _ => FileBrowserSessionLifecycleCheckAsync());
        host.AddTool("ai_cli_ctrl_c_check", _ => AiCliCtrlCCheckAsync());
        host.AddTool("agent_cli_locate_check", AgentCliLocateCheckAsync);
        host.AddTool("agent_discovery_cache_check", _ => AgentDiscoveryCacheCheckAsync());
        host.AddTool("agent_cli_mcp_config_check", AgentCliMcpConfigCheckAsync);
        host.AddTool("login_menu_select_check", LoginMenuSelectCheckAsync);
        host.AddTool("login_command_flow_check", LoginCommandFlowCheckAsync);
        host.AddTool("login_command_completion_check", _ => LoginCommandCompletionCheckAsync());
        host.AddTool("login_command_variable_check", _ => LoginCommandVariableCheckAsync());
        host.AddTool("bastion_login_template_check", _ => BastionLoginTemplateCheckAsync());
        host.AddTool("bastion_template_preset_check", _ => BastionTemplatePresetCheckAsync());
        host.AddTool("conpty_teardown_race_check", _ => ConPtyTeardownRaceCheckAsync());
        host.AddTool("bastion_channel_limit_check", _ => BastionChannelLimitCheckAsync());
        host.AddTool("connection_editor_switch_check", _ => ConnectionEditorSwitchCheckAsync());
        host.AddTool("login_menu_select_probe", LoginMenuSelectProbeAsync);
        host.AddTool("auto_update_stage_check", AutoUpdateStageCheckAsync);
        host.AddTool("ai_render_probe", AiRenderProbeAsync);
        host.AddTool("agent_project_link_check", AgentProjectLinkCheckAsync);
        host.AddTool("agent_application_link_check", AgentApplicationLinkCheckAsync);
        host.AddTool("global_agent_check", _ => GlobalAgentCheckAsync());
        host.AddTool("mcp_transport_check", _ => McpTransportCheckAsync());
        host.AddTool("product_mcp_check", _ => ProductMcpCheckAsync());
        return host;
    }

    private static async Task<JsonObject> TerminalTabLifecycleCheckAsync()
    {
        const int cycles = 5;
        var weakViews = new List<WeakReference<TerminalView>>(cycles);

        for (var i = 0; i < cycles; i++)
            weakViews.Add(await CreateAndCloseTerminalLifecycleProbeAsync());

        // The async state machine may keep its most recently awaited result live
        // until this method returns. Use an untracked sentinel cycle so that the
        // five measured views have no probe-owned strong reference.
        _ = await CreateAndCloseTerminalLifecycleProbeAsync();

        // Let removal/unload work drain before forcing a compacting collection.
        await Task.Delay(100);
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

        var alive = weakViews.Count(reference => reference.TryGetTarget(out _));
        return ToolText(
            $"{(alive == 0 ? "PASS" : "FAIL")}: closed terminal views are collectible.\n"
            + $"cycles={cycles}\nalive={alive}");
    }

    private static async Task<WeakReference<TerminalView>> CreateAndCloseTerminalLifecycleProbeAsync()
    {
        var tab = await OnUiAsync(() =>
        {
            if (Desktop?.MainWindow is not Views.MainWindow main)
                throw new InvalidOperationException("MainWindow is not available.");
            return main.DebugCreateTerminalTabForLifecycleProbe();
        });

        // Give Avalonia one layout pass so the probe covers loaded bindings and
        // compositor resources rather than only constructor-time objects.
        await Task.Delay(75);
        var weakView = await OnUiAsync(() =>
        {
            if (tab.Content is not TerminalView view)
                throw new InvalidOperationException("Lifecycle probe tab has no TerminalView.");
            return new WeakReference<TerminalView>(view);
        });

        await OnUiAsync(() =>
        {
            if (Desktop?.MainWindow is not Views.MainWindow main)
                throw new InvalidOperationException("MainWindow is not available.");
            main.CloseTerminalSession(tab);
            return true;
        });

        return weakView;
    }

    private static async Task<JsonObject> TerminalConnectionActionsCheckAsync()
    {
        var tabs = new List<TabItem>();
        try
        {
            var result = await OnUiAsync(() =>
            {
                if (Desktop?.MainWindow is not Views.MainWindow main)
                    throw new InvalidOperationException("MainWindow is not available.");

                var before = main.EnumerateTerminalSessions().Count;
                var probe = main.DebugOpenConnectionActionsProbe();
                tabs.Add(probe.Source);
                tabs.Add(probe.NewSession);
                tabs.Add(probe.NewTcpConnection);
                var newSessionView = (TerminalView)probe.NewSession.Content!;
                var newTcpView = (TerminalView)probe.NewTcpConnection.Content!;
                var after = main.EnumerateTerminalSessions().Count;
                var connectReusedTab = ReferenceEquals(probe.Source, probe.Connected);
                var newSessionUsesDuplicatePolicy =
                    newSessionView.DebugIsDuplicatedSession
                    && !newSessionView.DebugRequiresNewTcpConnection;
                var newTcpRequiresNewTransport =
                    !newTcpView.DebugIsDuplicatedSession
                    && newTcpView.DebugRequiresNewTcpConnection
                    && newTcpView.BastionSessionState == "new-tcp-forced";
                var passed = after == before + 3
                             && connectReusedTab
                             && newSessionUsesDuplicatePolicy
                             && newTcpRequiresNewTransport;
                return (
                    passed,
                    before,
                    after,
                    connectReusedTab,
                    newSessionUsesDuplicatePolicy,
                    newTcpRequiresNewTransport,
                    newTcpView.BastionSessionState);
            });

            return ToolText(
                $"{(result.passed ? "PASS" : "FAIL")}: connection actions have distinct transport semantics.\n"
                + $"tabsBefore={result.before}\n"
                + $"tabsAfter={result.after}\n"
                + $"connectReusedTab={result.connectReusedTab}\n"
                + $"newSessionUsesDuplicatePolicy={result.newSessionUsesDuplicatePolicy}\n"
                + $"newTcpRequiresNewTransport={result.newTcpRequiresNewTransport}\n"
                + $"transportState={result.BastionSessionState}",
                isError: !result.passed);
        }
        finally
        {
            if (tabs.Count > 0)
            {
                await OnUiAsync(() =>
                {
                    if (Desktop?.MainWindow is Views.MainWindow main)
                    {
                        for (var i = tabs.Count - 1; i >= 0; i--)
                            main.CloseTerminalSession(tabs[i]);
                    }
                    return true;
                });
            }
        }
    }

    /// <summary>
    /// Each product-MCP write should rebuild the tree exactly once, from the explicit
    /// reload the handler performs. The file watcher recognises the app's own writes by
    /// comparing against ConnectionStore.LastWriteTick — which is per-instance, so a
    /// handler that built its own store left the watcher firing a second, redundant
    /// full-tree rebuild a moment later.
    /// </summary>
    private static async Task<JsonObject> ConnectionWriteWatcherCheckAsync()
    {
        const string folder = "_mcp_watcher_selftest";
        const string connection = folder + "/probe";

        var pipeName = ProductMcpServer.PipeName;
        if (pipeName.Length == 0)
            return ToolText("FAIL: the product MCP server is not listening.", isError: true);

        static Task<long> ReloadCountAsync() => OnUiAsync(() =>
            (Desktop?.MainWindow?.DataContext as ViewModels.MainWindowViewModel)
            ?.TreeReloadCountForDebug ?? -1);

        var measurements = new List<(string Step, long Reloads)>();
        var failures = new List<string>();

        await using var session = await OpenPipeSessionAsync(pipeName).ConfigureAwait(false);
        await session.CallAsync(
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}""")
            .ConfigureAwait(false);

        async Task Measure(string step, string request)
        {
            // Let any watcher event from the previous step land before taking a baseline.
            await Task.Delay(1200).ConfigureAwait(false);
            var before = await ReloadCountAsync().ConfigureAwait(false);
            await session.CallAsync(request).ConfigureAwait(false);
            // Longer than the 400 ms watcher debounce plus the 1 s self-write window, so a
            // watcher-driven reload has every chance to show up before we count.
            await Task.Delay(1800).ConfigureAwait(false);
            var after = await ReloadCountAsync().ConfigureAwait(false);
            var reloads = after - before;
            measurements.Add((step, reloads));
            if (reloads != 1)
                failures.Add($"{step}: expected 1 tree reload, saw {reloads}");
        }

        static string Call(int id, string tool, string arguments) =>
            new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = tool,
                    ["arguments"] = JsonNode.Parse(arguments),
                },
            }.ToJsonString();

        try
        {
            await Measure(
                "connection_create",
                Call(10, "connection_create",
                    $$"""
                      {"name":"probe","folder":"{{folder}}","type":"SSH",
                       "host":"probe.invalid","port":22,"username":"probe"}
                      """));
            await Measure(
                "connection_update",
                Call(11, "connection_update",
                    $$"""{"connection":"{{connection}}","host":"probe2.invalid"}"""));
            // connection_set_secret is deliberately left out: it only writes when the
            // master password is unlocked, so its reload count is not deterministic here.
            await Measure(
                "connection_move",
                Call(13, "connection_move",
                    $$"""{"connection":"{{connection}}","folder":""}"""));
        }
        finally
        {
            // connection_delete and folder_delete block on a GUI confirmation by design,
            // so clean up straight through the store instead of hanging the probe.
            await OnUiAsync(() =>
            {
                if (Desktop?.MainWindow?.DataContext is ViewModels.MainWindowViewModel vm)
                {
                    vm.Store.DeleteFile(Path.Combine(vm.RootPath, "probe" + ConnectionStore.FileExtension));
                    vm.Store.DeleteFolder(Path.Combine(vm.RootPath, folder));
                    vm.ReloadTreeFromDisk();
                }
                return true;
            }).ConfigureAwait(false);
        }

        var passed = failures.Count == 0;
        var report =
            $"{(passed ? "PASS" : "FAIL")}: product MCP writes reload the tree once\n"
            + string.Join("\n", measurements.Select(m => $"{m.Step}: reloads={m.Reloads}"))
            + $"\nfailures={failures.Count}"
            + (passed ? "" : "\n" + string.Join("\n", failures));
        return ToolText(report, isError: !passed);
    }

    /// <summary>
    /// Records what retry policy each browser action asks for, and fails the first attempt
    /// the way a dropped transport does, so a replayed delete would show up as a second
    /// call. The classification lives at the call sites — this is what pins it down.
    /// </summary>
    private sealed class RetryPolicyProbeSession : IFileSystemSession
    {
        private readonly List<(string Label, FileSystemRetry Retry, int Attempts)> _calls = [];

        public string? HomePath => "/home/probe";

        public bool SupportsPermissions => true;

        /// <summary>Set before each action so the recorded call can be attributed.</summary>
        public string CurrentLabel { get; set; } = "";

        public IReadOnlyList<(string Label, FileSystemRetry Retry, int Attempts)> Calls => _calls;

        public Task<T> RunAsync<T>(
            Func<IFileSystemOps, T> operation,
            FileSystemRetry retry = FileSystemRetry.Once,
            CancellationToken cancellationToken = default)
        {
            var attempts = 0;
            var ops = LifecycleProbeFileSystemOps.Instance;
            T result;
            try
            {
                attempts++;
                // Mimic SftpSession: the transport dies mid-operation, and only an
                // idempotent operation is replayed on the fresh connection.
                throw new Renci.SshNet.Common.SshConnectionException("probe: transport dropped");
            }
            catch (Renci.SshNet.Common.SshConnectionException) when (retry == FileSystemRetry.Idempotent)
            {
                attempts++;
                result = operation(ops);
            }
            finally
            {
                lock (_calls)
                    _calls.Add((CurrentLabel, retry, attempts));
            }

            return Task.FromResult(result);
        }

        public void Dispose()
        {
        }
    }

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

        void Expect(string name, AuthenticationPromptEventArgs challenge, params string?[] expected)
        {
            SshConnectionFactory.AnswerPasswordPrompts(challenge, secret);
            var actual = challenge.Prompts.Select(prompt => prompt.Response).ToArray();
            if (actual.Length != expected.Length
                || actual.Where((response, i) => response != expected[i]).Any())
            {
                failures.Add(
                    $"{name}: expected [{string.Join(", ", expected.Select(v => v ?? "<null>"))}] "
                    + $"but got [{string.Join(", ", actual.Select(v => v ?? "<null>"))}]");
            }
        }

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

        // A configured key path that does not exist must be named, not swallowed into a
        // generic "no usable credential" that sends the user hunting.
        var missingKeyPath = Path.Combine(Path.GetTempPath(), "JeekRemoteManager.NoSuchKey.pem");
        var namesMissingKey = false;
        var missingKeyMessage = "";
        try
        {
            SshConnectionFactory.Build(new Connection
            {
                Type = ConnectionType.Ssh,
                Host = "example.invalid",
                Port = 22,
                Username = "probe",
                PrivateKeyPath = missingKeyPath,
            });
            missingKeyMessage = "Build succeeded with a missing key file";
        }
        catch (Exception ex)
        {
            missingKeyMessage = ex.Message;
            namesMissingKey = ex.Message.Contains(missingKeyPath, StringComparison.OrdinalIgnoreCase);
        }

        if (!namesMissingKey)
            failures.Add($"missing key file: {missingKeyMessage}");

        var passed = failures.Count == 0;
        var report =
            $"{(passed ? "PASS" : "FAIL")}: SSH keyboard-interactive and key-path diagnostics\n"
            + $"namesMissingKey={namesMissingKey}\n"
            + $"missingKeyMessage={missingKeyMessage}\n"
            + $"failures={failures.Count}"
            + (passed ? "" : "\n" + string.Join("\n", failures));
        return ToolText(report, isError: !passed);
    }

    /// <summary>
    /// A peer that never sends a frame terminator — a garbled link, or something that is
    /// not lrzsz at all — used to make the subpacket reader accumulate until the process
    /// died. Drives a real receive session against exactly that and checks it gives up.
    /// </summary>
    private static async Task<JsonObject> ZmodemSubpacketLimitCheckAsync()
    {
        // "*" "*" ZDLE "B" then the 5 header bytes and their CRC16, hex-encoded. Hex
        // headers need no escaping, so this stays readable without reaching into the
        // session's private encoder.
        var header = new List<byte>
        {
            ZmodemConstants.ZPAD, ZmodemConstants.ZPAD, ZmodemConstants.ZDLE, ZmodemConstants.ZHEX,
        };
        byte[] headerBytes = [(byte)ZmodemHeaderType.ZFILE, 0, 0, 0, 0];
        var crc = ZmodemCrc.Crc16(headerBytes);
        foreach (var b in headerBytes.Concat([(byte)(crc >> 8), (byte)crc]))
        {
            const string digits = "0123456789abcdef";
            header.Add((byte)digits[b >> 4]);
            header.Add((byte)digits[b & 0x0f]);
        }
        header.Add(ZmodemConstants.CR);
        header.Add(ZmodemConstants.LF_HIGH);

        // Never send a terminator: after the header it is plain data forever.
        var scripted = header.ToArray();
        var offset = 0;
        long consumed = 0;
        var runawayGuard = ZmodemSession.MaxDataSubpacketBytes * 4L;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        ValueTask<byte> ReadByte(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            consumed++;
            if (consumed > runawayGuard)
                throw new InvalidOperationException("runaway");
            return new ValueTask<byte>(offset < scripted.Length ? scripted[offset++] : (byte)'A');
        }

        long written = 0;
        Task Write(byte[] bytes, CancellationToken _)
        {
            written += bytes.Length;
            return Task.CompletedTask;
        }

        var destination = Path.Combine(
            Path.GetTempPath(),
            "JeekRemoteManager.ZmodemLimitProbe." + Guid.NewGuid().ToString("N"));
        var session = new ZmodemSession(Write, ReadByte);
        string outcome;
        var bounded = false;
        try
        {
            await session.ReceiveAsync(destination, stop.Token).ConfigureAwait(false);
            outcome = "completed without error";
        }
        catch (InvalidDataException ex)
        {
            outcome = ex.Message;
            bounded = true;
        }
        catch (Exception ex)
        {
            outcome = $"{ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            try { Directory.Delete(destination, recursive: true); } catch { /* best effort */ }
        }

        // Bounded means it stopped near the cap, not merely that it stopped eventually.
        var stoppedNearCap = consumed <= runawayGuard;
        var passed = bounded && stoppedNearCap;
        var report =
            $"{(passed ? "PASS" : "FAIL")}: ZMODEM subpacket reads are bounded\n"
            + $"capBytes={ZmodemSession.MaxDataSubpacketBytes}\n"
            + $"bytesConsumed={consumed}\n"
            + $"bytesWritten={written}\n"
            + $"stoppedNearCap={stoppedNearCap}\n"
            + $"outcome={outcome}";
        return ToolText(report, isError: !passed);
    }

    /// <summary>
    /// The queue only grows while the UI thread cannot drain it, so a remote spraying
    /// output used to be an out-of-memory kill. Feeds well past the cap without ever
    /// draining and checks that memory stays bounded and the newest bytes survive.
    /// </summary>
    private static JsonObject TerminalOutputBackpressureCheck()
    {
        const int generation = 7;
        var buffer = new TerminalSessionOutputBuffer();
        var packet = new byte[64 * 1024];

        // Four times the cap, so trimming has to happen repeatedly rather than once.
        var packetCount = TerminalSessionOutputBuffer.MaxPendingBytes / packet.Length * 4;
        for (var i = 0; i < packetCount; i++)
        {
            // Stamp each packet so the surviving tail is identifiable.
            Array.Fill(packet, (byte)(i & 0xff));
            buffer.Append(packet, generation);
        }

        var fedBytes = (long)packetCount * packet.Length;
        var boundedWhileFilling = buffer.PendingByteCount <= TerminalSessionOutputBuffer.MaxPendingBytes;

        var drained = buffer.Drain(generation);
        var dropped = buffer.TakeDroppedByteCount();
        var boundedAfterDrain = drained.Length <= TerminalSessionOutputBuffer.MaxPendingBytes;
        var accountsForEveryByte = drained.Length + dropped == fedBytes;
        var keptNewest = drained.Length > 0 && drained[^1] == (byte)((packetCount - 1) & 0xff);
        var reportsOnce = buffer.TakeDroppedByteCount() == 0;
        var emptyAfterDrain = buffer.PendingByteCount == 0 && buffer.PendingPacketCount == 0;

        var passed = boundedWhileFilling
                     && boundedAfterDrain
                     && accountsForEveryByte
                     && keptNewest
                     && reportsOnce
                     && emptyAfterDrain;
        var report =
            $"{(passed ? "PASS" : "FAIL")}: terminal output buffer is bounded under flood\n"
            + $"capBytes={TerminalSessionOutputBuffer.MaxPendingBytes}\n"
            + $"fedBytes={fedBytes}\n"
            + $"drainedBytes={drained.Length}\n"
            + $"droppedBytes={dropped}\n"
            + $"boundedWhileFilling={boundedWhileFilling}\n"
            + $"boundedAfterDrain={boundedAfterDrain}\n"
            + $"accountsForEveryByte={accountsForEveryByte}\n"
            + $"keptNewest={keptNewest}\n"
            + $"reportsOnce={reportsOnce}\n"
            + $"emptyAfterDrain={emptyAfterDrain}";
        return ToolText(report, isError: !passed);
    }

    private static async Task<JsonObject> TerminalOutputCoalescingCheckAsync()
    {
        const int packetCount = 200;
        TabItem? tab = null;
        try
        {
            await OnUiAsync(() =>
            {
                if (Desktop?.MainWindow is not Views.MainWindow main)
                    throw new InvalidOperationException("MainWindow is not available.");

                tab = main.DebugCreateTerminalTabForLifecycleProbe();
                var view = (TerminalView)tab.Content!;
                view.DebugResetTerminalOutputStats();
                var packet = Encoding.UTF8.GetBytes("x");
                for (var i = 0; i < packetCount; i++)
                    view.DebugFeedUtf8Bytes(packet);
                return true;
            });

            await Task.Delay(100);
            var result = await OnUiAsync(() =>
            {
                var view = (TerminalView)tab!.Content!;
                var stats = view.DebugTerminalOutputStats;
                var renderedBytes = view.DebugVisibleTerminalText.Count(character => character == 'x');
                var passed = stats.ReceivedPackets == packetCount
                             && stats.FeedBatches == 1
                             && stats.PendingPackets == 0
                             && renderedBytes == packetCount;
                return (
                    passed,
                    $"{(passed ? "PASS" : "FAIL")}: terminal output is coalesced per UI frame.\n"
                    + $"packets={stats.ReceivedPackets}\n"
                    + $"batches={stats.FeedBatches}\n"
                    + $"pending={stats.PendingPackets}\n"
                    + $"renderedBytes={renderedBytes}");
            });
            return ToolText(result.Item2, isError: !result.passed);
        }
        finally
        {
            if (tab is not null)
            {
                await OnUiAsync(() =>
                {
                    if (Desktop?.MainWindow is Views.MainWindow main)
                        main.CloseTerminalSession(tab);
                    return true;
                });
            }
        }
    }

    /// <summary>
    /// Verifies that server monitor sampling follows tab visibility: it runs for the
    /// visible tab, does NOT stop the moment the tab goes to the background (a grace
    /// period absorbs tab flipping), suspends once that period elapses, and resumes
    /// immediately when the tab comes back.
    /// </summary>
    private static async Task<JsonObject> MonitorSuspendCheckAsync()
    {
        TabItem? tab = null;
        try
        {
            var report = new StringBuilder();
            var passed = true;

            void Expect(bool condition, string label)
            {
                passed &= condition;
                report.Append(condition ? "  ok   " : "  FAIL ").Append(label).Append('\n');
            }

            var (view, main) = await OnUiAsync(() =>
            {
                if (Desktop?.MainWindow is not Views.MainWindow window)
                    throw new InvalidOperationException("MainWindow is not available.");

                tab = window.DebugCreateSshTerminalTabForMonitorProbe();
                var terminal = (TerminalView)tab.Content!;
                terminal.ToggleMonitorPanel();
                return (terminal, window);
            });

            var active = await OnUiAsync(() =>
                (view.IsMonitorPanelOpen, view.IsMonitorSamplingSuspended, view.IsMonitorSuspendPending));
            Expect(active.IsMonitorPanelOpen, "monitor panel opened on the SSH probe tab");
            Expect(!active.IsMonitorSamplingSuspended, "sampling runs while the tab is visible");
            Expect(!active.IsMonitorSuspendPending, "no suspend is pending while the tab is visible");

            // Move to another tab: sampling must keep going for now.
            await OnUiAsync(() =>
            {
                main.DebugSelectTab(main.DebugEditorTab);
                return true;
            });
            await Task.Delay(100);
            var backgrounded = await OnUiAsync(() =>
                (view.IsMonitorSamplingSuspended, view.IsMonitorSuspendPending));
            Expect(!backgrounded.IsMonitorSamplingSuspended,
                "sampling is NOT stopped the instant the tab goes to the background");
            Expect(backgrounded.IsMonitorSuspendPending, "a suspend is pending during the grace period");

            // Flipping back within the grace period must cancel the pending suspend.
            await OnUiAsync(() =>
            {
                main.DebugSelectTab(tab!);
                return true;
            });
            var flippedBack = await OnUiAsync(() =>
                (view.IsMonitorSamplingSuspended, view.IsMonitorSuspendPending));
            Expect(!flippedBack.IsMonitorSamplingSuspended, "returning to the tab keeps sampling running");
            Expect(!flippedBack.IsMonitorSuspendPending, "returning to the tab cancels the pending suspend");

            // Background it again and let the grace period elapse.
            await OnUiAsync(() =>
            {
                main.DebugSelectTab(main.DebugEditorTab);
                return true;
            });
            await OnUiAsync(() =>
            {
                view.FlushPendingMonitorSuspend();
                return true;
            });
            var suspended = await OnUiAsync(() =>
                (view.IsMonitorSamplingSuspended, view.IsMonitorSuspendPending));
            Expect(suspended.IsMonitorSamplingSuspended, "sampling is suspended once the grace period elapses");
            Expect(!suspended.IsMonitorSuspendPending, "no suspend stays pending after it fired");

            // Coming back resumes immediately.
            await OnUiAsync(() =>
            {
                main.DebugSelectTab(tab!);
                return true;
            });
            var resumed = await OnUiAsync(() =>
                (view.IsMonitorSamplingSuspended, view.IsMonitorSuspendPending));
            Expect(!resumed.IsMonitorSamplingSuspended, "sampling resumes when the tab is shown again");
            Expect(!resumed.IsMonitorSuspendPending, "no suspend is pending after resuming");

            return ToolText(
                $"{(passed ? "PASS" : "FAIL")}: server monitor sampling follows tab visibility.\n{report}",
                isError: !passed);
        }
        finally
        {
            if (tab is not null)
            {
                await OnUiAsync(() =>
                {
                    if (Desktop?.MainWindow is Views.MainWindow main)
                        main.CloseTerminalSession(tab);
                    return true;
                });
            }
        }
    }

    private static async Task<JsonObject> TerminalFontSyncCheckAsync()
    {
        TabItem? tab = null;
        var globalAgentWasOpen = false;
        var originalSize = 0;
        var delta = 0;

        try
        {
            var result = await OnUiAsync(() =>
            {
                if (Desktop?.MainWindow is not Views.MainWindow main
                    || main.DataContext is not MainWindowViewModel vm)
                {
                    throw new InvalidOperationException("MainWindow is not available.");
                }

                globalAgentWasOpen = main.IsGlobalAgentTabOpen;
                originalSize = vm.TerminalFontSize;
                delta = originalSize < 36 ? 1 : -1;

                tab = main.DebugCreateTerminalTabForLifecycleProbe();
                var terminal = (TerminalView)tab.Content!;
                _ = main.PrepareGlobalAgentTabForDebug();

                var initialTerminalSize = terminal.DebugTerminalFontSize;
                var initialEmbeddedAiSize = terminal.DebugAiPanel.TerminalFontSize;
                var initialGlobalAiSize = main.DebugGlobalAgentPanel.TerminalFontSize;

                if (delta > 0)
                    vm.IncreaseTerminalFontCommand.Execute(null);
                else
                    vm.DecreaseTerminalFontCommand.Execute(null);

                var expectedSize = originalSize + delta;
                var terminalSize = terminal.DebugTerminalFontSize;
                var embeddedAiSize = terminal.DebugAiPanel.TerminalFontSize;
                var globalAiSize = main.DebugGlobalAgentPanel.TerminalFontSize;
                var passed = initialTerminalSize == originalSize
                             && initialEmbeddedAiSize == originalSize
                             && initialGlobalAiSize == originalSize
                             && vm.TerminalFontSize == expectedSize
                             && terminalSize == expectedSize
                             && embeddedAiSize == expectedSize
                             && globalAiSize == expectedSize;

                return (
                    passed,
                    expectedSize,
                    terminalSize,
                    embeddedAiSize,
                    globalAiSize,
                    initialTerminalSize,
                    initialEmbeddedAiSize,
                    initialGlobalAiSize);
            });

            return ToolText(
                $"{(result.passed ? "PASS" : "FAIL")}: SSH terminal font controls update every AI CLI panel.\n"
                + $"original={originalSize}\n"
                + $"expected={result.expectedSize}\n"
                + $"initialTerminal={result.initialTerminalSize}\n"
                + $"initialEmbeddedAi={result.initialEmbeddedAiSize}\n"
                + $"initialGlobalAi={result.initialGlobalAiSize}\n"
                + $"terminal={result.terminalSize}\n"
                + $"embeddedAi={result.embeddedAiSize}\n"
                + $"globalAi={result.globalAiSize}",
                isError: !result.passed);
        }
        finally
        {
            Task? closeGlobalAgentTask = null;
            await OnUiAsync(() =>
            {
                if (Desktop?.MainWindow is not Views.MainWindow main
                    || main.DataContext is not MainWindowViewModel vm)
                {
                    return false;
                }

                if (vm.TerminalFontSize != originalSize && delta != 0)
                {
                    if (delta > 0)
                        vm.DecreaseTerminalFontCommand.Execute(null);
                    else
                        vm.IncreaseTerminalFontCommand.Execute(null);
                }

                if (tab is not null)
                    main.CloseTerminalSession(tab);

                if (!globalAgentWasOpen && main.IsGlobalAgentTabOpen)
                    closeGlobalAgentTask = main.CloseGlobalAgentAsync();

                return true;
            });

            if (closeGlobalAgentTask is not null)
                await closeGlobalAgentTask;
        }
    }

    private static async Task<JsonObject> AiPanelLifecycleCheckAsync()
    {
        TabItem? firstTab = null;
        TabItem? secondTab = null;
        AgentCliPanelViewModel? closedViewModel = null;
        try
        {
            var setup = await OnUiAsync(() =>
            {
                if (Desktop?.MainWindow is not Views.MainWindow main)
                    throw new InvalidOperationException("MainWindow is not available.");

                firstTab = main.DebugCreateTerminalTabForLifecycleProbe();
                var firstView = (TerminalView)firstTab.Content!;
                firstView.ToggleAiPanel();
                closedViewModel = firstView.AiViewModel
                                  ?? throw new InvalidOperationException("AI view model was not created.");
                var closeTask = firstView.CloseAiPanelAsync();
                var detached = !firstView.IsAiPanelOpen
                               && firstView.AiViewModel is null
                               && firstView.DebugAiPanel.DataContext is null;

                secondTab = main.DebugCreateTerminalTabForLifecycleProbe();
                var secondView = (TerminalView)secondTab.Content!;
                var freshTabClosed = !secondView.IsAiPanelOpen && secondView.AiViewModel is null;
                return (closeTask, detached, freshTabClosed);
            });

            await setup.closeTask;
            var disposedViewModel = closedViewModel
                                    ?? throw new InvalidOperationException("AI view model was not captured.");
            var passed = setup.detached
                         && setup.freshTabClosed
                         && disposedViewModel.IsDisposed
                         && !disposedViewModel.IsRunning
                         && !disposedViewModel.HasEmbeddedSession;
            return ToolText(
                $"{(passed ? "PASS" : "FAIL")}: AI panel close releases its runtime.\n"
                + $"detached={setup.detached}\n"
                + $"disposed={disposedViewModel.IsDisposed}\n"
                + $"running={disposedViewModel.IsRunning}\n"
                + $"embeddedSession={disposedViewModel.HasEmbeddedSession}\n"
                + $"freshTabClosed={setup.freshTabClosed}",
                isError: !passed);
        }
        finally
        {
            await OnUiAsync(() =>
            {
                if (Desktop?.MainWindow is Views.MainWindow main)
                {
                    if (secondTab is not null)
                        main.CloseTerminalSession(secondTab);
                    if (firstTab is not null)
                        main.CloseTerminalSession(firstTab);
                }
                return true;
            });
        }
    }

    private static async Task<JsonObject> FileBrowserSessionLifecycleCheckAsync()
    {
        var created = 0;
        var disposed = 0;
        FileBrowserViewModel? viewModel = null;
        try
        {
            viewModel = await OnUiAsync(() =>
                new FileBrowserViewModel(
                    () =>
                    {
                        created++;
                        return new LifecycleProbeFileSystemSession(() => disposed++);
                    },
                    _ => { },
                    "lifecycle-probe")
                {
                    HiddenSessionIdleTimeoutForDebug = TimeSpan.FromMilliseconds(25),
                });

            var firstLoad = await OnUiAsync(() =>
            {
                viewModel.NotifyPanelShown();
                return viewModel.EnsureLoadedAsync();
            });
            await firstLoad;

            var firstSessionReady = await OnUiAsync(() =>
            {
                var ready = created == 1 && viewModel.HasBrowseSession;
                viewModel.NotifyPanelHidden();
                var activeTransfer = new FileTransferItem("active", isUpload: true);
                viewModel.Transfers.Add(activeTransfer);
                return ready;
            });

            await Task.Delay(75);
            var transferBlockedRelease = await OnUiAsync(() =>
            {
                var blocked = viewModel.HasBrowseSession;
                var activeTransfer = viewModel.Transfers.Single();
                activeTransfer.IsFinished = true;
                viewModel.Transfers.Clear();
                viewModel.NotifyPanelHidden();
                return blocked;
            });

            await Task.Delay(75);
            var released = await OnUiAsync(() =>
                !viewModel.HasBrowseSession && disposed == 1);

            var reload = await OnUiAsync(() =>
            {
                viewModel.NotifyPanelShown();
                return viewModel.EnsureLoadedAsync();
            });
            await reload;

            var reopened = await OnUiAsync(() =>
                created == 2
                && viewModel.HasBrowseSession
                && viewModel.CurrentPath == "/home/probe");
            var passed = firstSessionReady
                         && transferBlockedRelease
                         && released
                         && reopened;
            return ToolText(
                $"{(passed ? "PASS" : "FAIL")}: hidden file-browser sessions recycle safely.\n"
                + $"firstSessionReady={firstSessionReady}\n"
                + $"activeTransferBlocked={transferBlockedRelease}\n"
                + $"released={released}\n"
                + $"reopened={reopened}\n"
                + $"created={created}\n"
                + $"disposed={disposed}",
                isError: !passed);
        }
        finally
        {
            if (viewModel is not null)
                await OnUiAsync(() => { viewModel.Dispose(); return true; });
        }
    }

    private sealed class LifecycleProbeFileSystemSession(Action onDispose) : IFileSystemSession
    {
        private bool _disposed;

        public string? HomePath => "/home/probe";

        public bool SupportsPermissions => true;

        public Task<T> RunAsync<T>(
            Func<IFileSystemOps, T> operation,
            FileSystemRetry retry = FileSystemRetry.Once,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(operation(LifecycleProbeFileSystemOps.Instance));
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            onDispose();
        }
    }

    private sealed class LifecycleProbeFileSystemOps : IFileSystemOps
    {
        public static readonly LifecycleProbeFileSystemOps Instance = new();

        public string WorkingDirectory => "/home/probe";

        public IEnumerable<FileSystemEntry> ListDirectory(string path) => [];

        public void CreateDirectory(string path)
        {
        }

        public void RenameFile(string oldPath, string newPath)
        {
        }

        public void DeleteFile(string path)
        {
        }

        public void DeleteDirectory(string path)
        {
        }

        public bool Exists(string path) => false;

        public void ChangePermissions(string path, short mode)
        {
        }

        public void UploadFile(Stream source, string remotePath, Action<ulong> progress)
        {
        }

        public void DownloadFile(string remotePath, Stream destination, Action<ulong> progress)
        {
        }
    }

    private static Task<T> OnUiAsync<T>(Func<T> func) => Host.OnUiAsync(func);

    private static JsonObject ToolText(string text, bool isError = false) =>
        McpHost.ToolText(text, isError);

    #region Discovery

    private static void OnEndpointChanged()
    {
        var endpoint = Host.PipeName.Length > 0 ? $@"\\.\pipe\{Host.PipeName}" : Host.Url;
        DebugInstanceContext.SetMcpUrl(endpoint);
        if (endpoint.Length > 0)
        {
            WriteDiscovery();
            Log.ZLogInformation($"Debug MCP listening on {endpoint} for {DebugInstanceContext.InstanceLabel}");
        }
        else
        {
            DeleteOwnedDiscovery();
        }
    }

    private static void WriteDiscovery()
    {
        try
        {
            var info = DebugInstanceContext.Info;
            var discovery = new DebugMcpDiscovery
            {
                Url = Host.Url,
                PipeName = Host.PipeName,
                ProcessId = Environment.ProcessId,
                ExecutablePath = Environment.ProcessPath ?? "",
                InstanceId = info.InstanceId,
                InstanceLabel = info.InstanceLabel,
                WorkspaceRoot = info.WorkspaceRoot,
                ConfigRoot = info.ConfigRoot,
                RuntimeTempRoot = info.RuntimeTempRoot,
            };
            SharedDataFile.WriteAllTextAtomic(
                DebugInstanceContext.DiscoveryPath,
                JsonSerializer.Serialize(discovery, PrettyOptions));
        }
        catch (Exception ex)
        {
            Log.ZLogWarning(ex, $"Could not write Debug MCP discovery file");
        }
    }

    private static void DeleteOwnedDiscovery()
    {
        try
        {
            var path = DebugInstanceContext.DiscoveryPath;
            if (!File.Exists(path))
                return;
            var discovery = JsonSerializer.Deserialize<DebugMcpDiscovery>(File.ReadAllText(path));
            if (discovery?.ProcessId == Environment.ProcessId)
                File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup; the bridge rejects stale process ids.
        }
    }

    #endregion

    #region Roots

    private static IClassicDesktopStyleApplicationLifetime? Desktop =>
        Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;

    private static object ResolveRoot(string name) => name switch
    {
        "App" => Application.Current
                 ?? throw new InvalidOperationException("Application.Current is null."),
        "Desktop" => Desktop
                     ?? throw new InvalidOperationException("No desktop lifetime."),
        "MainWindow" => Desktop?.MainWindow
                        ?? throw new InvalidOperationException("MainWindow is not created yet (master password not unlocked?)."),
        "MainVm" => Desktop?.MainWindow?.DataContext
                    ?? throw new InvalidOperationException("MainWindow.DataContext is not set yet."),
        _ => throw new InvalidOperationException($"Unknown root '{name}'. Available roots: App, Desktop, MainWindow, MainVm."),
    };

    private static Visual? FindDescendantByName(Visual root, string name)
    {
        var queue = new Queue<Visual>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var visual = queue.Dequeue();
            if (visual is StyledElement styled && styled.Name == name)
                return visual;
            foreach (var child in visual.GetVisualChildren())
                queue.Enqueue(child);
        }

        return null;
    }

    #endregion

    #region Describe

    private static string BuildDescribeText()
    {
        var sb = new StringBuilder();
        var instance = DebugInstanceContext.Info;
        sb.AppendLine($"JeekRemoteManager debug MCP server at {Host.Url} (build {AutoUpdateService.GetLocalCommitCount()}).");
        sb.AppendLine($"InstanceId: {instance.InstanceId}");
        sb.AppendLine($"InstanceLabel: {instance.InstanceLabel}");
        sb.AppendLine($"WorkspaceRoot: {instance.WorkspaceRoot}");
        sb.AppendLine($"ProcessId: {instance.ProcessId}");
        sb.AppendLine($"McpUrl: {instance.McpUrl}");
        sb.AppendLine($"ConfigRoot: {instance.ConfigRoot}");
        sb.AppendLine($"RuntimeTempRoot: {instance.RuntimeTempRoot}");
        sb.AppendLine($"Process uptime: {DateTime.Now - Process.GetCurrentProcess().StartTime:hh\\:mm\\:ss}.");
        sb.AppendLine($"Log file: {LogManager.CurrentRollingLogFile}");
        sb.AppendLine();
        sb.AppendLine("Roots for object paths:");
        sb.AppendLine("- App: the Avalonia Application instance");
        sb.AppendLine("- Desktop: the IClassicDesktopStyleApplicationLifetime (Windows list, Shutdown, ...)");
        sb.AppendLine("- MainWindow: the main window (null until the master password is unlocked)");
        sb.AppendLine("- MainVm: MainWindow.DataContext (MainWindowViewModel)");
        sb.AppendLine();
        sb.AppendLine(DebugMcpContract.PathHelp);
        sb.AppendLine();

        if (Desktop is not { } desktop)
        {
            sb.AppendLine("No desktop lifetime yet.");
        }
        else
        {
            sb.AppendLine($"Windows ({desktop.Windows.Count}):");
            foreach (var window in desktop.Windows)
            {
                sb.AppendLine(
                    $"- {window.GetType().Name} \"{window.Title}\" Visible={window.IsVisible} " +
                    $"State={window.WindowState} ClientSize={window.ClientSize} " +
                    $"DataContext={window.DataContext?.GetType().Name ?? "null"}");
            }
        }

        return sb.ToString();
    }

    #endregion

    #region Avalonia tools

    private const int MaxVisualNodes = 2000;

    private static async Task<JsonObject> VisualTreeAsync(JsonObject args)
    {
        var path = args["path"]?.GetValue<string>() ?? "MainWindow";
        var maxDepth = Math.Max(1, args["max_depth"]?.GetValue<int>() ?? 12);

        var text = await OnUiAsync(() =>
        {
            if (Graph.Resolve(path) is not Visual root)
                throw new InvalidOperationException($"'{path}' is not a Visual.");

            var sb = new StringBuilder();
            var count = 0;
            AppendVisual(sb, root, 0, maxDepth, null, ref count);
            if (count >= MaxVisualNodes)
                sb.AppendLine($"… truncated at {MaxVisualNodes} nodes.");
            return sb.ToString();
        });

        return ToolText(text);
    }

    private static void AppendVisual(
        StringBuilder sb, Visual visual, int depth, int maxDepth, object? parentDataContext, ref int count)
    {
        if (count >= MaxVisualNodes)
            return;
        count++;

        sb.Append(' ', depth * 2).Append(visual.GetType().Name);

        var dataContext = parentDataContext;
        if (visual is StyledElement styled)
        {
            if (!string.IsNullOrEmpty(styled.Name))
                sb.Append(" #").Append(styled.Name);
            var classes = string.Join(' ', styled.Classes);
            if (classes.Length > 0)
                sb.Append(" (").Append(classes).Append(')');
            dataContext = styled.DataContext;
            if (dataContext != null && !ReferenceEquals(dataContext, parentDataContext))
                sb.Append(" DataContext=").Append(dataContext.GetType().Name);
        }

        var bounds = visual.Bounds;
        sb.Append($" [{bounds.X:0},{bounds.Y:0} {bounds.Width:0}x{bounds.Height:0}]");
        if (!visual.IsVisible)
            sb.Append(" HIDDEN");

        switch (visual)
        {
            case TextBlock { Text.Length: > 0 } textBlock:
                sb.Append($" Text=\"{ObjectGraph.Truncate(textBlock.Text, 80)}\"");
                break;
            case TextBox { Text.Length: > 0 } textBox:
                sb.Append($" Text=\"{ObjectGraph.Truncate(textBox.Text, 80)}\"");
                break;
        }

        sb.AppendLine();

        if (depth >= maxDepth)
        {
            if (visual.GetVisualChildren().Any())
                sb.Append(' ', (depth + 1) * 2).AppendLine("…");
            return;
        }

        foreach (var child in visual.GetVisualChildren())
            AppendVisual(sb, child, depth + 1, maxDepth, dataContext, ref count);
    }

    private static async Task<JsonObject> ScreenshotAsync()
    {
        var (bytes, pixelSize) = await OnUiAsync(() =>
        {
            var window = Desktop?.MainWindow
                         ?? throw new InvalidOperationException("MainWindow is not created yet.");
            var scaling = window.RenderScaling;
            var size = new PixelSize(
                Math.Max(1, (int)Math.Ceiling(window.ClientSize.Width * scaling)),
                Math.Max(1, (int)Math.Ceiling(window.ClientSize.Height * scaling)));

            using var bitmap = new RenderTargetBitmap(size, new Vector(96 * scaling, 96 * scaling));
            bitmap.Render(window);
            using var stream = new MemoryStream();
            bitmap.Save(stream, PngBitmapEncoderOptions.Default);
            return (stream.ToArray(), size);
        });

        return new JsonObject
        {
            ["content"] = new JsonArray(
                new JsonObject { ["type"] = "text", ["text"] = $"Main window screenshot, {pixelSize.Width}x{pixelSize.Height}px." },
                new JsonObject
                {
                    ["type"] = "image",
                    ["data"] = Convert.ToBase64String(bytes),
                    ["mimeType"] = "image/png",
                }),
        };
    }

    private static async Task<JsonObject> AboutDialogProbeAsync()
    {
        var (passed, report) = await OnUiAsync(() =>
        {
            if (Desktop?.MainWindow is not Views.MainWindow main)
                return (false, "FAIL: MainWindow is not available.");

            var dialog = main.CreateAboutDialog();
            try
            {
                dialog.Show(main);
                var descendants = dialog.GetVisualDescendants().OfType<StyledElement>().ToArray();
                var versionBlock = descendants
                    .OfType<TextBlock>()
                    .FirstOrDefault(control => control.Name == "AboutVersionText");
                var version = versionBlock?.Text ?? "";
                var homepage = descendants
                    .OfType<SelectableTextBlock>()
                    .FirstOrDefault(control => control.Name == "AboutHomepageText")
                    ?.Text ?? "";
                var homepageButton = descendants
                    .OfType<Button>()
                    .FirstOrDefault(control => control.Name == "AboutHomepageButton");
                var closeButton = descendants
                    .OfType<Button>()
                    .FirstOrDefault(control =>
                        control.Content?.ToString() == Localizer.Get("DialogOk"));
                var title = dialog.Title ?? "";

                // Dialog chrome must share MainWindow's app palette (not Fluent defaults).
                var dialogBg = (dialog.Background as ISolidColorBrush)?.Color;
                var mainBg = (main.Background as ISolidColorBrush)?.Color;
                var versionFg = (versionBlock?.Foreground as ISolidColorBrush)?.Color;
                var closeBg = (closeButton?.Background as ISolidColorBrush)?.Color;
                var versionUsesHint = versionBlock?.Classes.Contains("hint") == true;
                var closeIsAccent = closeButton?.Classes.Contains("accent") == true;
                var contentOk = dialog.IsVisible
                         && title == Localizer.Get("About")
                         && version.Length > 0
                         && homepage == Views.MainWindow.ProjectHomepage
                         && homepageButton?.Content?.ToString() == Localizer.Get("ProjectHomepage");
                var themeOk = dialogBg is { } db
                              && mainBg is { } mb
                              && db == mb
                              && versionUsesHint
                              && closeIsAccent
                              && versionFg is not null
                              && closeBg is not null;
                var ok = contentOk && themeOk;

                return (ok,
                    $"{(ok ? "PASS" : "FAIL")}: About dialog\n"
                    + $"title: {title}\n"
                    + $"version: {version}\n"
                    + $"homepage: {homepage}\n"
                    + $"visible: {dialog.IsVisible}\n"
                    + $"theme.dialogBackground: {dialogBg}\n"
                    + $"theme.mainBackground: {mainBg}\n"
                    + $"theme.versionForeground: {versionFg}\n"
                    + $"theme.closeBackground: {closeBg}\n"
                    + $"theme.versionHintClass: {versionUsesHint}\n"
                    + $"theme.closeAccentClass: {closeIsAccent}");
            }
            finally
            {
                dialog.Close();
            }
        });

        return ToolText(report, isError: !passed);
    }

    #endregion

    #region App probe tools

    private static async Task<JsonObject> AiRuntimeSnapshotAsync()
    {
        var text = await OnUiAsync(() =>
        {
            if (Desktop?.MainWindow is not Views.MainWindow main)
                return "MainWindow is not available.";

            var tabs = main.FindControl<TabControl>("RightTabs");
            if (tabs is null)
                return "RightTabs not found.";

            var sb = new StringBuilder();
            var index = 0;
            var found = 0;
            foreach (var item in tabs.Items)
            {
                if (item is not TabItem { Content: TerminalView terminal })
                {
                    index++;
                    continue;
                }

                found++;
                var selected = ReferenceEquals(tabs.SelectedItem, item);
                var ai = terminal.AiViewModel;
                sb.AppendLine($"--- terminal tab[{index}] selected={selected} connected={terminal.IsTerminalConnected} ---");
                sb.AppendLine($"source={terminal.SourcePath}");
                sb.AppendLine($"sessionNumber={terminal.SessionNumber}");
                sb.AppendLine(
                    $"aiCommand exec={terminal.AiCommandExecutionCount} complete={terminal.AiCommandCompletionCount} "
                    + $"running={terminal.IsAiCommandRunning} lockAvailable={terminal.IsCommandLockAvailable} "
                    + $"payloadRunning={terminal.IsTerminalCommandRunning}");
                if (ai is null)
                {
                    sb.AppendLine("AiViewModel: null (panel not opened yet)");
                }
                else
                {
                    sb.AppendLine(
                        $"cliProvider={ai.SelectedProvider.Label} available={ai.SelectedProvider.IsAvailable} "
                        + $"running={ai.IsRunning} embedded={ai.HasEmbeddedSession} "
                        + $"runMode={ai.RunMode} hideSshTerminal={ai.HideSshTerminal} "
                        + $"installing={ai.IsInstalling} autoRun={ai.AutoRun} "
                        + $"autoApprove={ai.AutoApproveDangerousCommands}");
                    sb.AppendLine(
                        $"terminalVisible={terminal.IsTerminalAreaVisible} "
                        + $"sshTerminalHidden={terminal.IsSshTerminalHidden} "
                        + $"loginInputPending={terminal.IsLoginManualInputPending}");
                    sb.AppendLine($"status={ai.StatusText}");
                    sb.AppendLine($"workspace={ai.WorkingDirectory}");
                    sb.AppendLine($"mcpPipe={ProductMcpServer.PipeName}");
                    sb.AppendLine(
                        "dangerProbe="
                        + DangerousCommandDetector.IsDangerous("rm -rf /tmp/jrm-debug-probe"));
                    // Session attach state (TabControl unload/reload wiring).
                    sb.AppendLine($"outputStats={terminal.DebugAiOutputStats ?? "(n/a)"}");
                    sb.AppendLine($"headerHeight={terminal.DebugAiHeaderHeight?.ToString("0.#") ?? "(n/a)"}");
                }

                index++;
            }

            if (found == 0)
                sb.AppendLine("No TerminalView tabs are open.");
            return sb.ToString();
        });

        return ToolText(text);
    }

    private static async Task<JsonObject> TerminalTabTitleCheckAsync()
    {
        const string fullTitle = "production-cluster-singapore-api-node-末尾六个字符";
        var (passed, report) = await OnUiAsync(() =>
        {
            var title = Views.MainWindow.BuildTerminalTabTitle(fullTitle);
            title.Measure(new Size(180, 32));
            title.Arrange(new Rect(0, 0, 180, 32));

            var leading = title.Children.OfType<TextBlock>().ElementAtOrDefault(0);
            var trailing = title.Children.OfType<TextBlock>().ElementAtOrDefault(1);
            var parts = TerminalTabTitle.Split(fullTitle);

            var shortTitle = Views.MainWindow.BuildTerminalTabTitle("server");
            var shortParts = shortTitle.Children.OfType<TextBlock>().ToArray();

            const string similarTitleText = "production-api-node-02-singapore";
            var emphasis = TerminalTabTitle.FindEmphasis(
                similarTitleText,
                ["production-api-node-01-singapore", "production-api-node-03-singapore"]);
            var emphasizedTitle = Views.MainWindow.BuildTerminalTabTitle(similarTitleText, emphasis);
            emphasizedTitle.Measure(new Size(180, 32));
            emphasizedTitle.Arrange(new Rect(0, 0, 180, 32));
            var emphasizedParts = emphasizedTitle.Children
                .OfType<TextBlock>()
                .ToArray();
            var emphasizedContext = emphasizedParts
                .SingleOrDefault(part => part.Classes.Contains("tab-title-numeric-context"));
            var emphasizedDifference = emphasizedContext?
                .Inlines?
                .OfType<Run>()
                .SingleOrDefault(run => run.Classes.Contains("tab-title-emphasis"));

            const string numericIdentifier = "111111111111111111";
            var numericTitleText = $"production-api-node-{numericIdentifier}-singapore";
            var numericEmphasis = TerminalTabTitle.FindEmphasis(
                numericTitleText,
                [
                    "production-api-node-222222222222222222-singapore",
                    "production-api-node-333333333333333333-singapore",
                ]);
            var numericTitle = Views.MainWindow.BuildTerminalTabTitle(
                numericTitleText,
                numericEmphasis);
            numericTitle.Measure(new Size(180, 32));
            numericTitle.Arrange(new Rect(0, 0, 180, 32));
            var numericParts = numericTitle.Children.OfType<TextBlock>().ToArray();
            var numericContext = numericParts
                .SingleOrDefault(part => part.Classes.Contains("tab-title-numeric-context"));
            var numericDifference = numericContext?
                .Inlines?
                .OfType<Run>()
                .SingleOrDefault(run => run.Classes.Contains("tab-title-emphasis"));

            const string lastDigitTitleText =
                "very-long-production-server-name-12346-singapore";
            var lastDigitEmphasis = TerminalTabTitle.FindEmphasis(
                lastDigitTitleText,
                [
                    "very-long-production-server-name-12345-singapore",
                    "very-long-production-server-name-12347-singapore",
                ]);
            var lastDigitTitle = Views.MainWindow.BuildTerminalTabTitle(
                lastDigitTitleText,
                lastDigitEmphasis);
            lastDigitTitle.Measure(new Size(180, 32));
            lastDigitTitle.Arrange(new Rect(0, 0, 180, 32));
            var lastDigitContext = lastDigitTitle.Children
                .OfType<TextBlock>()
                .SingleOrDefault(part => part.Classes.Contains("tab-title-numeric-context"));
            var lastDigitDifference = lastDigitContext?
                .Inlines?
                .OfType<Run>()
                .SingleOrDefault(run => run.Classes.Contains("tab-title-emphasis"));

            var tooltip = ToolTip.GetTip(title)?.ToString() ?? "";
            var ok = leading?.Text == parts.LeadingText
                     && trailing?.Text == parts.TrailingText
                     && parts.LeadingText + parts.TrailingText == fullTitle
                     && parts.TrailingText == "末尾六个字符"
                     && tooltip == fullTitle
                     && title.MaxWidth == 180
                     && leading.TextTrimming == Avalonia.Media.TextTrimming.CharacterEllipsis
                     && trailing.Bounds.Width > 0
                     && trailing.Bounds.Right <= 180.01
                     && shortParts.Length == 2
                     && shortParts[0].Text == "server"
                     && shortParts[1].Text == ""
                     && !emphasis.IsEmpty
                     && similarTitleText.Substring(emphasis.Start, emphasis.Length) == "2"
                     && emphasizedParts.Length == 3
                     && emphasizedParts[0].Text == "production-api-node-"
                     && emphasizedContext?.Inlines?.Text == "02"
                     && emphasizedDifference?.Text == "2"
                     && emphasizedDifference.FontWeight == Avalonia.Media.FontWeight.Bold
                     && emphasizedParts[2].Text == "-singapore"
                     && emphasizedParts[2].TextTrimming == Avalonia.Media.TextTrimming.CharacterEllipsis
                     && numericTitleText.Substring(numericEmphasis.Start, numericEmphasis.Length)
                         == numericIdentifier
                     && numericContext?.Inlines?.Text == "1111"
                     && numericDifference?.Text == "1111"
                     && numericContext?.Inlines?.Text?.Count(char.IsDigit)
                         == TerminalTabTitle.NumericEmphasisMaxDigits
                     && numericContext.TextTrimming == Avalonia.Media.TextTrimming.None
                     && numericTitle.MaxWidth == 180
                     && lastDigitTitleText.Substring(
                         lastDigitEmphasis.Start,
                         lastDigitEmphasis.Length) == "6"
                     && lastDigitContext?.Inlines?.Text == "2346"
                     && lastDigitDifference?.Text == "6"
                     && lastDigitTitle.MaxWidth == 180;

            return (ok,
                $"{(ok ? "PASS" : "FAIL")}: terminal-tab long-name title\n"
                + $"leading: {leading?.Text}\n"
                + $"trailing: {trailing?.Text}\n"
                + $"tooltip: {tooltip}\n"
                + $"adjacent emphasis: {emphasizedDifference?.Text}\n"
                + $"similar layout: {string.Join(" | ", emphasizedParts.Select(
                    part => part.Text ?? part.Inlines?.Text ?? ""))}\n"
                + $"numeric emphasis: {numericContext?.Inlines?.Text} "
                + $"({TerminalTabTitle.NumericEmphasisMaxDigits}/{numericIdentifier.Length} digits)\n"
                + $"last-digit context: {lastDigitContext?.Inlines?.Text} "
                + $"(emphasis {lastDigitDifference?.Text})\n"
                + $"bounds: leading={leading?.Bounds}, trailing={trailing?.Bounds}\n"
                + $"maxWidth: {title.MaxWidth}");
        });

        return ToolText(report, isError: !passed);
    }

    private static async Task<JsonObject> TerminalTabFocusCheckAsync()
    {
        TabControl? tabs = null;
        object? originalSelection = null;
        TabItem? firstTab = null;
        TabItem? secondTab = null;
        TerminalView? firstView = null;
        TerminalView? secondView = null;

        try
        {
            await OnUiAsync(() =>
            {
                if (Desktop?.MainWindow is not Views.MainWindow main)
                    throw new InvalidOperationException("MainWindow is not available.");

                tabs = main.FindControl<TabControl>("RightTabs")
                       ?? throw new InvalidOperationException("RightTabs not found.");
                originalSelection = tabs.SelectedItem;
                firstView = new TerminalView();
                secondView = new TerminalView();
                firstView.DebugPrepareLoadedFocusCompetitor();
                firstTab = new TabItem { Header = "Focus probe A", Content = firstView };
                secondTab = new TabItem { Header = "Focus probe B", Content = secondView };
                tabs.Items.Add(firstTab);
                tabs.Items.Add(secondTab);
                tabs.SelectedItem = firstTab;
                return true;
            });

            await Task.Delay(75);
            var firstFocused = await OnUiAsync(() =>
            {
                firstView!.DebugFocusSecondaryTarget();
                return firstView.DebugCurrentFocusTarget;
            });

            await OnUiAsync(() =>
            {
                tabs!.SelectedItem = secondTab;
                return true;
            });
            await Task.Delay(75);
            var secondFocused = await OnUiAsync(() => secondView!.DebugCurrentFocusTarget);

            await OnUiAsync(() =>
            {
                tabs!.SelectedItem = firstTab;
                return true;
            });
            await Task.Delay(75);
            var restoredFocus = await OnUiAsync(() => firstView!.DebugCurrentFocusTarget);
            var rememberedFocus = await OnUiAsync(() => firstView!.DebugLastFocusTarget);

            var passed = firstFocused.EndsWith("#ScrollToBottomButton", StringComparison.Ordinal)
                         && secondFocused.EndsWith("#Term", StringComparison.Ordinal)
                         && restoredFocus == firstFocused
                         && rememberedFocus == firstFocused;
            return ToolText(
                $"{(passed ? "PASS" : "FAIL")}: terminal-tab focus is kept per tab in memory.\n"
                + $"first={firstFocused}\nsecond={secondFocused}\n"
                + $"restored={restoredFocus}\nremembered={rememberedFocus}",
                isError: !passed);
        }
        finally
        {
            if (tabs is not null)
            {
                await OnUiAsync(() =>
                {
                    if (originalSelection is not null && tabs.Items.Contains(originalSelection))
                        tabs.SelectedItem = originalSelection;
                    else if (tabs.Items.Count > 0)
                        tabs.SelectedIndex = 0;

                    if (firstTab is not null)
                        tabs.Items.Remove(firstTab);
                    if (secondTab is not null)
                        tabs.Items.Remove(secondTab);
                    firstView?.Close();
                    secondView?.Close();
                    return true;
                });
            }
        }
    }

    private static async Task<JsonObject> AiCliCtrlCCheckAsync()
    {
        TabControl? tabs = null;
        object? originalSelection = null;
        TabItem? probeTab = null;
        TerminalView? probeView = null;

        try
        {
            await OnUiAsync(() =>
            {
                if (Desktop?.MainWindow is not Views.MainWindow main)
                    throw new InvalidOperationException("MainWindow is not available.");

                tabs = main.FindControl<TabControl>("RightTabs")
                       ?? throw new InvalidOperationException("RightTabs not found.");
                originalSelection = tabs.SelectedItem;
                probeView = new TerminalView();
                probeView.DebugPrepareLoadedFocusCompetitor();
                probeTab = new TabItem { Header = "Ctrl+C probe", Content = probeView };
                tabs.Items.Add(probeTab);
                tabs.SelectedItem = probeTab;
                return true;
            });

            await Task.Delay(75);

            var (withSelection, withoutSelection) = await OnUiAsync(() =>
            {
                var panel = probeView!.DebugAiPanel;
                panel.DebugFeedCliText("jrm-ctrl-c-probe");
                return (panel.DebugPressCtrlCOnCli(selectVisibleText: true),
                        panel.DebugPressCtrlCOnCli(selectVisibleText: false));
            });

            // The terminal marks handled key events itself, so the outcome is judged
            // by what was copied and what reached the CLI input stream: Ctrl+C must
            // never send bytes (0x03 would interrupt the CLI), selection or not.
            var passed = withSelection.Contains("copiedText=jrm-ctrl-c-probe", StringComparison.Ordinal)
                         && withSelection.Contains("userInputHex=(none)", StringComparison.Ordinal)
                         && withoutSelection.Contains("copiedText=(none)", StringComparison.Ordinal)
                         && withoutSelection.Contains("userInputHex=(none)", StringComparison.Ordinal);
            return ToolText(
                $"{(passed ? "PASS" : "FAIL")}: AI CLI Ctrl+C copies the selection and never reaches the CLI.\n"
                + $"withSelection: {withSelection}\nwithoutSelection: {withoutSelection}",
                isError: !passed);
        }
        finally
        {
            if (tabs is not null)
            {
                await OnUiAsync(() =>
                {
                    if (originalSelection is not null && tabs.Items.Contains(originalSelection))
                        tabs.SelectedItem = originalSelection;
                    else if (tabs.Items.Count > 0)
                        tabs.SelectedIndex = 0;

                    if (probeTab is not null)
                        tabs.Items.Remove(probeTab);
                    probeView?.Close();
                    return true;
                });
            }
        }
    }

    private static TerminalView? _renderProbeView;
    private static TabItem? _renderProbeTab;

    /// <summary>
    /// Persistent AI panel probe for terminal-rendering bugs: "open" adds a local
    /// terminal tab with the AI panel started (embedded CLI, no SSH connection),
    /// "status" reports feed/scroll state plus the visible viewport text, and
    /// "close" removes the tab. The tab stays open across calls so long-running
    /// CLI sessions can be inspected with get_value / screenshot between calls.
    /// </summary>
    private static async Task<JsonObject> AiRenderProbeAsync(JsonObject args)
    {
        var action = args["action"]?.GetValue<string>() ?? "status";
        switch (action)
        {
            case "open":
                {
                    var text = await OnUiAsync(() =>
                    {
                        if (Desktop?.MainWindow is not Views.MainWindow main)
                            throw new InvalidOperationException("MainWindow is not available.");
                        if (_renderProbeView is not null)
                            return "already open";

                        var tabs = main.FindControl<TabControl>("RightTabs")
                                   ?? throw new InvalidOperationException("RightTabs not found.");
                        _renderProbeView = new TerminalView();
                        _renderProbeTab = new TabItem { Header = "AI render probe", Content = _renderProbeView };
                        tabs.Items.Add(_renderProbeTab);
                        tabs.SelectedItem = _renderProbeTab;
                        return "opened";
                    });
                    if (text == "opened")
                    {
                        // Let the tab load before opening the AI panel (auto-starts the CLI).
                        await Task.Delay(200);
                        await OnUiAsync(() =>
                        {
                            _renderProbeView!.ToggleAiPanel();
                            return true;
                        });
                    }

                    return ToolText(text);
                }

            case "close":
                return ToolText(await OnUiAsync(() =>
                {
                    if (Desktop?.MainWindow is not Views.MainWindow main || _renderProbeView is null)
                        return "not open";
                    var tabs = main.FindControl<TabControl>("RightTabs");
                    if (tabs is not null && _renderProbeTab is not null)
                        tabs.Items.Remove(_renderProbeTab);
                    _renderProbeView.Close();
                    _renderProbeView = null;
                    _renderProbeTab = null;
                    return "closed";
                }));

            case "hide":
                {
                    var closeTask = await OnUiAsync(() =>
                        _renderProbeView?.CloseAiPanelAsync());
                    if (closeTask is null)
                        return ToolText("not open");

                    await closeTask;
                    return ToolText(await OnUiAsync(() =>
                        $"panelOpen={_renderProbeView?.IsAiPanelOpen == true} "
                        + $"viewModelAttached={_renderProbeView?.AiViewModel is not null}"));
                }

            default:
                {
                    return ToolText(await OnUiAsync(() =>
                    {
                        if (_renderProbeView is null)
                            return "not open";
                        var panel = _renderProbeView.DebugAiPanel;
                        var vm = _renderProbeView.AiViewModel;
                        return $"provider={vm?.SelectedProvider.Label} running={vm?.IsRunning} "
                               + $"status={vm?.StatusText}\ncapture={vm?.CaptureFilePath ?? "(off)"}\n"
                               + $"stats: {panel.DebugOutputStats}\n--- visible ---\n{panel.DebugVisibleText}";
                    }));
                }
        }
    }

    /// <summary>
    /// Verifies the named-pipe transport from inside the app by connecting to its own pipe
    /// as an ordinary MCP client and running a handshake plus tools/list. Confirms the ACL
    /// lets this account in, the framing round-trips, and a second concurrent session is
    /// accepted while this one is open.
    /// </summary>
    private static async Task<JsonObject> McpTransportCheckAsync()
    {
        var pipeName = Host.PipeName;
        if (pipeName.Length == 0)
            return ToolText("FAIL: the pipe transport is not running (PipeName is empty).", isError: true);

        var report = new StringBuilder();
        report.AppendLine($"pipe: \\\\.\\pipe\\{pipeName}");
        report.AppendLine($"http: {(Host.Url.Length == 0 ? "(off)" : Host.Url)}");

        try
        {
            await using var first = await OpenPipeSessionAsync(pipeName).ConfigureAwait(false);
            var initialize = await first.CallAsync(
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}""")
                .ConfigureAwait(false);
            var toolList = await first.CallAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""")
                .ConfigureAwait(false);

            // A second client must be served while the first session is still open.
            await using var second = await OpenPipeSessionAsync(pipeName).ConfigureAwait(false);
            var ping = await second.CallAsync("""{"jsonrpc":"2.0","id":3,"method":"ping"}""").ConfigureAwait(false);

            var toolCount = JsonNode.Parse(toolList)?["result"]?["tools"] is JsonArray tools ? tools.Count : 0;
            var handshake = initialize.Contains("\"protocolVersion\"", StringComparison.Ordinal);
            var concurrent = ping.Contains("\"id\":3", StringComparison.Ordinal);

            report.AppendLine($"initialize: {(handshake ? "ok" : "FAIL")}");
            report.AppendLine($"tools/list: {toolCount} tools");
            report.AppendLine($"concurrent session: {(concurrent ? "ok" : "FAIL")}");

            var passed = handshake && toolCount > 0 && concurrent;
            return ToolText($"{(passed ? "PASS" : "FAIL")}: MCP pipe transport\n{report.ToString().TrimEnd()}",
                isError: !passed);
        }
        catch (Exception ex)
        {
            return ToolText($"FAIL: MCP pipe transport threw {ex.GetType().Name}: {ex.Message}\n{report}",
                isError: true);
        }
    }

    /// <summary>
    /// Drives the product MCP surface end to end over its own pipe, exactly as a user's agent
    /// would: create a connection, confirm passwords are write-only, set one, and check that
    /// in-session tools refuse clearly when nothing is open. Cleans up the connection it made.
    /// </summary>
    private static async Task<JsonObject> ProductMcpCheckAsync()
    {
        const string folder = "_mcp_selftest";
        const string connection = folder + "/probe";
        const string secret = "jrm-selftest-secret-2f4a";

        var pipeName = ProductMcpServer.PipeName;
        if (pipeName.Length == 0)
            return ToolText("FAIL: the product MCP server is not listening.", isError: true);

        var report = new StringBuilder();
        var failures = new List<string>();

        void Check(string name, bool ok)
        {
            report.AppendLine($"{(ok ? "ok  " : "FAIL")}: {name}");
            if (!ok)
                failures.Add(name);
        }

        try
        {
            var connectionsRoot = await OnUiAsync(() =>
                (Desktop?.MainWindow?.DataContext as ViewModels.MainWindowViewModel)?.RootPath ?? "");
            await using var session = await OpenPipeSessionAsync(pipeName).ConfigureAwait(false);

            var initialize = await session.CallAsync(
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}""")
                .ConfigureAwait(false);
            Check("initialize", initialize.Contains("\"jeek-remote-manager\"", StringComparison.Ordinal));

            var toolList = await session.CallAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""")
                .ConfigureAwait(false);
            var toolCount = JsonNode.Parse(toolList)?["result"]?["tools"] is JsonArray tools ? tools.Count : 0;
            Check("tools/list advertises the connection surface", toolCount >= 18);
            Check("tools/list advertises terminal-tab reordering",
                toolList.Contains("\"session_move\"", StringComparison.Ordinal)
                && toolList.Contains("\"position\"", StringComparison.Ordinal));
            Check("debug tools stay off the product surface", !toolList.Contains("\"get_value\"", StringComparison.Ordinal));

            var created = ExtractToolText(await session.CallAsync(ToolCall(3, "connection_create", new JsonObject
            {
                ["name"] = "probe",
                ["folder"] = folder,
                ["type"] = "ssh",
                ["host"] = "127.0.0.1",
                ["username"] = "selftest",
                ["notes"] = "debug probe",
            })).ConfigureAwait(false));
            Check("connection_create writes the connection", created.Contains("\"created\": true", StringComparison.Ordinal));

            var beforeSecret = await CallConnectionGetAsync(session, 4, connection).ConfigureAwait(false);
            Check("new connection has no password", beforeSecret.Contains("\"hasPassword\": false", StringComparison.Ordinal));

            var stored = ExtractToolText(await session.CallAsync(ToolCall(5, "connection_set_password", new JsonObject
            {
                ["connection"] = connection,
                ["mode"] = "value",
                ["value"] = secret,
            })).ConfigureAwait(false));
            Check("connection_set_password stores the value", stored.Contains("\"status\": \"saved\"", StringComparison.Ordinal));
            Check("set_password never echoes the secret", !stored.Contains(secret, StringComparison.Ordinal));

            var afterSecret = await CallConnectionGetAsync(session, 6, connection).ConfigureAwait(false);
            Check("connection_get reports hasPassword", afterSecret.Contains("\"hasPassword\": true", StringComparison.Ordinal));
            Check("connection_get never returns the secret", !afterSecret.Contains(secret, StringComparison.Ordinal));
            Check("connection_get never returns the encrypted blob",
                !afterSecret.Contains("EncryptedPassword", StringComparison.OrdinalIgnoreCase)
                && !afterSecret.Contains("jrm1", StringComparison.OrdinalIgnoreCase));

            var updated = ExtractToolText(await session.CallAsync(ToolCall(20, "connection_update", new JsonObject
            {
                ["connection"] = connection,
                ["host"] = "10.0.0.9",
                ["notes"] = "updated by probe",
            })).ConfigureAwait(false));
            Check("connection_update writes only the fields it was given",
                updated.Contains("\"host\": \"10.0.0.9\"", StringComparison.Ordinal)
                && updated.Contains("updated by probe", StringComparison.Ordinal)
                && updated.Contains("\"username\": \"selftest\"", StringComparison.Ordinal));
            Check("connection_update keeps the stored password", updated.Contains("\"hasPassword\": true", StringComparison.Ordinal));

            var moved = ExtractToolText(await session.CallAsync(ToolCall(21, "connection_move", new JsonObject
            {
                ["connection"] = connection,
                ["folder"] = folder + "/nested",
            })).ConfigureAwait(false));
            Check("connection_move relocates it in the tree",
                moved.Contains($"\"connection\": \"{folder}/nested/probe\"", StringComparison.Ordinal));

            var movedBack = ExtractToolText(await session.CallAsync(ToolCall(22, "connection_move", new JsonObject
            {
                ["connection"] = folder + "/nested/probe",
                ["folder"] = folder,
            })).ConfigureAwait(false));
            Check("connection_move accepts the new path afterwards",
                movedBack.Contains($"\"connection\": \"{connection}\"", StringComparison.Ordinal));

            var folderCreated = ExtractToolText(await session.CallAsync(ToolCall(23, "folder_create", new JsonObject
            {
                ["folder"] = folder + "/made-by-probe",
            })).ConfigureAwait(false));
            Check("folder_create adds a tree folder",
                folderCreated.Contains("\"status\": \"created\"", StringComparison.Ordinal)
                && Directory.Exists(Path.Combine(
                    connectionsRoot, folder, "made-by-probe")));

            var scripts = ExtractToolText(await session.CallAsync(ToolCall(24, "script_list", new JsonObject()))
                .ConfigureAwait(false));
            var scriptsNode = JsonNode.Parse(scripts);
            var suites = scriptsNode?["suites"] as JsonArray ?? [];
            Check("script_list returns suites with their scripts and parameters",
                suites.Count > 0
                && suites.Any(suite => suite?["scripts"] is JsonArray { Count: > 0 })
                && suites.All(suite => suite?["suite"] is not null && suite["parameters"] is JsonArray));
            Check("script_list returns no stored parameter values",
                suites.All(suite => (suite?["parameters"] as JsonArray ?? [])
                    .All(parameter => parameter?["type"]?.GetValue<string>() != "Secret"
                                      || (parameter["default"]?.GetValue<string>() ?? "").Length == 0)));

            var badSuite = ExtractToolText(await session.CallAsync(ToolCall(25, "script_run", new JsonObject
            {
                ["connection"] = connection,
                ["suite"] = "no-such-suite",
                ["script"] = "nope",
            })).ConfigureAwait(false));
            Check("script_run rejects an unknown suite and points at script_list",
                badSuite.Contains("script_list", StringComparison.Ordinal));

            var batch = ExtractToolText(await session.CallAsync(ToolCall(26, "script_run_batch", new JsonObject
            {
                ["connections"] = new JsonArray(connection, "nope/missing"),
                ["suite"] = "Demo",
                ["script"] = "print-all.sh",
                ["open_missing"] = false,
            })).ConfigureAwait(false));
            Check("script_run_batch reports one result per connection",
                JsonNode.Parse(batch)?["results"] is JsonArray { Count: 2 });
            Check("script_run_batch keeps going after a failed connection",
                batch.Contains("\"total\": 2", StringComparison.Ordinal)
                && batch.Contains("has no open session", StringComparison.Ordinal));

            var commandBatch = ExtractToolText(await session.CallAsync(ToolCall(
                32,
                "terminal_run_batch",
                new JsonObject
                {
                    ["connections"] = new JsonArray(connection, "nope/missing"),
                    ["command"] = "echo global-agent-probe",
                    ["open_missing"] = false,
                    ["max_parallel"] = 2,
                })).ConfigureAwait(false));
            Check("terminal_run_batch reports one result per connection",
                JsonNode.Parse(commandBatch)?["results"] is JsonArray { Count: 2 });
            Check("terminal_run_batch keeps going after a failed connection",
                commandBatch.Contains("\"total\": 2", StringComparison.Ordinal)
                && commandBatch.Contains("\"status\": \"error\"", StringComparison.Ordinal)
                && commandBatch.Contains("has no open session", StringComparison.Ordinal));

            var keyMissing = ExtractToolText(await session.CallAsync(ToolCall(27, "public_key_install", new JsonObject
            {
                ["connection"] = connection,
                ["public_key_path"] = "Z:\no-such-key.pub",
            })).ConfigureAwait(false));
            Check("public_key_install rejects a missing key file",
                keyMissing.Contains("No public key file", StringComparison.Ordinal));

            var folderMoved = ExtractToolText(await session.CallAsync(ToolCall(28, "folder_move", new JsonObject
            {
                ["folder"] = folder + "/made-by-probe",
                ["name"] = "renamed-by-probe",
            })).ConfigureAwait(false));
            Check("folder_move renames a folder in place",
                folderMoved.Contains("renamed-by-probe", StringComparison.Ordinal)
                && Directory.Exists(Path.Combine(connectionsRoot, folder, "renamed-by-probe")));

            var importBadSource = ExtractToolText(await session.CallAsync(ToolCall(29, "connections_import", new JsonObject
            {
                ["source"] = "putty",
                ["path"] = connectionsRoot,
            })).ConfigureAwait(false));
            Check("connections_import names the sources it supports",
                importBadSource.Contains("xshell", StringComparison.Ordinal)
                && importBadSource.Contains("finalshell", StringComparison.Ordinal));

            var hosts = ExtractToolText(await session.CallAsync(ToolCall(30, "known_hosts_list", new JsonObject()))
                .ConfigureAwait(false));
            Check("known_hosts_list returns the trusted fingerprints",
                JsonNode.Parse(hosts)?["hosts"] is JsonArray);

            var forget = ExtractToolText(await session.CallAsync(ToolCall(31, "known_hosts_forget", new JsonObject
            {
                ["host"] = "no-such-host.invalid",
                ["port"] = 22,
            })).ConfigureAwait(false));
            Check("known_hosts_forget reports an unknown host instead of failing",
                forget.Contains("not_stored", StringComparison.Ordinal));

            var noSession = ExtractToolText(await session.CallAsync(ToolCall(7, "terminal_run", new JsonObject
            {
                ["session"] = "nope/none",
                ["command"] = "echo hi",
            })).ConfigureAwait(false));
            Check("in-session tools refuse clearly without a session",
                noSession.Contains("session_list", StringComparison.Ordinal)
                || noSession.Contains("session_open", StringComparison.Ordinal));

            var prompt = ExtractToolText(await session.CallAsync(ToolCall(8, "connection_set_password", new JsonObject
            {
                ["connection"] = connection,
                ["mode"] = "prompt",
            })).ConfigureAwait(false));
            Check("prompt mode hands the secret entry to the GUI",
                prompt.Contains("awaiting_user", StringComparison.Ordinal));

            // Session lifecycle against 127.0.0.1, which has no sshd here: the tab opens and
            // stays un-live, which is exactly the addressing path we need to exercise without
            // touching one of the user's real servers.
            var opened = ExtractToolText(await session.CallAsync(ToolCall(9, "session_open", new JsonObject
            {
                ["connection"] = connection,
                ["activate"] = false,
                ["wait_seconds"] = 2,
            })).ConfigureAwait(false));
            Check("session_open returns the tree-path session id",
                opened.Contains($"\"session\": \"{connection}\"", StringComparison.Ordinal));
            Check("session_open reports a status", opened.Contains("\"status\"", StringComparison.Ordinal));

            var listed = ExtractToolText(await session.CallAsync(ToolCall(10, "session_list", new JsonObject()))
                .ConfigureAwait(false));
            Check("session_list shows the new session", listed.Contains(connection, StringComparison.Ordinal));

            var openedSecond = ExtractToolText(await session.CallAsync(ToolCall(32, "session_open", new JsonObject
            {
                ["connection"] = connection,
                ["activate"] = false,
                ["wait_seconds"] = 1,
            })).ConfigureAwait(false));
            var secondSession = connection + " (2)";
            Check("session_open creates an addressable second tab",
                openedSecond.Contains($"\"session\": \"{secondSession}\"", StringComparison.Ordinal));

            var sessionMoved = ExtractToolText(await session.CallAsync(ToolCall(33, "session_move", new JsonObject
            {
                ["session"] = secondSession,
                ["position"] = 0,
            })).ConfigureAwait(false));
            var movedNode = JsonNode.Parse(sessionMoved);
            Check("session_move returns the new terminal-tab order",
                movedNode?["position"]?.GetValue<int>() == 0
                && movedNode?["sessions"] is JsonArray movedSessions
                && movedSessions.FirstOrDefault()?.GetValue<string>() == secondSession);

            var listedAfterMove = ExtractToolText(
                await session.CallAsync(ToolCall(34, "session_list", new JsonObject()))
                    .ConfigureAwait(false));
            Check("session_list reflects the moved tab",
                JsonNode.Parse(listedAfterMove)?["sessions"] is JsonArray reorderedSessions
                && reorderedSessions.FirstOrDefault()?["session"]?.GetValue<string>() == secondSession);

            var invalidMove = ExtractToolText(await session.CallAsync(ToolCall(35, "session_move", new JsonObject
            {
                ["session"] = secondSession,
                ["position"] = 999,
            })).ConfigureAwait(false));
            Check("session_move rejects an out-of-range position",
                invalidMove.Contains("must be between", StringComparison.Ordinal));

            var status = ExtractToolText(await session.CallAsync(ToolCall(11, "terminal_status", new JsonObject
            {
                ["session"] = connection,
            })).ConfigureAwait(false));
            Check("in-session tools resolve by session id", status.Length > 0
                && !status.Contains("No open session", StringComparison.Ordinal));

            var byConnection = ExtractToolText(await session.CallAsync(ToolCall(12, "terminal_status", new JsonObject
            {
                ["connection"] = connection,
            })).ConfigureAwait(false));
            Check("in-session tools resolve by connection path",
                !byConnection.Contains("has no open session", StringComparison.Ordinal));

            var closed = ExtractToolText(await session.CallAsync(ToolCall(13, "session_close", new JsonObject
            {
                ["session"] = connection,
            })).ConfigureAwait(false));
            Check("session_close closes the tab", closed.Contains("Closed session", StringComparison.Ordinal));

            var closedSecond = ExtractToolText(await session.CallAsync(ToolCall(36, "session_close", new JsonObject
            {
                ["session"] = secondSession,
            })).ConfigureAwait(false));
            Check("session_close closes the reordered tab",
                closedSecond.Contains("Closed session", StringComparison.Ordinal));

            var afterClose = ExtractToolText(await session.CallAsync(ToolCall(14, "session_list", new JsonObject()))
                .ConfigureAwait(false));
            Check("session_list drops the closed session", !afterClose.Contains(connection, StringComparison.Ordinal));

            var passed = failures.Count == 0;
            return ToolText($"{(passed ? "PASS" : "FAIL")}: product MCP surface\n{report.ToString().TrimEnd()}",
                isError: !passed);
        }
        catch (Exception ex)
        {
            return ToolText($"FAIL: product MCP surface threw {ex.GetType().Name}: {ex.Message}\n{report}",
                isError: true);
        }
        finally
        {
            await CleanupSelfTestConnectionAsync(folder).ConfigureAwait(false);
        }
    }

    private static async Task<string> CallConnectionGetAsync(PipeProbeSession session, int id, string connection) =>
        ExtractToolText(await session
            .CallAsync(ToolCall(id, "connection_get", new JsonObject { ["connection"] = connection }))
            .ConfigureAwait(false));

    /// <summary>
    /// Unwraps a tools/call reply to the text an agent would read. Assertions must run on
    /// this, not the raw line, where every quote of the payload is backslash-escaped.
    /// </summary>
    private static string ExtractToolText(string rawResponse)
    {
        if (JsonNode.Parse(rawResponse)?["result"]?["content"] is not JsonArray content)
            return rawResponse;

        return string.Join(
            "\n",
            content.Select(item => item?["text"]?.GetValue<string>()).Where(text => text is not null));
    }

    /// <summary>Builds one JSON-RPC tools/call line for the probe sessions.</summary>
    private static string ToolCall(int id, string name, JsonObject arguments) =>
        new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = name, ["arguments"] = arguments },
        }.ToJsonString();

    private static async Task CleanupSelfTestConnectionAsync(string folder)
    {
        try
        {
            await OnUiAsync(() =>
            {
                if (Desktop?.MainWindow is not Views.MainWindow main
                    || main.DataContext is not ViewModels.MainWindowViewModel vm)
                {
                    return false;
                }

                var path = Path.Combine(vm.RootPath, folder);
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
                vm.ReloadTreeFromDisk();
                return true;
            });
        }
        catch (Exception ex)
        {
            Log.ZLogWarning($"Could not clean up the product MCP self-test connection: {ex.Message}");
        }
    }

    private static async Task<PipeProbeSession> OpenPipeSessionAsync(string pipeName)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(3000).ConfigureAwait(false);
        return new PipeProbeSession(pipe);
    }

    /// <summary>One client-side pipe session used by <c>mcp_transport_check</c>.</summary>
    private sealed class PipeProbeSession : IAsyncDisposable
    {
        private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

        private readonly NamedPipeClientStream _pipe;
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;

        public PipeProbeSession(NamedPipeClientStream pipe)
        {
            _pipe = pipe;
            _reader = new StreamReader(pipe, Utf8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            _writer = new StreamWriter(pipe, Utf8, leaveOpen: true) { AutoFlush = true };
        }

        public async Task<string> CallAsync(string request)
        {
            await _writer.WriteLineAsync(request).ConfigureAwait(false);
            return await _reader.ReadLineAsync().ConfigureAwait(false)
                   ?? throw new IOException("The pipe closed before replying.");
        }

        public async ValueTask DisposeAsync()
        {
            _reader.Dispose();
            await _writer.DisposeAsync().ConfigureAwait(false);
            await _pipe.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// End-to-end check of <see cref="AgentProjectLink"/> against a throwaway project folder:
    /// link (merging into pre-existing agent files), refresh with a new endpoint (no duplicate
    /// blocks, URL rotated), then unlink (project content restored). With <c>panel: true</c> it
    /// also drives the live AI panel view model from the open ai_render_probe tab.
    /// </summary>
    private static async Task<JsonObject> AgentProjectLinkCheckAsync(JsonObject args)
    {
        var keep = args["keep"]?.GetValue<bool>() ?? false;
        var usePanel = args["panel"]?.GetValue<bool>() ?? false;

        var root = Path.Combine(Path.GetTempPath(), "jrm-link-check-" + Guid.NewGuid().ToString("N")[..8]);
        var project = Path.Combine(root, "my-project");
        var workspace = Path.Combine(root, "workspace");
        const string server = "jrm-remote-vps-bwg";

        var report = new StringBuilder();
        var failures = new List<string>();

        void Check(string name, bool ok)
        {
            report.AppendLine($"{(ok ? "ok  " : "FAIL")}: {name}");
            if (!ok)
                failures.Add(name);
        }

        static int CountOccurrences(string text, string needle)
        {
            var count = 0;
            var index = 0;
            while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += needle.Length;
            }

            return count;
        }

        try
        {
            Directory.CreateDirectory(project);
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(Path.Combine(project, ".codex"));

            // Seed the project the way a real repository looks before linking.
            File.WriteAllText(Path.Combine(project, "AGENTS.md"), "# My project\n\nProject rules stay here.\n");
            File.WriteAllText(
                Path.Combine(project, ".mcp.json"),
                "{\n  \"mcpServers\": {\n    \"other\": { \"type\": \"http\", \"url\": \"http://example/other\" }\n  }\n}\n");
            File.WriteAllText(Path.Combine(project, ".codex", "config.toml"), "model = \"gpt-5\"\n");

            var link = new AgentWorkspaceLink(
                workspace, "vps/bwg", "vps/bwg", "bwg", "SSH", "root@10.0.0.1:22", McpToolsAutoApprove: true);
            Check("MCP server name is per-connection", link.ProjectMcpServerName == server);

            AgentProjectLink.WriteInto(link, project);

            var agentsMd = File.ReadAllText(Path.Combine(project, "AGENTS.md"));
            var codexToml = File.ReadAllText(Path.Combine(project, ".codex", "config.toml"));
            var grokToml = File.ReadAllText(Path.Combine(project, ".grok", "config.toml"));
            var mcpJson = File.ReadAllText(Path.Combine(project, ".mcp.json"));

            Check("AGENTS.md keeps the project's own text", agentsMd.Contains("Project rules stay here.", StringComparison.Ordinal));
            Check("AGENTS.md gains the reference block", agentsMd.Contains("BEGIN JeekRemoteManager link: vps/bwg", StringComparison.Ordinal));
            Check("reference block names the MCP server", agentsMd.Contains(server, StringComparison.Ordinal));
            Check("reference block points at the workspace doc", agentsMd.Contains(Path.Combine(workspace, "AGENTS.md"), StringComparison.Ordinal));
            foreach (var include in AgentMcpConfigCatalog.ContextIncludeFiles)
            {
                var includePath = Path.Combine(project, include);
                Check(
                    $"{include} includes AGENTS.md",
                    File.Exists(includePath)
                    && File.ReadAllText(includePath).Contains(
                        AgentMcpConfigCatalog.ContextIncludeBody,
                        StringComparison.Ordinal));
            }
            Check(".mcp.json keeps the project's own server", mcpJson.Contains("\"other\"", StringComparison.Ordinal));
            Check(".mcp.json gains this connection as a stdio adapter launch",
                mcpJson.Contains(server, StringComparison.Ordinal)
                && mcpJson.Contains("\"stdio\"", StringComparison.Ordinal)
                && mcpJson.Contains("JeekRemoteManagerMcp.exe", StringComparison.Ordinal)
                && mcpJson.Contains("--connection", StringComparison.Ordinal));
            Check(".mcp.json entry carries no URL, port, or token",
                JsonNode.Parse(mcpJson)?["mcpServers"]?[server] is JsonObject entry
                && entry["url"] is null);
            Check(".codex/config.toml keeps existing keys", codexToml.Contains("model = \"gpt-5\"", StringComparison.Ordinal));
            Check(".codex/config.toml gains the server table",
                codexToml.Contains($"[mcp_servers.{server}]", StringComparison.Ordinal)
                && codexToml.Contains("JeekRemoteManagerMcp.exe", StringComparison.Ordinal)
                && codexToml.Contains("--connection", StringComparison.Ordinal));
            Check(".codex approval mode follows auto-run", codexToml.Contains("default_tools_approval_mode = \"approve\"", StringComparison.Ordinal));
            Check(".grok/config.toml gains the server table",
                grokToml.Contains($"[mcp_servers.{server}]", StringComparison.Ordinal)
                && grokToml.Contains("JeekRemoteManagerMcp.exe", StringComparison.Ordinal));

            // Every catalog config must be written, and each JSON one under the root key its
            // agent actually reads — VS Code ignores "mcpServers" without reporting anything.
            foreach (var target in AgentMcpConfigCatalog.All)
            {
                var targetPath = target.ResolvePath(project);
                if (target.Format != AgentMcpConfigCatalog.ConfigFormat.Json)
                {
                    Check($"{target.RelativePath} written", File.Exists(targetPath));
                    continue;
                }

                Check(
                    $"{target.RelativePath} holds the entry under \"{target.JsonRootKey}\"",
                    File.Exists(targetPath)
                    && TryParseJsonObject(File.ReadAllText(targetPath))
                        ?[target.JsonRootKey!]?[server] is JsonObject);
            }
            // Writing again must replace the block in place, not append a second copy.
            AgentProjectLink.WriteInto(link with { Target = "root@10.0.0.2:22" }, project);

            agentsMd = File.ReadAllText(Path.Combine(project, "AGENTS.md"));
            codexToml = File.ReadAllText(Path.Combine(project, ".codex", "config.toml"));
            mcpJson = File.ReadAllText(Path.Combine(project, ".mcp.json"));

            Check("rewriting does not duplicate the markdown block", CountOccurrences(agentsMd, "BEGIN JeekRemoteManager link: vps/bwg") == 1);
            Check("rewriting does not duplicate the TOML block", CountOccurrences(codexToml, $"[mcp_servers.{server}]") == 1);
            Check("rewriting updates the block from the connection",
                agentsMd.Contains("root@10.0.0.2:22", StringComparison.Ordinal));
            Check("rewriting keeps the project's own server", mcpJson.Contains("\"other\"", StringComparison.Ordinal));

            AgentProjectLink.RemoveFrom(link, project);

            agentsMd = File.ReadAllText(Path.Combine(project, "AGENTS.md"));
            codexToml = File.ReadAllText(Path.Combine(project, ".codex", "config.toml"));
            mcpJson = File.ReadAllText(Path.Combine(project, ".mcp.json"));

            Check("removal takes the markdown block out", !agentsMd.Contains("JeekRemoteManager link", StringComparison.Ordinal));
            Check("removal keeps the project's own text", agentsMd.Contains("Project rules stay here.", StringComparison.Ordinal));
            Check("removal takes the TOML block out", !codexToml.Contains(server, StringComparison.Ordinal) && codexToml.Contains("model = \"gpt-5\"", StringComparison.Ordinal));
            Check("removal takes the MCP entry out", !mcpJson.Contains(server, StringComparison.Ordinal) && mcpJson.Contains("\"other\"", StringComparison.Ordinal));
            Check(
                "removal deletes the files it created",
                AgentMcpConfigCatalog.ContextIncludeFiles.All(
                    include => !File.Exists(Path.Combine(project, include)))
                && !Directory.Exists(Path.Combine(project, ".grok")));
            // Configs we created outright go, folder and all; ones we merged into keep
            // whatever the project already had.
            foreach (var target in AgentMcpConfigCatalog.All)
            {
                var targetPath = target.ResolvePath(project);
                var remaining = File.Exists(targetPath) ? File.ReadAllText(targetPath) : "";
                Check(
                    $"removal takes this connection out of {target.RelativePath}",
                    !remaining.Contains(server, StringComparison.Ordinal));
                if (target is { HasOwnFolder: true, Format: AgentMcpConfigCatalog.ConfigFormat.Json })
                {
                    Check(
                        $"removal drops the folder we created for {target.RelativePath}",
                        !Directory.Exists(Path.GetDirectoryName(targetPath)!));
                }
            }

            if (usePanel)
            {
                var panelProject = Path.Combine(root, "panel-project");
                Directory.CreateDirectory(panelProject);
                var panelResult = await OnUiAsync(() =>
                {
                    if (_renderProbeView?.AiViewModel is not { } vm)
                        return "probe tab is not open (run ai_render_probe with action=open first)";

                    var written = vm.WriteToProject(panelProject);
                    var block = File.Exists(Path.Combine(panelProject, "AGENTS.md"))
                                && File.ReadAllText(Path.Combine(panelProject, "AGENTS.md"))
                                    .Contains("JeekRemoteManager link", StringComparison.Ordinal);
                    vm.RemoveFromProject(panelProject);
                    var cleared = !File.Exists(Path.Combine(panelProject, "AGENTS.md"));
                    return $"written={written} block={block} cleared={cleared} status={vm.StatusText}";
                });

                report.AppendLine($"panel: {panelResult}");
                Check(
                    "AI panel writes and removes through the view model",
                    panelResult.Contains("written=True", StringComparison.Ordinal)
                    && panelResult.Contains("block=True", StringComparison.Ordinal)
                    && panelResult.Contains("cleared=True", StringComparison.Ordinal));
            }

            // Leave a written sample behind so the generated wording can be reviewed by hand.
            if (keep)
                AgentProjectLink.WriteInto(link, project);

            var passed = failures.Count == 0;
            return ToolText(
                $"{(passed ? "PASS" : "FAIL")}: agent project link ({project})\n{report.ToString().TrimEnd()}",
                isError: !passed);
        }
        catch (Exception ex)
        {
            return ToolText($"FAIL: agent project link threw {ex.GetType().Name}: {ex.Message}\n{report}", isError: true);
        }
        finally
        {
            if (!keep)
            {
                try { Directory.Delete(root, recursive: true); }
                catch { /* best-effort cleanup */ }
            }
        }
    }

    /// <summary>
    /// Exercises the public methods used by the main-menu application-wide MCP actions, bypassing
    /// only the native folder picker so the generated files can be inspected deterministically.
    /// </summary>
    private static async Task<JsonObject> AgentApplicationLinkCheckAsync(JsonObject args)
    {
        var keep = args["keep"]?.GetValue<bool>() ?? false;
        var project = Path.Combine(
            Path.GetTempPath(),
            "jrm-application-link-check-" + Guid.NewGuid().ToString("N")[..8]);
        var report = new StringBuilder();
        var failures = new List<string>();

        void Check(string name, bool ok)
        {
            report.AppendLine($"{(ok ? "ok  " : "FAIL")}: {name}");
            if (!ok)
                failures.Add(name);
        }

        try
        {
            Directory.CreateDirectory(project);
            Directory.CreateDirectory(Path.Combine(project, ".codex"));
            File.WriteAllText(Path.Combine(project, "AGENTS.md"), "# Existing rules\n");
            File.WriteAllText(
                Path.Combine(project, ".mcp.json"),
                "{ \"mcpServers\": { \"other\": { \"url\": \"http://example/other\" } } }");
            File.WriteAllText(
                Path.Combine(project, ".codex", "config.toml"),
                "model = \"gpt-5\"\n");

            var (menuHeaders, trayHeaders) = await OnUiAsync(() =>
            {
                if (Desktop?.MainWindow is not Views.MainWindow main)
                    throw new InvalidOperationException("The main window is not available.");
                main.WriteApplicationMcpToProject(project);
                var tray = (Application.Current as App)?.TrayMenuHeaders.ToArray() ?? [];
                return (main.MoreActionsMenuHeaders.ToArray(), tray);
            });

            var linkLabel = Localizer.Get("AiLinkApplicationProject");
            var unlinkLabel = Localizer.Get("AiUnlinkApplicationProject");
            Check("main menu exposes application-wide link", menuHeaders.Contains(linkLabel));
            Check("main menu exposes application-wide unlink", menuHeaders.Contains(unlinkLabel));
            Check("tray menu exposes application-wide link", trayHeaders.Contains(linkLabel));
            Check("tray menu exposes application-wide unlink", trayHeaders.Contains(unlinkLabel));

            // Shared application actions (everything after tray-only "Show") must match.
            var sharedFromTray = trayHeaders
                .SkipWhile(h => h == Localizer.Get("TrayShow"))
                .ToArray();
            Check(
                "tray and main menus share the same application actions",
                sharedFromTray.SequenceEqual(menuHeaders));

            var agentsMd = File.ReadAllText(Path.Combine(project, "AGENTS.md"));
            var root = TryParseJsonObject(File.ReadAllText(Path.Combine(project, ".mcp.json")));
            var entry = root?["mcpServers"]?[AgentProjectLink.ApplicationMcpServerName] as JsonObject;
            var codex = File.ReadAllText(Path.Combine(project, ".codex", "config.toml"));
            var instanceId = AgentWorkspaceLink.AdapterInstanceId;
            var jsonRouteOk = instanceId is null
                ? entry?["args"] is null
                : entry?["args"] is JsonArray routeArgs
                  && routeArgs.Select(node => node?.GetValue<string>())
                      .SequenceEqual(["--instance", instanceId]);
            var codexRouteOk = instanceId is null
                ? !codex.Contains("--instance", StringComparison.Ordinal)
                : codex.Contains(
                    $"args = [\"--instance\", \"{instanceId}\"]",
                    StringComparison.Ordinal);

            Check(
                "AGENTS.md describes global application control",
                agentsMd.Contains("BEGIN JeekRemoteManager link: application", StringComparison.Ordinal)
                && agentsMd.Contains("connection_list", StringComparison.Ordinal)
                && agentsMd.Contains("whole application", StringComparison.Ordinal));
            Check(
                "JSON config launches the fixed adapter with only the required instance route",
                entry?["command"]?.GetValue<string>() == McpAdapterRegistry.AdapterPath
                && jsonRouteOk
                && entry["url"] is null);
            Check(
                "Codex config is application-wide",
                codex.Contains(
                    $"[mcp_servers.{AgentProjectLink.ApplicationMcpServerName}]",
                    StringComparison.Ordinal)
                && !codex.Contains("--connection", StringComparison.Ordinal)
                && codexRouteOk);
            Check(
                "fixed adapter and current instance registration exist",
                File.Exists(McpAdapterRegistry.AdapterPath)
                && McpAdapterRegistration.IsCurrentInstanceRegistered());
            Check(
                "existing project configuration is preserved",
                root?["mcpServers"]?["other"] is not null
                && codex.Contains("model = \"gpt-5\"", StringComparison.Ordinal)
                && agentsMd.Contains("Existing rules", StringComparison.Ordinal));

            await OnUiAsync(() =>
            {
                if (Desktop?.MainWindow is not Views.MainWindow main)
                    throw new InvalidOperationException("The main window is not available.");
                main.RemoveApplicationMcpFromProject(project);
                return true;
            });

            agentsMd = File.ReadAllText(Path.Combine(project, "AGENTS.md"));
            root = TryParseJsonObject(File.ReadAllText(Path.Combine(project, ".mcp.json")));
            codex = File.ReadAllText(Path.Combine(project, ".codex", "config.toml"));
            Check(
                "unlink removes only JeekRemoteManager's application entry",
                !agentsMd.Contains("JeekRemoteManager link: application", StringComparison.Ordinal)
                && root?["mcpServers"]?[AgentProjectLink.ApplicationMcpServerName] is null
                && root?["mcpServers"]?["other"] is not null
                && !codex.Contains(AgentProjectLink.ApplicationMcpServerName, StringComparison.Ordinal)
                && codex.Contains("model = \"gpt-5\"", StringComparison.Ordinal));

            if (keep)
            {
                await OnUiAsync(() =>
                {
                    ((Views.MainWindow)Desktop!.MainWindow!).WriteApplicationMcpToProject(project);
                    return true;
                });
            }

            var passed = failures.Count == 0;
            return ToolText(
                $"{(passed ? "PASS" : "FAIL")}: application-wide project MCP link ({project})\n"
                + report.ToString().TrimEnd(),
                isError: !passed);
        }
        catch (Exception ex)
        {
            return ToolText(
                $"FAIL: application-wide project MCP link threw {ex.GetType().Name}: {ex.Message}\n{report}",
                isError: true);
        }
        finally
        {
            if (!keep)
            {
                try { Directory.Delete(project, recursive: true); }
                catch { /* best-effort cleanup */ }
            }
        }
    }

    private static async Task<JsonObject> GlobalAgentCheckAsync()
    {
        var report = new StringBuilder();
        var failures = new List<string>();

        void Check(string name, bool ok)
        {
            report.AppendLine($"{(ok ? "ok  " : "FAIL")}: {name}");
            if (!ok)
                failures.Add(name);
        }

        try
        {
            var initial = await OnUiAsync(() =>
            {
                if (Desktop?.MainWindow is not Views.MainWindow main)
                    throw new InvalidOperationException("The main window is not available.");

                return (
                    IsOpen: main.IsGlobalAgentTabOpen,
                    IsActive: main.IsGlobalAgentTabActive,
                    SelectedTab: main.SelectedRightTab);
            }).ConfigureAwait(false);

            Check("global AI Agent tab is closed by default", !initial.IsOpen && !initial.IsActive);
            if (initial.IsOpen)
            {
                return ToolText(
                    $"FAIL: global AI Agent lifecycle\n{report.ToString().TrimEnd()}",
                    isError: true);
            }

            var firstOpen = await OnUiAsync(() =>
            {
                if (Desktop?.MainWindow is not Views.MainWindow main)
                    throw new InvalidOperationException("The main window is not available.");

                var vm = main.PrepareGlobalAgentTabForDebug();
                return (
                    ViewModel: vm,
                    Workspace: vm.WorkingDirectory,
                    Count: vm.Providers.Count,
                    vm.ShowConnectionOptions,
                    IsOpen: main.IsGlobalAgentTabOpen,
                    IsActive: main.IsGlobalAgentTabActive);
            }).ConfigureAwait(false);

            Check(
                "global AI Agent tab opens on demand without activating the probe",
                firstOpen.IsOpen && !firstOpen.IsActive);

            var firstCloseTask = await OnUiAsync(() =>
            {
                if (Desktop?.MainWindow is not Views.MainWindow main)
                    throw new InvalidOperationException("The main window is not available.");
                return main.CloseGlobalAgentAsync();
            }).ConfigureAwait(false);
            await firstCloseTask.ConfigureAwait(false);

            var closed = await OnUiAsync(() =>
            {
                if (Desktop?.MainWindow is not Views.MainWindow main)
                    throw new InvalidOperationException("The main window is not available.");
                return (
                    IsOpen: main.IsGlobalAgentTabOpen,
                    HasViewModel: main.HasGlobalAgentViewModel,
                    IsActive: main.IsGlobalAgentTabActive);
            }).ConfigureAwait(false);

            Check(
                "closing the global AI Agent removes the tab and releases its view model",
                !closed.IsOpen && !closed.HasViewModel && !closed.IsActive);

            var secondOpen = await OnUiAsync(() =>
            {
                if (Desktop?.MainWindow is not Views.MainWindow main)
                    throw new InvalidOperationException("The main window is not available.");

                var vm = main.PrepareGlobalAgentTabForDebug();
                return (
                    ViewModel: vm,
                    IsOpen: main.IsGlobalAgentTabOpen,
                    IsActive: main.IsGlobalAgentTabActive);
            }).ConfigureAwait(false);

            Check(
                "reopening the global AI Agent creates a fresh view model",
                secondOpen.IsOpen
                && !secondOpen.IsActive
                && !ReferenceEquals(firstOpen.ViewModel, secondOpen.ViewModel));

            var secondCloseTask = await OnUiAsync(() =>
            {
                if (Desktop?.MainWindow is not Views.MainWindow main)
                    throw new InvalidOperationException("The main window is not available.");
                return main.CloseGlobalAgentAsync();
            }).ConfigureAwait(false);
            await secondCloseTask.ConfigureAwait(false);

            var final = await OnUiAsync(() =>
            {
                if (Desktop?.MainWindow is not Views.MainWindow main)
                    throw new InvalidOperationException("The main window is not available.");
                return (
                    IsOpen: main.IsGlobalAgentTabOpen,
                    HasViewModel: main.HasGlobalAgentViewModel,
                    IsActive: main.IsGlobalAgentTabActive,
                    SelectedTab: main.SelectedRightTab);
            }).ConfigureAwait(false);

            Check(
                "lifecycle probe restores the default closed state and selected tab",
                !final.IsOpen
                && !final.HasViewModel
                && !final.IsActive
                && ReferenceEquals(initial.SelectedTab, final.SelectedTab));

            var workspace = firstOpen.Workspace;
            var agentsPath = Path.Combine(workspace, "AGENTS.md");
            var jsonPath = Path.Combine(workspace, ".mcp.json");
            var codexPath = Path.Combine(workspace, ".codex", "config.toml");
            var agentsMd = File.Exists(agentsPath) ? File.ReadAllText(agentsPath) : "";
            var json = File.Exists(jsonPath)
                ? TryParseJsonObject(File.ReadAllText(jsonPath))
                : null;
            var entry = json?["mcpServers"]?[AgentProjectLink.ApplicationMcpServerName] as JsonObject;
            var codex = File.Exists(codexPath) ? File.ReadAllText(codexPath) : "";
            var productToolNames = ProductMcpContract.BuildToolList()
                .Select(tool => tool?["name"]?.GetValue<string>() ?? "")
                .ToHashSet(StringComparer.Ordinal);

            Check(
                "workspace uses the reserved application directory",
                Path.GetFileName(workspace) == AgentCliWorkspace.ApplicationWorkspaceFolderName);
            Check(
                "workspace documents application-wide control",
                agentsMd.Contains("whole application", StringComparison.Ordinal)
                && agentsMd.Contains("connection_list", StringComparison.Ordinal));
            Check(
                "JSON MCP config launches the product adapter without a connection pin",
                entry?["command"]?.GetValue<string>() == McpAdapterRegistry.AdapterPath
                && entry["url"] is null
                && !(entry["args"]?.ToJsonString() ?? "")
                    .Contains("--connection", StringComparison.Ordinal));
            Check(
                "Codex MCP config is application-wide",
                codex.Contains(
                    $"[mcp_servers.{AgentProjectLink.ApplicationMcpServerName}]",
                    StringComparison.Ordinal)
                && !codex.Contains("--connection", StringComparison.Ordinal));
            Check("agent providers are available to the global panel", firstOpen.Count > 0);
            Check("connection-only panel options are hidden", !firstOpen.ShowConnectionOptions);
            Check(
                "product MCP advertises safe and dangerous batch command tools",
                productToolNames.Contains("terminal_run_batch")
                && productToolNames.Contains("terminal_run_batch_danger"));

            var passed = failures.Count == 0;
            return ToolText(
                $"{(passed ? "PASS" : "FAIL")}: global AI Agent ({workspace})\n"
                + report.ToString().TrimEnd(),
                isError: !passed);
        }
        catch (Exception ex)
        {
            return ToolText(
                $"FAIL: global AI Agent check threw {ex.GetType().Name}: {ex.Message}\n{report}",
                isError: true);
        }
    }

    /// <summary>
    /// Verifies the agent-discovery cache both saves the repeated probe and cannot go
    /// stale: an agent installed outside the app has to appear without a restart.
    /// </summary>
    private static async Task<JsonObject> AgentDiscoveryCacheCheckAsync()
    {
        var report = new StringBuilder();
        var passed = true;

        void Expect(bool condition, string label)
        {
            passed &= condition;
            report.Append(condition ? "  ok   " : "  FAIL ").Append(label).Append('\n');
        }

        // Probe() builds a fresh list every time, so reference equality tells us whether
        // a call was answered from the cache.
        var first = AgentCliCatalog.Discover();
        var second = AgentCliCatalog.Discover();
        Expect(ReferenceEquals(first, second), "a second Discover inside the TTL reuses the probe");

        var forced = AgentCliCatalog.Rediscover();
        Expect(!ReferenceEquals(second, forced), "Rediscover always re-probes");

        await Task.Delay(TimeSpan.FromSeconds(6));
        var afterTtl = AgentCliCatalog.Discover();
        Expect(!ReferenceEquals(forced, afterTtl), "Discover re-probes once the TTL has elapsed");
        Expect(afterTtl.Count == first.Count, "the refreshed result still lists every agent");

        return ToolText(
            $"{(passed ? "PASS" : "FAIL")}: agent discovery cache saves the repeat probe without going stale.\n{report}",
            isError: !passed);
    }

    private static Task<JsonObject> AgentCliLocateCheckAsync(JsonObject args)
    {
        var sb = new StringBuilder();
        // Drive off the panel's own provider list so a newly added agent cannot be missing here,
        // and report every surface plus its missing-state action. An agent can have its CLI
        // installed but not its desktop app or IDE.
        // Rediscover, not Discover: this check exists to report what is actually on disk
        // right now, so it must not be answered from the process-lifetime cache.
        foreach (var descriptor in AgentCliCatalog.Rediscover())
        {
            var surfaceKinds = AgentCliCatalog.RunModesFor(descriptor.Kind)
                .Select(AgentCliCatalog.SurfaceKindFor)
                .Distinct()
                .ToList();

            foreach (var kind in surfaceKinds)
            {
                var surface = descriptor.Surfaces.GetValueOrDefault(kind);
                var label = surfaceKinds.Count > 1 ? $"{descriptor.Label} [{kind}]" : descriptor.Label;
                var availability = surface switch
                {
                    null => "(surface missing from catalog)",
                    { ExecutablePath: { Length: > 0 } executablePath } => executablePath,
                    { IsAvailableWithoutExecutable: true } => "available via official web launcher",
                    { CanAutoInstall: true } =>
                        DescribeAgentInstaller(descriptor.Kind, kind, surface.InstallHint),
                    _ => $"not found — download page: {surface.InstallHint}",
                };
                sb.AppendLine($"{label}: {availability}");
            }
        }
        if (args["path"]?.GetValue<string>() is { Length: > 0 } path)
            sb.AppendLine($"resolve: {path} -> {AgentCliLocator.ResolveRealPath(path)}");
        return Task.FromResult(ToolText(sb.ToString().TrimEnd()));
    }

    private static string DescribeAgentInstaller(
        AgentCliKind agentKind,
        AgentSurfaceKind surfaceKind,
        string command)
    {
        var startInfo = AgentCliInstaller.CreateInstallProcessStartInfo(agentKind, surfaceKind);
        var isVisibleExternalConsole =
            startInfo.UseShellExecute
            && !startInfo.CreateNoWindow
            && !startInfo.RedirectStandardOutput
            && !startInfo.RedirectStandardError
            && startInfo.WindowStyle == ProcessWindowStyle.Normal;
        return isVisibleExternalConsole
            ? $"not found — external console install command: {command}"
            : $"not found — INVALID hidden installer configuration: {command}";
    }

    private static async Task<JsonObject> AgentCliMcpConfigCheckAsync(JsonObject args)
    {
        var connection = args["connection"]?.GetValue<string>()?.Replace('\\', '/').Trim('/')
                         ?? "vps/bwg";
        if (connection.Length == 0)
            connection = "vps/bwg";

        var root = Path.GetFullPath(AgentCliWorkspace.RootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var workspace = Path.GetFullPath(Path.Combine(
            root,
            connection.Replace('/', Path.DirectorySeparatorChar)));
        if (!workspace.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return ToolText("FAIL: connection escapes the agent workspace root.", isError: true);

        var adapter = AgentWorkspaceLink.AdapterPath;
        var connectionsRoot = await OnUiAsync(() =>
            ResolveRoot("MainVm") is MainWindowViewModel vm ? vm.RootPath : "");
        var sourcePath = connectionsRoot.Length == 0
            ? ""
            : Path.GetFullPath(Path.Combine(
                connectionsRoot,
                connection.Replace('/', Path.DirectorySeparatorChar) + ConnectionStore.FileExtension));
        if (sourcePath.Length > 0
            && sourcePath.StartsWith(
                Path.GetFullPath(connectionsRoot).TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)
            && File.Exists(sourcePath))
        {
            var model = new ConnectionStore(connectionsRoot).Load(sourcePath);
            workspace = AgentCliWorkspace.Ensure(connectionsRoot, sourcePath, model);
        }
        else
        {
            AgentCliWorkspace.WriteProjectMcpConfigs(workspace, connection);
        }

        var agentsPath = Path.Combine(workspace, "AGENTS.md");
        var claudeSettingsPath = Path.Combine(workspace, ".claude", "settings.local.json");

        var agents = File.Exists(agentsPath) ? File.ReadAllText(agentsPath) : "";
        var claudeApproved = false;
        if (File.Exists(claudeSettingsPath))
        {
            try
            {
                claudeApproved = JsonNode.Parse(File.ReadAllText(claudeSettingsPath))
                    ?["enabledMcpjsonServers"] is JsonArray enabled
                    && enabled.Any(node =>
                        string.Equals(
                            node?.GetValue<string>(),
                            AgentCliWorkspace.McpServerName,
                            StringComparison.Ordinal));
            }
            catch (JsonException)
            {
                // Report the invalid settings as a failed check below.
            }
        }

        var cursorPermissionsPath = Path.Combine(workspace, ".cursor", "cli.json");
        var cursorAllowed =
            File.Exists(cursorPermissionsPath)
            && TryParseJsonObject(File.ReadAllText(cursorPermissionsPath))
                ?["permissions"]?["allow"] is JsonArray cursorAllow
            && cursorAllow.Any(node => string.Equals(
                node?.GetValue<string>(),
                $"Mcp({AgentCliWorkspace.McpServerName}:*)",
                StringComparison.Ordinal));

        var expectedArguments = new List<string>();
        if (AgentWorkspaceLink.AdapterInstanceId is { } instanceId)
        {
            expectedArguments.Add("--instance");
            expectedArguments.Add(instanceId);
        }
        expectedArguments.Add("--connection");
        expectedArguments.Add(connection);
        static string EscapeTomlValue(string value) =>
            value.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal);
        var escapedAdapter = EscapeTomlValue(adapter);
        var expectedTomlArgs = "args = ["
                               + string.Join(
                                   ", ",
                                   expectedArguments.Select(value => $"\"{EscapeTomlValue(value)}\""))
                               + "]";
        var checks = new List<(string Name, bool Ok)>
        {
            ("adapter", File.Exists(adapter)),
            ("Pi MCP extension", File.Exists(Path.Combine(
                AppContext.BaseDirectory,
                "Data",
                "AgentSupport",
                "Pi",
                "jrm-mcp.ts"))),
            ("instance registration", McpAdapterRegistration.IsCurrentInstanceRegistered()),
            ("AGENTS.md", agents.Contains($"**Adapter:** `{adapter}`", StringComparison.Ordinal)
                          && agents.Contains(
                              $"**Pinned connection:** `{connection}`",
                              StringComparison.Ordinal)),
            ("Claude approval", claudeApproved),
            ("Cursor CLI permission", cursorAllowed),
        };

        // Every config in the catalog must exist, launch the adapter, and be pinned to this
        // connection. JSON is checked structurally — the entry must sit under the root key
        // that agent reads, and indented output puts the args on separate lines.
        foreach (var target in AgentMcpConfigCatalog.All)
        {
            var path = target.ResolvePath(workspace);
            var text = File.Exists(path) ? File.ReadAllText(path) : "";
            var jsonRoot = target.Format == AgentMcpConfigCatalog.ConfigFormat.Json
                ? TryParseJsonObject(text)
                : null;
            var entry = jsonRoot?[target.JsonRootKey!]?[AgentCliWorkspace.McpServerName] as JsonObject;
            var ok = target.Format == AgentMcpConfigCatalog.ConfigFormat.Json
                ? target.JsonStyle switch
                {
                    AgentMcpConfigCatalog.JsonEntryStyle.OpenCodeLocal =>
                        entry?["type"]?.GetValue<string>() == "local"
                        && entry["command"] is JsonArray command
                        && command.Select(node => node?.GetValue<string>())
                            .SequenceEqual(new[] { adapter }.Concat(expectedArguments))
                        && entry["enabled"]?.GetValue<bool>() == true
                        && jsonRoot?["permission"]?[$"{AgentCliWorkspace.McpServerName}_*"]
                            ?.GetValue<string>() == "allow",
                    // Zed picks the transport by shape, so a "type" key would not be schema-valid,
                    // and it approves per tool because it has no per-server wildcard.
                    AgentMcpConfigCatalog.JsonEntryStyle.ZedContextServer =>
                        entry?["command"]?.GetValue<string>() == adapter
                        && entry["type"] is null
                        && entry["args"] is JsonArray zedArgs
                        && zedArgs.Select(node => node?.GetValue<string>())
                            .SequenceEqual(expectedArguments)
                        && AgentCliCatalog.AutoRunSafeToolNames.All(tool =>
                            jsonRoot?["agent"]?["tool_permissions"]?["tools"]?[
                                    AgentMcpConfigCatalog.ZedToolKey(
                                        AgentCliWorkspace.McpServerName,
                                        tool)]
                                ?["default"]?.GetValue<string>() == "allow"),
                    _ => entry?["command"]?.GetValue<string>() == adapter
                         && entry["args"] is JsonArray entryArgs
                         && entryArgs.Select(node => node?.GetValue<string>())
                             .SequenceEqual(expectedArguments),
                }
                : text.Contains(escapedAdapter, StringComparison.Ordinal)
                  && text.Contains(expectedTomlArgs, StringComparison.Ordinal);

            checks.Add((target.RelativePath, ok));
            // AGENTS.md must tell the agent which file to look at, or the workspace is
            // silently missing a surface the user thinks is supported.
            checks.Add((
                $"AGENTS.md lists {target.RelativePath}",
                agents.Contains($"`{target.RelativePath}`", StringComparison.Ordinal)));
        }

        // Agents that do not read AGENTS.md by name need their own one-line include.
        foreach (var include in AgentMcpConfigCatalog.ContextIncludeFiles)
        {
            var path = Path.Combine(workspace, include);
            checks.Add((
                include,
                File.Exists(path)
                && File.ReadAllText(path).Trim() == AgentMcpConfigCatalog.ContextIncludeBody));
        }
        var passed = checks.All(check => check.Item2);
        var report = new StringBuilder()
            .AppendLine($"{(passed ? "PASS" : "FAIL")}: AI CLI MCP config")
            .AppendLine($"workspace={workspace}")
            .AppendLine($"connectionFile={(File.Exists(sourcePath) ? sourcePath : "(not found)")}")
            .AppendLine($"adapter={adapter}")
            .AppendLine($"instance={AgentWorkspaceLink.AdapterInstanceId ?? "release (default)"}");
        foreach (var (name, ok) in checks)
            report.AppendLine($"{name}={(ok ? "ok" : "FAIL")}");

        return ToolText(report.ToString().TrimEnd(), isError: !passed);
    }

    /// <summary>Parses a config file for probing, treating malformed JSON as a failed check.</summary>
    private static JsonObject? TryParseJsonObject(string text)
    {
        try
        {
            return JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static TerminalView? _menuProbeView;
    private static TabItem? _menuProbeTab;

    // A local cmd.exe shell stands in for the bastion: one command prints the whole
    // menu, then "#select" must type the number of the named entry (3 here).
    private const string MenuProbeLoginCommands =
        "echo    1: 10.11.13.128   ai-lab-10.11.13.128"
        + "& echo    3: 10.11.13.42    mecha-linux-build-10.11.13.42"
        + "& echo    8: 10.11.66.134   test-box-66.134"
        + "& echo Please select a target:\n"
        + "#select mecha-linux-build";

    // The paged scenario stands in for a bastion menu that needs Ctrl-F to page:
    // the wanted entry is only on page 2, so "#select" must press the key first.
    private const string MenuProbePagedLoginCommands =
        "#pagekey Ctrl-F\n#select oa-test";

    private const string MenuProbePagerScript = """
        $page1 = "  35: 120.92.154.189   ksc-cm-prd-it-server01`n  36: 120.92.154.86    ksc-cm-prd-it-server02`n  37: 172.18.251.142   mcp-server`n-- 51 records total. Ctrl-F: next page --"
        $page2 = "  38: 120.92.138.81    oa-120.92.138.81`n  39: 172.18.251.107   oa-test-172.18.251.107`n-- 51 records total. Ctrl-F: next page --"
        $page = 1
        $typed = ''
        Write-Host $page1
        while ($true) {
            $key = [Console]::ReadKey($true)
            if ([int]$key.KeyChar -eq 6) {
                if ($page -lt 2) { $page++ }
                if ($page -eq 1) { Write-Host $page1 } else { Write-Host $page2 }
                continue
            }
            if ($key.Key -eq 'Enter') {
                Write-Host "SELECTED=$typed"
                $typed = ''
                continue
            }
            $typed += $key.KeyChar
        }
        """;

    private const string MenuProbeSwitchLoginCommands =
        "#reuse-enter\n#select all-assets\n#duplicate\n#reuse-leave\nexit\n#key Enter";

    // Models a target shell that needs "exit" plus one more Enter before returning.
    // The menu is deliberately slower than the normal 500 ms quiet threshold, which
    // catches a phase transition that accidentally accepts the old target output.
    private const string MenuProbeSwitchScript = """
        Write-Host "TARGET_A_READY"
        $reuseLeaveInput = [Console]::ReadLine()
        Write-Host "REUSE_LEAVE=$reuseLeaveInput"
        $confirm = [Console]::ReadLine()
        Write-Host "CONFIRM=ENTER"
        Start-Sleep -Milliseconds 1200
        Write-Host "  7: 10.0.0.7   all-assets"
        Write-Host "Please select a target:"
        $selected = [Console]::ReadLine()
        Write-Host "SWITCH_SELECTED=$selected"
        while ($true) { Start-Sleep -Seconds 1 }
        """;

    private static (
        string ExePath,
        IReadOnlyList<string> Arguments,
        string LoginCommands,
        IReadOnlyList<string[]>? LoginPhases) BuildMenuProbeShell(
        string scenario)
    {
        if (scenario == "switch")
        {
            var switchScriptPath = Path.Combine(
                DebugInstanceContext.Info.RuntimeTempRoot,
                "login-menu-switch.ps1");
            Directory.CreateDirectory(Path.GetDirectoryName(switchScriptPath)!);
            File.WriteAllText(switchScriptPath, MenuProbeSwitchScript);
            return (
                "powershell.exe",
                ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", switchScriptPath],
                MenuProbeSwitchLoginCommands,
                [
                    LoginCommandSequence.Select(
                        MenuProbeSwitchLoginCommands,
                        LoginCommandSection.ReuseLeave),
                    LoginCommandSequence.Select(
                        MenuProbeSwitchLoginCommands,
                        LoginCommandSection.ReuseEnter),
                ]);
        }

        if (scenario != "paged")
            return (
                Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                [],
                MenuProbeLoginCommands,
                null);

        var scriptPath = Path.Combine(DebugInstanceContext.Info.RuntimeTempRoot, "login-menu-pager.ps1");
        Directory.CreateDirectory(Path.GetDirectoryName(scriptPath)!);
        File.WriteAllText(scriptPath, MenuProbePagerScript);
        return (
            "powershell.exe",
            ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath],
            MenuProbePagedLoginCommands,
            null);
    }

    /// <summary>
    /// End-to-end probe for the "#select &lt;name&gt;" login directive without a bastion:
    /// "open" adds a terminal tab attached to a local cmd.exe shell whose login commands
    /// print a numbered menu and then select an entry by name, "status" returns the
    /// scrollback (the typed number is visible in it), and "close" removes the tab.
    /// </summary>
    private static async Task<JsonObject> LoginMenuSelectProbeAsync(JsonObject args)
    {
        var action = args["action"]?.GetValue<string>() ?? "status";
        switch (action)
        {
            case "open":
                {
                    var view = await OnUiAsync(() =>
                    {
                        if (Desktop?.MainWindow is not Views.MainWindow main)
                            throw new InvalidOperationException("MainWindow is not available.");
                        if (_menuProbeView is not null)
                            return null;

                        var tabs = main.FindControl<TabControl>("RightTabs")
                                   ?? throw new InvalidOperationException("RightTabs not found.");
                        _menuProbeView = new TerminalView();
                        _menuProbeTab = new TabItem { Header = "Login menu probe", Content = _menuProbeView };
                        tabs.Items.Add(_menuProbeTab);
                        tabs.SelectedItem = _menuProbeTab;
                        return _menuProbeView;
                    });
                    if (view is null)
                        return ToolText("already open");

                    var shell = BuildMenuProbeShell(args["scenario"]?.GetValue<string>() ?? "single");
                    var loginCommands = args["login_commands"]?.GetValue<string>() is { Length: > 0 } custom
                        ? custom
                        : shell.LoginCommands;
                    // Let the tab lay out so the terminal has a real size before the PTY starts.
                    await Task.Delay(200);
                    await OnUiAsync(() => view.DebugStartLocalShellAsync(
                        new Models.Connection { Name = "login menu probe", LoginCommands = loginCommands },
                        shell.ExePath,
                        shell.Arguments,
                        shell.LoginPhases));
                    return ToolText("opened");
                }

            case "close":
                return ToolText(await OnUiAsync(() =>
                {
                    if (Desktop?.MainWindow is not Views.MainWindow main || _menuProbeView is null)
                        return "not open";
                    var tabs = main.FindControl<TabControl>("RightTabs");
                    if (tabs is not null && _menuProbeTab is not null)
                        tabs.Items.Remove(_menuProbeTab);
                    _menuProbeView.Close();
                    _menuProbeView = null;
                    _menuProbeTab = null;
                    return "closed";
                }));

            default:
                {
                    return ToolText(await OnUiAsync(() =>
                        _menuProbeView is null
                            ? "not open"
                            : $"state={_menuProbeView.LoginSequenceState}\n--- visible ---\n"
                              + _menuProbeView.DebugVisibleTerminalText));
                }
        }
    }

    private static Task<JsonObject> LoginMenuSelectCheckAsync(JsonObject args)
    {
        var menu = args["menu"]?.GetValue<string>() ?? "";
        var name = args["name"]?.GetValue<string>() ?? "";
        var keyword = LoginCommandSequence.TryGetMenuSelectKeyword(name) ?? name;

        var sb = new StringBuilder();
        sb.AppendLine("entries:");
        foreach (var entry in LoginMenuSelection.ParseEntries(menu))
            sb.AppendLine($"  {entry.Choice} -> {entry.Label}");

        var result = LoginMenuSelection.Resolve(menu, keyword);
        sb.AppendLine(result.Success
            ? $"match: {keyword} -> types \"{result.Choice}\" ({result.MatchedLabel})"
            : $"no match: {result.Failure}");
        return Task.FromResult(ToolText(sb.ToString().TrimEnd()));
    }

    /// <summary>
    /// Spins up a real <see cref="LoginCommandsTextBox"/> on a temporary tab and checks
    /// that # prefix filtering, popup presentation, and accept-to-insert work.
    /// </summary>
    private static async Task<JsonObject> LoginCommandCompletionCheckAsync()
    {
        TabControl? tabs = null;
        object? originalSelection = null;
        TabItem? probeTab = null;
        LoginCommandsTextBox? editor = null;

        try
        {
            await OnUiAsync(() =>
            {
                if (Desktop?.MainWindow is not MainWindow main)
                    throw new InvalidOperationException("MainWindow is not available.");

                tabs = main.FindControl<TabControl>("RightTabs")
                       ?? throw new InvalidOperationException("RightTabs not found.");
                originalSelection = tabs.SelectedItem;
                editor = new LoginCommandsTextBox
                {
                    Name = "LoginCommandCompletionProbeEditor",
                    AcceptsReturn = true,
                    MinHeight = 120,
                    Margin = new Thickness(24),
                };
                probeTab = new TabItem
                {
                    Header = "Login completion probe",
                    Content = editor,
                };
                tabs.Items.Add(probeTab);
                tabs.SelectedItem = probeTab;
                return true;
            });

            await Task.Delay(75);
            await OnUiAsync(() =>
            {
                var box = editor
                          ?? throw new InvalidOperationException("Completion probe editor missing.");
                box.Focus();
                box.Text = "#re";
                box.CaretIndex = box.Text.Length;
                return true;
            });
            // TextChanged defers open to Input priority so the caret is final first.
            await Task.Delay(100);

            var reuse = await OnUiAsync(() =>
            {
                var box = editor!;
                var snapshot = (
                    hasTemplate: box.Template is not null,
                    open: box.IsDirectiveCompletionOpen,
                    overlay: box.IsDirectiveCompletionUsingOverlayLayer,
                    background: box.HasDirectiveCompletionBackground,
                    bounds: box.DirectiveCompletionBounds,
                    renderedItems: box.DirectiveCompletionRenderedItemCount,
                    renderedItemDetails: box.DirectiveCompletionRenderedItems,
                    items: box.DirectiveCompletionItems);
                var accepted = box.AcceptDirectiveCompletion();
                return (
                    snapshot.hasTemplate,
                    snapshot.open,
                    closedAfterAccept: !box.IsDirectiveCompletionOpen,
                    snapshot.overlay,
                    snapshot.background,
                    snapshot.bounds,
                    snapshot.renderedItems,
                    snapshot.renderedItemDetails,
                    snapshot.items,
                    accepted,
                    text: box.Text);
            });

            await OnUiAsync(() =>
            {
                editor!.Text = "  #P";
                editor.CaretIndex = editor.Text.Length;
                return true;
            });
            await Task.Delay(100);

            var pageKey = await OnUiAsync(() =>
            {
                var openBefore = editor!.IsDirectiveCompletionOpen;
                var items = editor.DirectiveCompletionItems;
                var accepted = editor.AcceptDirectiveCompletion();
                return (
                    open: openBefore,
                    closedAfterAccept: !editor.IsDirectiveCompletionOpen,
                    items: items,
                    accepted: accepted,
                    text: editor.Text);
            });

            // Caret-only moves must not open the popup on an existing # line.
            await OnUiAsync(() =>
            {
                var box = editor!;
                box.Text = "#input\nother";
                box.CaretIndex = (box.Text ?? "").Length;
                return true;
            });
            await Task.Delay(100);
            var caretOnly = await OnUiAsync(() =>
            {
                var box = editor!;
                // Move onto the #input token without typing.
                box.CaretIndex = 3;
                return box.IsDirectiveCompletionOpen;
            });

            var passed = reuse.hasTemplate
                         && reuse.open
                         && reuse.closedAfterAccept
                         && reuse.overlay
                         && reuse.background
                         && reuse.bounds.Width > 0
                         && reuse.bounds.Height > 0
                         && reuse.renderedItems == reuse.items.Length
                         && reuse.items.SequenceEqual(["#reuse-enter", "#reuse-leave"])
                         && reuse.accepted
                         && reuse.text == "#reuse-enter"
                         && pageKey.open
                         && pageKey.closedAfterAccept
                         && pageKey.items.SequenceEqual(["#pagekey <key>"])
                         && pageKey.accepted
                         && pageKey.text == "  #pagekey "
                         && !caretOnly;

            return ToolText(
                $"{(passed ? "PASS" : "FAIL")}: login-command # marker completion\n"
                + $"reuse: template={reuse.hasTemplate} open={reuse.open} closedAfter={reuse.closedAfterAccept} "
                + $"overlay={reuse.overlay} background={reuse.background} bounds={reuse.bounds} "
                + $"renderedItems={reuse.renderedItems} [{reuse.renderedItemDetails}] "
                + $"items=[{string.Join(", ", reuse.items)}] accepted={reuse.accepted} text=\"{reuse.text}\"\n"
                + $"pagekey: open={pageKey.open} closedAfter={pageKey.closedAfterAccept} "
                + $"items=[{string.Join(", ", pageKey.items)}] "
                + $"accepted={pageKey.accepted} text=\"{pageKey.text}\"\n"
                + $"caretOnlyOpen={caretOnly}",
                isError: !passed);
        }
        finally
        {
            if (tabs is not null)
            {
                await OnUiAsync(() =>
                {
                    if (editor is not null)
                        editor.Text = "";

                    if (originalSelection is not null && tabs.Items.Contains(originalSelection))
                        tabs.SelectedItem = originalSelection;
                    else if (tabs.Items.Count > 0)
                        tabs.SelectedIndex = 0;

                    if (probeTab is not null)
                        tabs.Items.Remove(probeTab);
                    return true;
                });
            }
        }
    }

    private static Task<JsonObject> LoginCommandFlowCheckAsync(JsonObject args)
    {
        var commands = args["login_commands"]?.GetValue<string>()
                       ?? "#input\n#reuse-enter\n5\n#key Enter\n#duplicate\n#reuse-leave\nexit";
        var key = args["key"]?.GetValue<string>() ?? "Enter";
        var sb = new StringBuilder();
        sb.AppendLine($"structured={LoginCommandSequence.HasStructuredReuseWorkflow(commands)}");
        sb.AppendLine(LoginCommandSequence.BuildPreview(commands));
        var validation = LoginCommandSequence.Validate(commands);
        sb.AppendLine(validation.Count == 0
            ? "validation: ok"
            : "validation:\n  " + string.Join("\n  ", validation));
        if (LoginKeySequence.TryParse(key, out var sequence, out var error))
        {
            sb.AppendLine(
                $"key {key}: {Convert.ToHexString(Encoding.UTF8.GetBytes(sequence))}");
        }
        else
        {
            sb.AppendLine($"key {key}: error: {error}");
        }

        return Task.FromResult(ToolText(sb.ToString().TrimEnd()));
    }

    private static Task<JsonObject> BastionLoginTemplateCheckAsync()
    {
        var probeRoot = Path.Combine(
            DebugInstanceContext.Info.RuntimeTempRoot,
            "bastion-template-probe-" + Guid.NewGuid().ToString("N"));
        var connectionsRoot = Path.Combine(probeRoot, "Connections");
        try
        {
            var store = new ConnectionStore(connectionsRoot);
            var a = new Models.Connection
            {
                Name = "target-a",
                Host = "bastion.example.test",
                Port = 22,
                Username = "probe",
                LoginCommands =
                    "#template 1\n#reuse-enter\n#select {{name}}\n#duplicate\n#template 2\n#reuse-leave\n#template 4",
            };
            var b = new Models.Connection
            {
                Name = "target-b",
                Host = "BASTION.EXAMPLE.TEST.",
                Port = 22,
                Username = "probe",
                LoginCommands =
                    "#template 1\n#reuse-enter\n#select {{name}}\n#duplicate\n#template 2\n#reuse-leave\n#template 4",
            };
            var aPath = store.Save(a, connectionsRoot);
            var bPath = store.Save(b, connectionsRoot);
            var loadedA = store.Load(aPath);
            var loadedB = store.Load(bPath);
            var automaticTemplateId = loadedA.ResolvedBastionProfile!.Id;
            var defaultAssociation =
                store.BastionProfiles.Profiles.Count == 0
                && loadedA.TryResolveLoginCommands(out _, out _)
                && loadedB.TryResolveLoginCommands(out _, out _)
                && automaticTemplateId == loadedB.ResolvedBastionProfile!.Id;
            var editorA = ConnectionEditorViewModel.FromConnection(
                loadedA,
                store.BastionProfiles);
            editorA.LoginCommands = "\r\n " + editorA.LoginCommands + "\r\n\r\n";
            editorA.BastionTemplateSegment1 = "\r\n#input\r\n\r\n";
            editorA.BastionTemplateSegment2 = "";
            editorA.BastionTemplateSegment3 = "sudo -i";
            editorA.BastionTemplateSegment4 = "\nexit\n#key Enter\n ";
            editorA.ApplyTo(loadedA);
            store.SaveInPlace(loadedA, aPath);

            var editorB = ConnectionEditorViewModel.FromConnection(
                loadedB,
                store.BastionProfiles);
            store.SaveInPlace(loadedB, bPath);
            loadedA = store.Load(aPath);
            loadedB = store.Load(bPath);
            var profileText = File.ReadAllText(store.BastionProfiles.FilePath);
            var sameTemplate =
                loadedA.ResolvedBastionProfile!.Id == loadedB.ResolvedBastionProfile!.Id;

            var passed = defaultAssociation
                         && store.BastionProfiles.Profiles.Count == 1
                         && editorA.HasBastionProfile
                         && editorB.HasBastionProfile
                         && sameTemplate
                         && loadedA.UsesBastionProfile
                         && loadedB.UsesBastionProfile
                         && !File.ReadAllText(aPath)
                             .Contains("BastionTemplateId", StringComparison.Ordinal)
                         && !loadedA.LoginCommands.StartsWith(
                             Environment.NewLine,
                             StringComparison.Ordinal)
                         && !loadedA.LoginCommands.EndsWith(
                             Environment.NewLine,
                             StringComparison.Ordinal)
                         && loadedA.LoginCommands.Contains("#template 1", StringComparison.Ordinal)
                         && loadedB.LoginCommands.Contains("#template 4", StringComparison.Ordinal)
                         && loadedA.EffectiveLoginCommands.Contains("#input", StringComparison.Ordinal)
                         && loadedA.EffectiveLoginCommands.Contains("#key Enter", StringComparison.Ordinal)
                         && loadedA.EffectiveLoginCommands.Contains("#select target-a", StringComparison.Ordinal)
                         && loadedB.EffectiveLoginCommands.Contains("#select target-b", StringComparison.Ordinal)
                         && Path.GetFileName(store.BastionProfiles.FilePath)
                             == "bastion-login-templates.json"
                         && !profileText.Contains("target-a", StringComparison.Ordinal)
                         && !profileText.Contains("target-b", StringComparison.Ordinal);

            var report =
                $"{(passed ? "PASS" : "FAIL")}: shared bastion login template\n"
                + $"defaultAssociation={defaultAssociation}\n"
                + $"profiles={store.BastionProfiles.Profiles.Count}\n"
                + $"sameTemplate={sameTemplate}\n"
                + $"segmentCount={store.BastionProfiles.Profiles.Single().Segments.Length}\n"
                + $"surroundingBlankLinesTrimmed="
                + $"{!loadedA.LoginCommands.StartsWith(Environment.NewLine, StringComparison.Ordinal)}\n"
                + $"connectionReferencesPreserved="
                + $"{loadedA.LoginCommands.Contains("#template 1", StringComparison.Ordinal)}";
            return Task.FromResult(ToolText(report, isError: !passed));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolText(
                $"FAIL: shared bastion login template threw {ex.GetType().Name}: {ex.Message}",
                isError: true));
        }
        finally
        {
            try
            {
                if (Directory.Exists(probeRoot))
                    Directory.Delete(probeRoot, recursive: true);
            }
            catch
            {
                // Probe cleanup is best effort inside the isolated Debug temp root.
            }
        }
    }

    private static async Task<JsonObject> BastionTemplatePresetCheckAsync()
    {
        var (passed, report) = await OnUiAsync(() =>
        {
            if (Desktop?.MainWindow is not Views.MainWindow main)
                return (false, "FAIL: MainWindow is not available.");

            var editor = new ConnectionEditorViewModel
            {
                Type = ConnectionType.Ssh,
                Name = "target-a",
                Host = "bastion.example.test",
                Port = 22,
                Username = "probe",
                HasBastionProfile = true,
                BastionTemplateId = "preset-check",
                BastionProfileEndpoint = "probe@bastion.example.test:22",
                BastionTemplateSegment1 = "keep-existing",
                LoginCommands = " \r\n ",
            };
            var approveOverwrite = false;
            var confirmationCount = 0;
            var localizedConfirmation = false;
            var dialog = main.CreateBastionTemplateEditorDialog(
                editor,
                confirmAsync: (title, prompt) =>
                {
                    confirmationCount++;
                    localizedConfirmation =
                        title == Localizer.Get("BastionTemplateOverwriteTitle")
                        && prompt == Localizer.Get("BastionTemplateOverwritePrompt");
                    return Task.FromResult(approveOverwrite);
                });
            try
            {
                dialog.Show(main);
                var descendants = dialog.GetVisualDescendants().OfType<Control>().ToArray();
                var insert = descendants
                    .OfType<Button>()
                    .FirstOrDefault(control =>
                        control.Name == "InsertTypicalBastionTemplateButton");
                var save = descendants
                    .OfType<Button>()
                    .FirstOrDefault(control =>
                        control.Name == "SaveBastionTemplateButton");
                var fragments = Enumerable.Range(1, BastionLoginProfile.SegmentCount)
                    .Select(id => descendants
                        .OfType<TextBox>()
                        .FirstOrDefault(control =>
                            control.Name == $"BastionTemplateSegment{id}"))
                    .ToArray();
                var hint = descendants
                    .OfType<TextBlock>()
                    .FirstOrDefault(control =>
                        control.Name == "TypicalBastionTemplateHint");

                insert?.RaiseEvent(
                    new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                var cancelledOverwritePreserved =
                    fragments[0]?.Text == "keep-existing"
                    && fragments.Skip(1).All(fragment =>
                        string.IsNullOrWhiteSpace(fragment?.Text))
                    && confirmationCount == 1;

                approveOverwrite = true;
                insert?.RaiseEvent(
                    new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                var dialogFilled = insert is not null
                                   && save is not null
                                   && fragments.All(fragment => fragment is not null)
                                   && Enumerable.Range(1, BastionLoginProfile.SegmentCount)
                                       .All(id =>
                                           fragments[id - 1]!.Text
                                               == BastionLoginTemplatePreset.GetSegment(id)
                                                   .ReplaceLineEndings(Environment.NewLine))
                                   && hint?.Text == Localizer.Get("BastionTemplateTypicalHint");
                var overwriteConfirmed =
                    confirmationCount == 2
                    && localizedConfirmation;
                var buttonLocalized =
                    insert?.Content?.ToString()
                    == Localizer.Get("BastionTemplateInsertTypical");
                var transactionalBeforeSave = string.IsNullOrWhiteSpace(editor.LoginCommands);

                save?.RaiseEvent(
                    new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                var commandsInserted =
                    editor.LoginCommands
                    == BastionLoginTemplatePreset.ConnectionLoginCommands
                        .ReplaceLineEndings(Environment.NewLine);
                var savedSegments = new[]
                {
                    editor.BastionTemplateSegment1,
                    editor.BastionTemplateSegment2,
                    editor.BastionTemplateSegment3,
                    editor.BastionTemplateSegment4,
                };
                var fragmentsSaved = Enumerable.Range(1, BastionLoginProfile.SegmentCount)
                    .All(id =>
                        savedSegments[id - 1]
                        == BastionLoginTemplatePreset.GetSegment(id)
                            .ReplaceLineEndings(Environment.NewLine));
                var existingPreserved =
                    BastionLoginTemplatePreset.UseConnectionCommandsWhenEmpty("custom")
                    == "custom";
                var ok = dialogFilled
                         && buttonLocalized
                         && cancelledOverwritePreserved
                         && overwriteConfirmed
                         && transactionalBeforeSave
                         && commandsInserted
                         && fragmentsSaved
                         && existingPreserved;
                return (ok,
                    $"{(ok ? "PASS" : "FAIL")}: typical bastion template preset\n"
                    + $"buttonFound={insert is not null}\n"
                    + $"buttonLocalized={buttonLocalized}\n"
                    + $"cancelledOverwritePreserved={cancelledOverwritePreserved}\n"
                    + $"overwriteConfirmed={overwriteConfirmed}\n"
                    + $"dialogFilled={dialogFilled}\n"
                    + $"transactionalBeforeSave={transactionalBeforeSave}\n"
                    + $"commandsInserted={commandsInserted}\n"
                    + $"fragmentsSaved={fragmentsSaved}\n"
                    + $"existingCommandsPreserved={existingPreserved}");
            }
            finally
            {
                if (dialog.IsVisible)
                    dialog.Close();
            }
        }).ConfigureAwait(false);

        return ToolText(report, isError: !passed);
    }

    private static Task<JsonObject> LoginCommandVariableCheckAsync()
    {
        var template = new BastionLoginProfile
        {
            Id = "variable-check",
            Segments =
            [
                "#select {{name}}\nssh {{username}}@{{host}} -p {{port}}\necho \\{{host}}",
                "",
                "",
                "",
            ],
        };
        var connection = new Models.Connection
        {
            Name = "target-b",
            Host = "bastion.example.test",
            Port = 0,
            Username = "probe",
            LoginCommands = "#template 1",
            ResolvedBastionProfile = template,
        };

        var resolvedOk = connection.TryResolveLoginCommands(out var resolved, out _);
        var unknownRejected = !LoginCommandSequence.TryResolve(
            "{{password}}",
            null,
            connection,
            out _,
            out var unknownError);
        var emptyUsername = new Models.Connection
        {
            Name = connection.Name,
            Host = connection.Host,
            Port = connection.Port,
            Username = "",
        };
        var emptyRejected = !LoginCommandSequence.TryResolve(
            "{{username}}",
            null,
            emptyUsername,
            out _,
            out var emptyError);
        var templateSourceReported = !LoginCommandSequence.TryResolve(
            "#template 1",
            new BastionLoginProfile
            {
                Id = "bad-variable",
                Segments = ["{{missing}}", "", "", ""],
            },
            connection,
            out _,
            out var templateError);

        var passed = resolvedOk
                     && resolved.ReplaceLineEndings("\n")
                         == "#select target-b\nssh probe@bastion.example.test -p 22\necho {{host}}"
                     && unknownRejected
                     && unknownError.Contains("unknown connection variable", StringComparison.Ordinal)
                     && emptyRejected
                     && emptyError.Contains("is empty", StringComparison.Ordinal)
                     && templateSourceReported
                     && templateError.StartsWith("Template fragment 1, line 1:", StringComparison.Ordinal);
        var report =
            $"{(passed ? "PASS" : "FAIL")}: login command variables\n"
            + $"resolved={resolvedOk}\n"
            + $"escapedLiteral={resolved.Contains("{{host}}", StringComparison.Ordinal)}\n"
            + $"unknownRejected={unknownRejected}\n"
            + $"emptyRejected={emptyRejected}\n"
            + $"templateSourceReported={templateSourceReported}\n"
            + "--- resolved ---\n"
            + resolved;
        return Task.FromResult(ToolText(report, isError: !passed));
    }

    private sealed class LateChannelProbe : IDisposable
    {
        public bool IsDisposed { get; private set; }
        public void Dispose() => IsDisposed = true;
    }

    /// <summary>
    /// Closing a WSL or agent-CLI tab disposes the ConPTY session while keystrokes and
    /// layout-driven resizes are still in flight. Both used to check a plain flag outside
    /// the write gate, so a Write could land on a closed FileStream and a Resize on an
    /// already-closed HPCON. This drives that window directly on real sessions.
    /// </summary>
    private static async Task<JsonObject> ConPtyTeardownRaceCheckAsync()
    {
        const int rounds = 10;
        const int writersPerRound = 6;
        var writes = 0;
        var resizes = 0;
        var failures = new List<string>();

        // A child that never drains stdin. Once the console input buffer fills, ConPTY
        // stops reading our pipe and Write blocks inside the FileStream — which is the
        // window Dispose has to respect. Small keystroke-sized payloads always drain and
        // never reproduce it, so push 64 KiB at a time.
        var payload = new byte[64 * 1024];
        Array.Fill(payload, (byte)'x');

        for (var round = 0; round < rounds; round++)
        {
            ConPtySession session;
            try
            {
                session = ConPtySession.Start(
                    Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                    ["/c", "ping -n 30 127.0.0.1 > nul"],
                    80,
                    25);
            }
            catch (Exception ex)
            {
                failures.Add($"round {round}: could not start a pseudo console: {ex.Message}");
                break;
            }

            var roundNumber = round;
            using var stop = new CancellationTokenSource();
            void Record(string what, Exception ex)
            {
                lock (failures)
                    failures.Add($"round {roundNumber}: {what} threw {ex.GetType().Name}: {ex.Message}");
            }

            var workers = new List<Task>(writersPerRound + 1);
            for (var writerIndex = 0; writerIndex < writersPerRound; writerIndex++)
            {
                workers.Add(Task.Run(() =>
                {
                    // Keystroke-sized writes first: they drain, so they prove the normal
                    // path still works. The big ones then wedge and set up the race.
                    var keystroke = "\r\n"u8.ToArray();
                    for (var i = 0; i < 8 && !stop.IsCancellationRequested; i++)
                    {
                        try
                        {
                            session.Write(keystroke);
                            Interlocked.Increment(ref writes);
                        }
                        catch (Exception ex)
                        {
                            Record("Write", ex);
                            return;
                        }
                    }

                    while (!stop.IsCancellationRequested)
                    {
                        try
                        {
                            session.Write(payload);
                            Interlocked.Increment(ref writes);
                        }
                        catch (Exception ex)
                        {
                            Record("Write", ex);
                            return;
                        }
                    }
                }));
            }
            workers.Add(Task.Run(() =>
            {
                var columns = 80;
                while (!stop.IsCancellationRequested)
                {
                    try
                    {
                        session.Resize(columns = columns == 80 ? 120 : 80, 25);
                        Interlocked.Increment(ref resizes);
                    }
                    catch (Exception ex)
                    {
                        Record("Resize", ex);
                        return;
                    }
                }
            }));

            // Let the writers wedge on a full pipe so Dispose lands mid-write.
            await Task.Delay(120);
            session.Dispose();
            stop.Cancel();
            await Task.WhenAll(workers);

            // Post-dispose calls must be silent no-ops, not throws.
            try
            {
                session.Write(payload);
                session.Resize(100, 30);
                session.Dispose();
            }
            catch (Exception ex)
            {
                Record("post-dispose call", ex);
            }
        }

        var passed = failures.Count == 0 && writes > 0 && resizes > 0;
        var report =
            $"{(passed ? "PASS" : "FAIL")}: ConPTY teardown races with concurrent Write/Resize\n"
            + $"rounds={rounds}\n"
            + $"writes={writes}\n"
            + $"resizes={resizes}\n"
            + $"failures={failures.Count}"
            + (failures.Count == 0 ? "" : "\n" + string.Join("\n", failures.Take(10)));
        return ToolText(report, isError: !passed);
    }

    private static async Task<JsonObject> BastionChannelLimitCheckAsync()
    {
        var capacity = new ShellChannelCapacityTracker();
        var startsUnknownAndAvailable = capacity.KnownLimit is null && capacity.HasCapacity;
        capacity.MarkOpened();
        capacity.MarkOpened();
        capacity.MarkOpened();
        var observedLimit = capacity.RecordObservedLimit();
        var fullAtObservedLimit = observedLimit == 3
                                  && capacity.ActiveChannels == 3
                                  && !capacity.HasCapacity;
        capacity.MarkClosed();
        var reusableAfterClose = capacity.ActiveChannels == 2 && capacity.HasCapacity;
        capacity.MarkOpened();
        var fullAgainWithoutProbe = capacity.ActiveChannels == 3 && !capacity.HasCapacity;

        // A timeout on a transport that never opened a channel must not be turned into a
        // ceiling: that would leave HasCapacity false forever and retire a healthy link.
        var coldTracker = new ShellChannelCapacityTracker();
        var coldTimeoutRecordsNothing = coldTracker.TryRecordTimedOutLimit() is null
                                        && coldTracker.KnownLimit is null
                                        && coldTracker.HasCapacity;
        coldTracker.MarkOpened();
        coldTracker.MarkOpened();
        var warmTimeoutRecordsLimit = coldTracker.TryRecordTimedOutLimit() == 2
                                      && !coldTracker.HasCapacity;

        // An outright refusal at zero open channels is the server's own answer, so the
        // ceiling stands — and the pool has to retire the transport instead of holding it.
        var refusedTracker = new ShellChannelCapacityTracker();
        var refusedAtZero = refusedTracker.RecordObservedLimit() == 0
                            && !refusedTracker.HasCapacity;

        var source = new TaskCompletionSource<LateChannelProbe>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var timedOut = false;
        try
        {
            _ = await SharedSshClient.WaitForChannelOpenAsync(
                source.Task,
                TimeSpan.FromMilliseconds(30),
                CancellationToken.None,
                probe => probe.Dispose());
        }
        catch (TimeoutException)
        {
            timedOut = true;
        }

        var lateProbe = new LateChannelProbe();
        source.TrySetResult(lateProbe);
        for (var attempt = 0; attempt < 10 && !lateProbe.IsDisposed; attempt++)
            await Task.Delay(10);

        var visibleWait = TerminalView.BastionPoolWaitingMessage.Contains(
            "Waiting for another session",
            StringComparison.Ordinal);
        var boundedQueue = TerminalView.BastionPoolWaitTimeoutSeconds > 0
                           && TerminalView.BastionPoolWaitTimeoutMessage.Contains(
                               "opening a fresh SSH connection",
                               StringComparison.Ordinal);
        var visibleFallback = TerminalView.BastionReuseFallbackMessage.Contains(
            "opening a fresh SSH connection",
            StringComparison.Ordinal);
        var visibleFull = TerminalView.BastionPoolFullMessage.Contains(
            "observed session limits",
            StringComparison.Ordinal)
                          && TerminalView.BastionPoolFullMessage.Contains(
                              "opening a fresh SSH connection",
                              StringComparison.Ordinal);

        var passed = timedOut
                     && lateProbe.IsDisposed
                     && startsUnknownAndAvailable
                     && fullAtObservedLimit
                     && reusableAfterClose
                     && fullAgainWithoutProbe
                     && coldTimeoutRecordsNothing
                     && warmTimeoutRecordsLimit
                     && refusedAtZero
                     && visibleWait
                     && boundedQueue
                     && visibleFull
                     && visibleFallback;
        var report =
            $"{(passed ? "PASS" : "FAIL")}: bastion channel-limit handling\n"
            + $"shellOpenTimeoutSeconds={SharedSshClient.ShellOpenTimeoutSeconds}\n"
            + $"poolWaitTimeoutSeconds={TerminalView.BastionPoolWaitTimeoutSeconds}\n"
            + $"timeoutObserved={timedOut}\n"
            + $"lateChannelDisposed={lateProbe.IsDisposed}\n"
            + $"startsUnknownAndAvailable={startsUnknownAndAvailable}\n"
            + $"observedLimit={observedLimit}\n"
            + $"fullAtObservedLimit={fullAtObservedLimit}\n"
            + $"reusableAfterClose={reusableAfterClose}\n"
            + $"fullAgainWithoutProbe={fullAgainWithoutProbe}\n"
            + $"coldTimeoutRecordsNothing={coldTimeoutRecordsNothing}\n"
            + $"warmTimeoutRecordsLimit={warmTimeoutRecordsLimit}\n"
            + $"refusedAtZero={refusedAtZero}\n"
            + $"visibleWait={visibleWait}\n"
            + $"boundedQueue={boundedQueue}\n"
            + $"visibleFull={visibleFull}\n"
            + $"visibleFallback={visibleFallback}";
        return ToolText(report, isError: !passed);
    }

    private static async Task<JsonObject> ConnectionEditorSwitchCheckAsync()
    {
        var (passed, report) = await OnUiAsync(() =>
        {
            if (ResolveRoot("MainVm") is not MainWindowViewModel vm)
                return (false, "FAIL: MainWindowViewModel is not available.");

            static void Collect(
                IEnumerable<TreeNodeViewModel> nodes,
                List<TreeNodeViewModel> result)
            {
                foreach (var node in nodes)
                {
                    if (!node.IsRecent && node.Connection is { IsSsh: true })
                        result.Add(node);
                    if (node.IsFolder)
                        Collect(node.Children, result);
                }
            }

            var candidates = new List<TreeNodeViewModel>();
            Collect(vm.Nodes, candidates);
            candidates = candidates.Take(12).ToList();
            if (candidates.Count == 0)
                return (true, "PASS: no SSH connections are available for the editor switch probe.");

            vm.FlushAutoSave();
            var previous = vm.SelectedNode;
            var editorsBuilt = 0;
            var stopwatch = Stopwatch.StartNew();
            foreach (var candidate in candidates)
            {
                vm.SelectedNode = candidate;
                if (vm.Editor is not null)
                    editorsBuilt++;
            }
            stopwatch.Stop();
            if (previous is not { IsRecent: true })
                vm.SelectedNode = previous;

            var averageMs = stopwatch.Elapsed.TotalMilliseconds / candidates.Count;
            var ok = editorsBuilt == candidates.Count;
            return (
                ok,
                $"{(ok ? "PASS" : "FAIL")}: connection editor switching\n"
                + $"connections={candidates.Count}\n"
                + $"editorsBuilt={editorsBuilt}\n"
                + $"totalMs={stopwatch.Elapsed.TotalMilliseconds:0.0}\n"
                + $"averageMs={averageMs:0.0}");
        });

        return ToolText(report, isError: !passed);
    }

    private static async Task<JsonObject> AutoUpdateStageCheckAsync(JsonObject args)
    {
        // Exercises the in-app update pipeline (download -> extract -> verify)
        // against the real release URL. Runs off the UI thread; the staging
        // folder is instance-isolated in Debug builds, so parallel worktree
        // instances don't collide.
        var url = args["url"]?.GetValue<string>();
        var keep = args["keep"]?.GetValue<bool>() ?? false;
        IReadOnlyList<string> urls = string.IsNullOrWhiteSpace(url)
            ? AutoUpdateService.GetDefaultDownloadUrls()
            : [url];

        UpdateDownloadProgress? last = null;
        var progress = new SynchronousProgress<UpdateDownloadProgress>(p => Volatile.Write(ref last, p));

        var stopwatch = Stopwatch.StartNew();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var stagedDir = await AutoUpdateService.DownloadAndStageAsync(urls, progress, cts.Token);
        stopwatch.Stop();

        if (stagedDir is null)
        {
            return ToolText(
                $"FAIL: download/stage failed after {stopwatch.Elapsed.TotalSeconds:0}s: {AutoUpdateService.FailureReason}",
                isError: true);
        }

        var exePath = Path.Combine(stagedDir, "JeekRemoteManager.exe");
        var exeSize = File.Exists(exePath) ? new FileInfo(exePath).Length : 0;
        var fileCount = Directory.EnumerateFileSystemEntries(stagedDir, "*", SearchOption.AllDirectories).Count();
        var report =
            $"PASS: staged at {stagedDir}\n"
            + $"Files: {fileCount}, JeekRemoteManager.exe: {exeSize} bytes\n"
            + $"Downloaded {Volatile.Read(ref last)?.ReceivedBytes ?? 0} bytes in {stopwatch.Elapsed.TotalSeconds:0.0}s "
            + $"(mirror {(Volatile.Read(ref last)?.MirrorIndex ?? 0) + 1}/{urls.Count})";

        if (!keep)
        {
            try
            {
                Directory.Delete(Path.GetDirectoryName(stagedDir)!, recursive: true);
                report += "\nStaged folder cleaned up.";
            }
            catch (Exception ex)
            {
                report += $"\nCleanup failed: {ex.Message}";
            }
        }

        return ToolText(report);
    }

    private sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    #endregion
}
