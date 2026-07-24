using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
/// App-specific configuration over the generic <see cref="DebugMcpHost"/> in
/// JeekTools: object-graph roots (App/Desktop/MainWindow/MainVm), '#Name'
/// visual-tree lookup, the Avalonia tools (visual_tree, screenshot), the app
/// probe tools, and the instance discovery file. Compiled into all
/// configurations so Debug and Release behave identically, but the listener
/// only starts in Debug builds. Registered for agents through the repo MCP
/// bridge (port overridable via JRM_MCP_PORT).
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

    private static readonly DebugMcpHost Host = CreateHost();

    public static void Start() => Host.Start();

    public static void Stop() => Host.Stop();

    public static void RefreshDiscovery()
    {
        if (Host.Url.Length > 0)
            WriteDiscovery();
    }

    private static DebugMcpHost CreateHost()
    {
        var host = new DebugMcpHost(new DebugMcpHostOptions
        {
            ServerName = "jeek-remote-manager-debug",
            ServerTitle = "JeekRemoteManager Debug Server",
            Graph = Graph,
            GetVersion = () => $"{AutoUpdateService.GetLocalCommitCount()}",
            Enabled = ListeningEnabled,
            DefaultPort = 8737,
            PortEnvironmentVariable = "JRM_MCP_PORT",
            PortMutexPrefix = "JeekRemoteManager.DebugMcp.Port.",
            UiInvoker = func => Dispatcher.UIThread.InvokeAsync(func).GetTask()
                .WaitAsync(TimeSpan.FromSeconds(15)),
            Describe = BuildDescribeText,
            ToolListProvider = DebugMcpContract.BuildToolList,
            UrlChanged = OnUrlChanged,
        });

        host.AddTool("visual_tree", VisualTreeAsync);
        host.AddTool("screenshot", _ => ScreenshotAsync());
        host.AddTool("ai_runtime_snapshot", _ => AiRuntimeSnapshotAsync());
        host.AddTool("terminal_tab_focus_check", _ => TerminalTabFocusCheckAsync());
        host.AddTool("ai_cli_ctrl_c_check", _ => AiCliCtrlCCheckAsync());
        host.AddTool("agent_cli_locate_check", AgentCliLocateCheckAsync);
        host.AddTool("auto_update_stage_check", AutoUpdateStageCheckAsync);
        return host;
    }

    private static Task<T> OnUiAsync<T>(Func<T> func) => Host.OnUiAsync(func);

    private static JsonObject ToolText(string text, bool isError = false) =>
        DebugMcpHost.ToolText(text, isError);

    #region Discovery

    private static void OnUrlChanged(string url)
    {
        DebugInstanceContext.SetMcpUrl(url);
        if (url.Length > 0)
        {
            WriteDiscovery();
            Log.ZLogInformation($"Debug MCP server listening on {url} for {DebugInstanceContext.InstanceLabel}");
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
                    sb.AppendLine($"mcpUrl={terminal.AgentRemoteMcpUrl ?? "(none)"}");
                    sb.AppendLine(
                        "dangerProbe=" + (terminal.AgentRemoteMcp?.RequiresDangerConfirmation(
                            "rm -rf /tmp/jrm-debug-probe", dangerTagged: false).ToString() ?? "(n/a)"));
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
