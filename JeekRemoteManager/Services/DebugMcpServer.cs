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
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using JeekTools;
using JeekRemoteManager.Views;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace JeekRemoteManager.Services;

/// <summary>
/// App-specific configuration over the generic <see cref="McpHost"/> in
/// JeekTools: object-graph roots (App/Desktop/MainWindow/MainVm), '#Name'
/// visual-tree lookup, the Avalonia tools (visual_tree, screenshot), the app
/// probe tools, and the instance discovery file. Compiled into all
/// configurations so Debug and Release behave identically, but the listener
/// only starts in Debug builds. Agents reach it through <c>bin\JrmMcp.exe
/// --surface debug</c>, which forwards stdio to this instance's named pipe —
/// the pipe name carries the worktree's instance id, so parallel Debug builds
/// never answer for each other and there is no port to collide over.
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
        host.AddTool("ai_runtime_snapshot", _ => AiRuntimeSnapshotAsync());
        host.AddTool("terminal_tab_focus_check", _ => TerminalTabFocusCheckAsync());
        host.AddTool("ai_cli_ctrl_c_check", _ => AiCliCtrlCCheckAsync());
        host.AddTool("agent_cli_locate_check", AgentCliLocateCheckAsync);
        host.AddTool("login_menu_select_check", LoginMenuSelectCheckAsync);
        host.AddTool("login_menu_select_probe", LoginMenuSelectProbeAsync);
        host.AddTool("auto_update_stage_check", AutoUpdateStageCheckAsync);
        host.AddTool("ai_render_probe", AiRenderProbeAsync);
        host.AddTool("agent_project_link_check", AgentProjectLinkCheckAsync);
        host.AddTool("mcp_transport_check", _ => McpTransportCheckAsync());
        host.AddTool("product_mcp_check", _ => ProductMcpCheckAsync());
        return host;
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
            Check("CLAUDE.md includes AGENTS.md", File.ReadAllText(Path.Combine(project, "CLAUDE.md")).Contains("@AGENTS.md", StringComparison.Ordinal));
            Check(".mcp.json keeps the project's own server", mcpJson.Contains("\"other\"", StringComparison.Ordinal));
            Check(".mcp.json gains this connection as a stdio adapter launch",
                mcpJson.Contains(server, StringComparison.Ordinal)
                && mcpJson.Contains("\"stdio\"", StringComparison.Ordinal)
                && mcpJson.Contains("JrmMcp.exe", StringComparison.Ordinal)
                && mcpJson.Contains("--connection", StringComparison.Ordinal));
            Check(".mcp.json entry carries no URL, port, or token",
                JsonNode.Parse(mcpJson)?["mcpServers"]?[server] is JsonObject entry
                && entry["url"] is null);
            Check(".codex/config.toml keeps existing keys", codexToml.Contains("model = \"gpt-5\"", StringComparison.Ordinal));
            Check(".codex/config.toml gains the server table",
                codexToml.Contains($"[mcp_servers.{server}]", StringComparison.Ordinal)
                && codexToml.Contains("JrmMcp.exe", StringComparison.Ordinal)
                && codexToml.Contains("--connection", StringComparison.Ordinal));
            Check(".codex approval mode follows auto-run", codexToml.Contains("default_tools_approval_mode = \"approve\"", StringComparison.Ordinal));
            Check(".grok/config.toml gains the server table",
                grokToml.Contains($"[mcp_servers.{server}]", StringComparison.Ordinal)
                && grokToml.Contains("JrmMcp.exe", StringComparison.Ordinal));
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
                !File.Exists(Path.Combine(project, "CLAUDE.md"))
                && !Directory.Exists(Path.Combine(project, ".grok")));

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

    private static Task<JsonObject> AgentCliLocateCheckAsync(JsonObject args)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"claude: {AgentCliLocator.FindClaude() ?? "(not found)"}");
        sb.AppendLine($"codex: {AgentCliLocator.FindCodex() ?? "(not found)"}");
        sb.AppendLine($"grok: {AgentCliLocator.FindGrok() ?? "(not found)"}");
        if (args["path"]?.GetValue<string>() is { Length: > 0 } path)
            sb.AppendLine($"resolve: {path} -> {AgentCliLocator.ResolveRealPath(path)}");
        return Task.FromResult(ToolText(sb.ToString().TrimEnd()));
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

    private static (string ExePath, IReadOnlyList<string> Arguments, string LoginCommands) BuildMenuProbeShell(
        string scenario)
    {
        if (scenario != "paged")
            return (Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", [], MenuProbeLoginCommands);

        var scriptPath = Path.Combine(DebugInstanceContext.Info.RuntimeTempRoot, "login-menu-pager.ps1");
        Directory.CreateDirectory(Path.GetDirectoryName(scriptPath)!);
        File.WriteAllText(scriptPath, MenuProbePagerScript);
        return (
            "powershell.exe",
            ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath],
            MenuProbePagedLoginCommands);
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
                    shell.Arguments));
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
                        : "--- visible ---\n" + _menuProbeView.DebugVisibleTerminalText));
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
