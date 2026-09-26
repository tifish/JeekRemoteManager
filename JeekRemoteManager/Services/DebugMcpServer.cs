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
internal static partial class DebugMcpServer
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
        host.AddTool("settings_dialog_layout_check", _ => SettingsDialogLayoutCheckAsync());
        host.AddTool("button_content_alignment_check", _ => ButtonContentAlignmentCheckAsync());
        host.AddTool("ai_runtime_snapshot", _ => AiRuntimeSnapshotAsync());
        host.AddTool("password_ime_check", _ => PasswordImeCheckAsync());
        host.AddTool("terminal_tab_title_check", _ => TerminalTabTitleCheckAsync());
        host.AddTool("terminal_tab_focus_check", _ => TerminalTabFocusCheckAsync());
        host.AddTool("terminal_tab_lifecycle_check", _ => TerminalTabLifecycleCheckAsync());
        host.AddTool("terminal_connection_actions_check", _ => TerminalConnectionActionsCheckAsync());
        host.AddTool("terminal_output_coalescing_check", _ => TerminalOutputCoalescingCheckAsync());
        host.AddTool("script_completion_order_check", _ => ScriptCompletionOrderCheckAsync());
        host.AddTool("terminal_encoding_check", _ => TerminalEncodingCheckAsync());
        host.AddTool("terminal_session_log_check", _ => TerminalSessionLogCheckAsync());
        host.AddTool("terminal_find_check", _ => TerminalFindCheckAsync());
        host.AddTool("terminal_appearance_check", _ => TerminalAppearanceCheckAsync());
        host.AddTool(
            "terminal_output_backpressure_check",
            _ => Task.FromResult(TerminalOutputBackpressureCheck()));
        host.AddTool("zmodem_subpacket_limit_check", _ => ZmodemSubpacketLimitCheckAsync());
        host.AddTool(
            "zmodem_detector_latency_check",
            _ => Task.FromResult(ZmodemDetectorLatencyCheck()));
        host.AddTool("ssh_auth_prompt_check", _ => Task.Run(SshAuthPromptCheck));
        host.AddTool("host_key_trust_check", _ => Task.FromResult(HostKeyTrustCheck()));
        host.AddTool("sftp_host_key_check", SftpHostKeyCheckAsync);
        host.AddTool("ssh_jump_forward_check", SshJumpForwardCheckAsync);
        host.AddTool("sftp_retry_policy_check", _ => SftpRetryPolicyCheckAsync());
        host.AddTool("connection_write_watcher_check", _ => ConnectionWriteWatcherCheckAsync());
        host.AddTool("connection_external_change_check", _ => ConnectionExternalChangeCheckAsync());
        host.AddTool("connection_tree_read_cache_check", _ => ConnectionTreeReadCacheCheckAsync());
        host.AddTool("connection_tree_reload_order_check", _ => ConnectionTreeReloadOrderCheckAsync());
        host.AddTool("connection_tree_load_check", _ => ConnectionTreeLoadCheckAsync());
        host.AddTool("monitor_suspend_check", _ => MonitorSuspendCheckAsync());
        host.AddTool("terminal_font_sync_check", _ => TerminalFontSyncCheckAsync());
        host.AddTool("ai_panel_lifecycle_check", _ => AiPanelLifecycleCheckAsync());
        host.AddTool("file_browser_session_lifecycle_check", _ => FileBrowserSessionLifecycleCheckAsync());
        host.AddTool("ai_cli_ctrl_c_check", _ => AiCliCtrlCCheckAsync());
        host.AddTool("agent_cli_locate_check", AgentCliLocateCheckAsync);
        host.AddTool("agent_desktop_launch_check", AgentDesktopLaunchCheckAsync);
        host.AddTool("agent_discovery_cache_check", _ => AgentDiscoveryCacheCheckAsync());
        host.AddTool("agent_cli_mcp_config_check", AgentCliMcpConfigCheckAsync);
        host.AddTool("login_menu_select_check", LoginMenuSelectCheckAsync);
        host.AddTool("login_command_flow_check", LoginCommandFlowCheckAsync);
        host.AddTool("login_command_completion_check", _ => LoginCommandCompletionCheckAsync());
        host.AddTool("login_command_variable_check", _ => LoginCommandVariableCheckAsync());
        host.AddTool("bastion_login_template_check", _ => BastionLoginTemplateCheckAsync());
        host.AddTool("bastion_template_preset_check", _ => BastionTemplatePresetCheckAsync());
        host.AddTool("conpty_teardown_race_check", _ => ConPtyTeardownRaceCheckAsync());
        host.AddTool("conpty_environment_check", _ => ConPtyEnvironmentCheckAsync());
        host.AddTool("bastion_channel_limit_check", _ => BastionChannelLimitCheckAsync());
        host.AddTool("bastion_reuse_landing_check", _ => Task.FromResult(BastionReuseLandingCheck()));
        host.AddTool("bastion_pool_lease_check", _ => BastionPoolLeaseCheckAsync());
        host.AddTool("bastion_tray_lifecycle_check", _ => BastionTrayLifecycleCheckAsync());
        host.AddTool("window_close_reason_check", _ => WindowCloseReasonCheckAsync());
        host.AddTool("connection_editor_switch_check", _ => ConnectionEditorSwitchCheckAsync());
        host.AddTool("login_menu_select_probe", LoginMenuSelectProbeAsync);
        host.AddTool("auto_update_stage_check", AutoUpdateStageCheckAsync);
        host.AddTool("ai_render_probe", AiRenderProbeAsync);
        host.AddTool("agent_project_link_check", AgentProjectLinkCheckAsync);
        host.AddTool("agent_application_link_check", AgentApplicationLinkCheckAsync);
        host.AddTool("global_agent_check", _ => GlobalAgentCheckAsync());
        host.AddTool("mcp_transport_check", _ => McpTransportCheckAsync());
        host.AddTool("mcp_concurrency_check", _ => McpConcurrencyCheckAsync());
        host.AddTool("mcp_reconnect_check", _ => McpReconnectCheckAsync());
        host.AddTool("ssh_port_forward_validate", args => Task.FromResult(ToolText(
            SshPortForwarding.Validate(McpHost.RequiredString(args, "text")) ?? "valid")));
        host.AddTool("mcp_adapter_offline_check", _ => McpAdapterOfflineCheckAsync());
        host.AddTool("product_mcp_check", _ => ProductMcpCheckAsync());
        return host;
    }

    /// <summary>Views closed by the previous lifecycle run, measured by the next one so
    /// the measurement is not taken from inside the call that created them.</summary>
    private static List<WeakReference<TerminalView>> _lifecycleProbeCarryOver = [];

    /// <summary>
    /// How many of <paramref name="views"/> are sitting in the window renderer's dirty
    /// set, plus a short description of that renderer. Reaches into Avalonia internals on
    /// purpose: there is no public way to ask, and telling "queued for the next render
    /// pass" apart from "genuinely leaked" is the whole point of this check.
    /// Returns -1 pending when the internals cannot be read, so the check stays strict.
    /// </summary>
    private static (int Pending, string RendererState) CountPendingInRendererDirtySet(
        IEnumerable<WeakReference<TerminalView>> views)
    {
        try
        {
            var topLevel = Desktop?.MainWindow;
            if (topLevel is null)
                return (0, "no main window");

            var source = ReadMember(topLevel, "PresentationSource");
            var renderer = source is null ? null : ReadMember(source, "Renderer");
            if (renderer is null)
                return (-1, "renderer not reachable");

            if (ReadMember(renderer, "_dirty") is not System.Collections.IEnumerable dirty)
                return (-1, "dirty set not reachable");

            var dirtyVisuals = dirty.Cast<object>().ToHashSet();
            var pending = views.Count(reference =>
                reference.TryGetTarget(out var view) && dirtyVisuals.Contains(view));

            var compositionTarget = ReadMember(renderer, "CompositionTarget");
            var enabled = compositionTarget is null ? null : ReadMember(compositionTarget, "IsEnabled");
            return (
                pending,
                $"dirtyCount={dirtyVisuals.Count}; windowVisible={topLevel.IsVisible}; enabled={enabled ?? "?"}");
        }
        catch (Exception ex)
        {
            return (-1, $"inspection failed: {ex.GetType().Name}");
        }
    }

    /// <summary>Reads a property or field by name, walking the declaring hierarchy so a
    /// name redeclared on a base type does not come back ambiguous.</summary>
    private static object? ReadMember(object instance, string name)
    {
        const System.Reflection.BindingFlags flags =
            System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.DeclaredOnly;

        for (var type = instance.GetType(); type is not null; type = type.BaseType)
        {
            var property = type.GetProperties(flags)
                .FirstOrDefault(candidate => candidate.Name == name && candidate.GetIndexParameters().Length == 0);
            if (property is not null)
                return property.GetValue(instance);

            var field = type.GetFields(flags).FirstOrDefault(candidate => candidate.Name == name);
            if (field is not null)
                return field.GetValue(instance);
        }

        return null;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
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

    /// <summary>
    /// Debug writes pin <c>--instance</c> to this worktree; Release omits it so committed
    /// project files talk to the installed app.
    /// </summary>
    private static bool PortableArgsPinThisInstance(string text) =>
        AgentWorkspaceLink.AdapterInstanceId is { } instanceId
            ? text.Contains("--instance", StringComparison.Ordinal)
              && text.Contains(instanceId, StringComparison.Ordinal)
            : !text.Contains("--instance", StringComparison.Ordinal);

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

    private static async Task<JsonObject> SettingsDialogLayoutCheckAsync()
    {
        var opened = await OnUiAsync(() =>
        {
            if (Desktop?.MainWindow is not Views.MainWindow main
                || main.DataContext is not MainWindowViewModel vm)
                return false;

            main.ActivateMainWindow();
            vm.OpenSettingsCommand.Execute(null);
            return true;
        });
        if (!opened)
            return ToolText("FAIL: MainWindow is not available.", isError: true);

        await Task.Delay(150);

        var (passed, report) = await OnUiAsync(() =>
        {
            var dialog = Desktop?.Windows.FirstOrDefault(window => window.Name == "SettingsDialog");
            if (dialog is null)
                return (false, "FAIL: Settings dialog did not open.");

            try
            {
                var descendants = dialog.GetVisualDescendants().OfType<Control>().ToArray();
                var root = dialog.Content as Grid;
                var header = descendants.FirstOrDefault(control => control.Name == "SettingsDialogHeader");
                var scroller = descendants.OfType<ScrollViewer>()
                    .FirstOrDefault(control => control.Name == "SettingsDialogScrollViewer");
                var footer = descendants.FirstOrDefault(control => control.Name == "SettingsDialogFooter");
                var cards = new[]
                {
                    "SettingsAppearanceCard",
                    "SettingsTerminalCard",
                    "SettingsFilesCard",
                    "SettingsSecurityCard",
                    "SettingsUpdatesCard",
                }
                    .Select(name => descendants.OfType<Border>().FirstOrDefault(control => control.Name == name))
                    .ToArray();
                var actions = new[] { "SettingsCancelButton", "SettingsOkButton" }
                    .Select(name => descendants.OfType<Button>().FirstOrDefault(control => control.Name == name))
                    .ToArray();
                var actionPanel = footer is Border { Child: StackPanel panel } ? panel : null;

                var structureOk = root?.Name == "SettingsDialogLayout"
                                  && root.RowDefinitions.Count == 3
                                  && root.RowDefinitions[1].Height.IsStar
                                  && header is not null && Grid.GetRow(header) == 0
                                  && scroller is not null && Grid.GetRow(scroller) == 1
                                  && footer is not null && Grid.GetRow(footer) == 2;
                var cardsOk = cards.All(card => card?.Classes.Contains("form-card") == true);
                var actionsOk = actions.All(button => button is not null)
                                && actions[1]?.Classes.Contains("accent") == true
                                && actionPanel?.Children.Count == 2
                                && actionPanel.Children[0].Name == "SettingsOkButton"
                                && actionPanel.Children[1].Name == "SettingsCancelButton";
                var sizingOk = dialog.CanResize
                               && dialog.Width >= 640
                               && dialog.Height >= 640
                               && dialog.MinWidth <= dialog.Width
                               && dialog.MinHeight <= dialog.Height;
                // The terminal card always opens on a concrete choice for each field.
                var terminalBoxes = new[] { "SettingsTerminalFontBox", "SettingsTerminalSchemeBox", "SettingsTerminalScrollbackBox" }
                    .Select(name => descendants.OfType<ComboBox>().FirstOrDefault(control => control.Name == name))
                    .ToArray();
                var terminalOk = terminalBoxes.All(box => box?.SelectedItem is not null);
                var ok = dialog.IsVisible && structureOk && cardsOk && actionsOk && sizingOk && terminalOk;

                return (ok,
                    $"{(ok ? "PASS" : "FAIL")}: Settings dialog layout\n"
                    + $"visible={dialog.IsVisible}, size={dialog.Width}x{dialog.Height}, min={dialog.MinWidth}x{dialog.MinHeight}, resizable={dialog.CanResize}\n"
                    + $"rows={(root is null ? 0 : root.RowDefinitions.Count)}, structure={structureOk}\n"
                    + $"scrollbars={scroller?.HorizontalScrollBarVisibility}/{scroller?.VerticalScrollBarVisibility}\n"
                    + $"cards={string.Join(",", cards.Select(card => card?.Name ?? "missing"))}\n"
                    + $"actions={string.Join(",", actionPanel?.Children.Select(control => control.Name ?? "unnamed") ?? [])}\n"
                    + $"terminal={string.Join(",", terminalBoxes.Select(box => box?.SelectedItem?.ToString() ?? "missing"))}");
            }
            finally
            {
                dialog.Close();
            }
        });

        return ToolText(report, isError: !passed);
    }

    /// <summary>
    /// Runs <paramref name="act"/> on the UI thread, drains the dispatcher down to background
    /// priority - where <see cref="PasswordImeGuard"/> posts its deferred restore - and only
    /// then reads the outcome.
    /// </summary>
    private static async Task<T> ActThenReadAsync<T>(Action act, Func<T> read)
    {
        await OnUiAsync(() => { act(); return true; });
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        return await OnUiAsync(read);
    }

    private static TerminalView? _renderProbeView;
    private static TabItem? _renderProbeTab;

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

    private static string DescribeExecutablelessSurface(
        AgentCliKind agentKind,
        AgentSurfaceKind surfaceKind)
    {
        if (surfaceKind != AgentSurfaceKind.Desktop)
            return "available without a local executable";

        // Any absolute path works here; only the scheme of the resulting URI is read.
        var uri = AgentCliCatalog.BuildDesktopProtocolUri(agentKind, Path.GetTempPath());
        if (uri is null)
            return "available without a local executable";

        return uri.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? "available via official web launcher"
            : $"available via registered protocol: {uri[..uri.IndexOf(':')]}";
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

    private sealed class LateChannelProbe : IDisposable
    {
        public bool IsDisposed { get; private set; }
        public void Dispose() => IsDisposed = true;
    }

    private static Task<JsonObject> WindowCloseReasonCheckAsync() => OnUiAsync(() =>
    {
        var app = Application.Current as App
                  ?? throw new InvalidOperationException("App is not running.");
        // Use Avalonia's actual close pipeline without requesting shutdown from the
        // desktop lifetime or the OS. CloseCore and the event-args constructor are internal.
        var closeCore = typeof(Window).GetMethod(
            "CloseCore",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            binder: null,
            types: [typeof(WindowCloseReason), typeof(bool), typeof(bool)],
            modifiers: null) ?? throw new MissingMethodException(typeof(Window).FullName, "CloseCore");
        var report = new StringBuilder();
        var passed = true;
        foreach (var reason in new[]
                 {
                     WindowCloseReason.WindowClosing,
                     WindowCloseReason.OSShutdown,
                     WindowCloseReason.ApplicationShutdown,
                 })
        foreach (var programmatic in new[] { false, true })
        foreach (var initiallyHidden in new[] { false, true })
        {
            var window = new Window
            {
                Title = "Window close reason probe",
                Width = 240,
                Height = 100,
                ShowActivated = false,
            };
            var closed = false;
            var cancelled = false;
            var reasonObserved = false;
            window.Closed += (_, _) => closed = true;
            window.Closing += app.OnMainWindowClosing;
            window.Closing += (_, e) =>
            {
                cancelled = e.Cancel;
                reasonObserved = e.CloseReason == reason && e.IsProgrammatic == programmatic;
            };
            try
            {
                window.Show();
                if (initiallyHidden)
                    window.Hide();
                closeCore.Invoke(window, [reason, programmatic, false]);
                var hideToTray = reason == WindowCloseReason.WindowClosing;
                var ok = reasonObserved && !window.IsVisible
                         && cancelled == hideToTray && closed == !hideToTray;
                passed &= ok;
                report.AppendLine(
                    $"{(ok ? "PASS" : "FAIL")}: reason={reason}; programmatic={programmatic}; "
                    + $"initiallyHidden={initiallyHidden}; cancelled={cancelled}; closed={closed}");
            }
            finally
            {
                window.Closing -= app.OnMainWindowClosing;
                if (!closed)
                    window.Close();
            }
        }
        return ToolText(
            $"{(passed ? "PASS" : "FAIL")}: window close reasons\n{report}",
            isError: !passed);
    });

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
