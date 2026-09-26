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

/// <summary>AI agent probes: CLI location, workspaces, MCP configs, project links, panels and rendering.</summary>
internal static partial class DebugMcpServer
{
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
                        + $"installing={ai.IsInstalling}");
                    sb.AppendLine(
                        $"terminalVisible={terminal.IsTerminalAreaVisible} "
                        + $"sshTerminalHidden={terminal.IsSshTerminalHidden} "
                        + $"loginInputPending={terminal.IsLoginManualInputPending}");
                    sb.AppendLine($"status={ai.StatusText}");
                    sb.AppendLine($"workspace={ai.WorkingDirectory}");
                    sb.AppendLine($"mcpPipe={ProductMcpServer.PipeName}");
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
                workspace, "vps/bwg", "vps/bwg", "bwg", "SSH", "root@10.0.0.1:22");
            Check("MCP server name is per-connection", link.ProjectMcpServerName == server);

            AgentProjectLink.WriteInto(link, project);

            var agentsMd = File.ReadAllText(Path.Combine(project, "AGENTS.md"));
            var codexToml = File.ReadAllText(Path.Combine(project, ".codex", "config.toml"));
            var grokToml = File.ReadAllText(Path.Combine(project, ".grok", "config.toml"));
            var mcpJson = File.ReadAllText(Path.Combine(project, ".mcp.json"));

            Check("AGENTS.md keeps the project's own text", agentsMd.Contains("Project rules stay here.", StringComparison.Ordinal));
            Check("AGENTS.md gains the reference block", agentsMd.Contains("BEGIN JeekRemoteManager link: vps/bwg", StringComparison.Ordinal));
            Check("reference block names the MCP server", agentsMd.Contains(server, StringComparison.Ordinal));
            Check(
                "reference block points at the portable workspace doc",
                agentsMd.Contains(
                    AgentProjectLink.PortableWorkspaceAgentsPath("vps/bwg"),
                    StringComparison.Ordinal)
                && agentsMd.Contains("%LocalAppData%", StringComparison.Ordinal)
                && !agentsMd.Contains(Environment.UserName, StringComparison.Ordinal));
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
            Check(".mcp.json gains this connection as a portable launcher",
                mcpJson.Contains(server, StringComparison.Ordinal)
                && mcpJson.Contains("\"stdio\"", StringComparison.Ordinal)
                && mcpJson.Contains("\"cmd\"", StringComparison.Ordinal)
                && mcpJson.Contains(AgentMcpConfigCatalog.ProjectLauncherFileName, StringComparison.Ordinal)
                && mcpJson.Contains("--connection", StringComparison.Ordinal)
                && PortableArgsPinThisInstance(mcpJson)
                && !mcpJson.Contains(Environment.UserName, StringComparison.Ordinal));
            Check(".mcp.json entry carries no URL, port, or token",
                JsonNode.Parse(mcpJson)?["mcpServers"]?[server] is JsonObject entry
                && entry["url"] is null
                && entry["command"]?.GetValue<string>() == "cmd"
                && entry["cwd"]?.GetValue<string>() == ".");
            Check(".codex/config.toml keeps existing keys", codexToml.Contains("model = \"gpt-5\"", StringComparison.Ordinal));
            Check(".codex/config.toml gains the server table",
                codexToml.Contains($"[mcp_servers.{server}]", StringComparison.Ordinal)
                && codexToml.Contains("command = \"cmd\"", StringComparison.Ordinal)
                && codexToml.Contains(AgentMcpConfigCatalog.ProjectLauncherFileName, StringComparison.Ordinal)
                && codexToml.Contains("--connection", StringComparison.Ordinal)
                && PortableArgsPinThisInstance(codexToml)
                && !codexToml.Contains(Environment.UserName, StringComparison.Ordinal));
            Check(".codex pre-approves remote MCP tools", codexToml.Contains("default_tools_approval_mode = \"approve\"", StringComparison.Ordinal));
            Check(".grok/config.toml gains the server table",
                grokToml.Contains($"[mcp_servers.{server}]", StringComparison.Ordinal)
                && grokToml.Contains("command = \"cmd\"", StringComparison.Ordinal)
                && grokToml.Contains(AgentMcpConfigCatalog.ProjectLauncherFileName, StringComparison.Ordinal));
            var projectLauncher = File.Exists(
                    Path.Combine(project, AgentMcpConfigCatalog.ProjectLauncherFileName))
                ? File.ReadAllText(Path.Combine(project, AgentMcpConfigCatalog.ProjectLauncherFileName))
                : "";
            Check(
                "portable launcher expands LocalAppData and is not a worktree pin",
                projectLauncher.Contains("%LocalAppData%", StringComparison.Ordinal)
                && projectLauncher.Contains(AgentMcpConfigCatalog.ProjectLauncherMarker, StringComparison.Ordinal)
                && !projectLauncher.Contains("--app", StringComparison.Ordinal)
                && !projectLauncher.Contains(Environment.UserName, StringComparison.Ordinal));

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

            var subset = new[] { ".mcp.json", ".grok/config.toml" };
            AgentProjectLink.WriteInto(link, project, subset);
            Check(
                "selective write keeps only chosen connection agents",
                AgentProjectLink.ListWrittenTargetPaths(project, server).SequenceEqual(subset)
                && !Directory.Exists(Path.Combine(project, ".cursor"))
                && !Directory.Exists(Path.Combine(project, ".vscode")));
            AgentProjectLink.WriteInto(link, project);

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
                && !Directory.Exists(Path.Combine(project, ".grok"))
                && !File.Exists(Path.Combine(project, AgentMcpConfigCatalog.ProjectLauncherFileName)));
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

            var openWorkspaceLabel = Localizer.Get("AiOpenWorkspaceFolder");
            var copyWorkspaceLabel = Localizer.Get("AiCopyWorkspaceFolder");
            var connectionLinkLabel = Localizer.Get("AiLinkProject");
            var (optionHeaders, hasExecutionToggle, copiedWorkspace, copyStatus) = await OnUiAsync(() =>
            {
                var panel = new Views.AgentCliPanelView();
                var vm = new AgentCliPanelViewModel(workspace);
                panel.DataContext = vm;
                var copied = panel.DebugCopyWorkspaceFolder();
                return (panel.OptionsMenuHeaders.ToArray(),
                    panel.FindControl<MenuItem>("AutoRunMenuItem") is not null
                    || panel.FindControl<MenuItem>("AutoApproveMenuItem") is not null,
                    copied,
                    vm.StatusText);
            });
            report.AppendLine("options-menu: " + string.Join(" | ", optionHeaders));
            report.AppendLine("workspace-copy: " + copiedWorkspace);
            Check("AI options menu exposes workspace open", optionHeaders.Contains(openWorkspaceLabel));
            Check("AI options menu exposes workspace copy", optionHeaders.Contains(copyWorkspaceLabel));
            Check(
                "AI workspace copy writes the exact absolute path",
                copiedWorkspace == Path.GetFullPath(workspace)
                && copyStatus.Contains(copiedWorkspace, StringComparison.Ordinal));
            Check("AI options menu exposes connection MCP write", optionHeaders.Contains(connectionLinkLabel));
            Check("AI options menu has no execution approval toggles", !hasExecutionToggle);

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

        string? previousLastDirectory = null;
        IReadOnlyList<string>? previousLastWritten = null;
        Views.McpProjectLinkDialog? dialog = null;
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
                if (main.DataContext is MainWindowViewModel vm)
                {
                    previousLastDirectory = vm.LastMcpProjectDirectory;
                    previousLastWritten = vm.LastMcpWrittenTargetPaths.ToArray();
                }
                main.WriteApplicationMcpToProject(project);
                var tray = (Application.Current as App)?.TrayMenuHeaders.ToArray() ?? [];
                return (main.MoreActionsMenuHeaders.ToArray(), tray);
            });

            var linkLabel = Localizer.Get("AiLinkApplicationProject");
            var unlinkLabel = Localizer.Get("AiUnlinkApplicationProject");
            Check("main menu exposes application-wide write", menuHeaders.Contains(linkLabel));
            Check("main menu no longer exposes a separate unlink item", !menuHeaders.Contains(unlinkLabel));
            Check("tray menu exposes application-wide write", trayHeaders.Contains(linkLabel));
            Check("tray menu no longer exposes a separate unlink item", !trayHeaders.Contains(unlinkLabel));

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
            var launcher = File.Exists(
                    Path.Combine(project, AgentMcpConfigCatalog.ProjectLauncherFileName))
                ? File.ReadAllText(Path.Combine(project, AgentMcpConfigCatalog.ProjectLauncherFileName))
                : "";
            var jsonArgs = (entry?["args"] as JsonArray)?
                .Select(node => node?.GetValue<string>())
                .ToArray() ?? [];

            Check(
                "AGENTS.md describes global application control",
                agentsMd.Contains("BEGIN JeekRemoteManager link: application", StringComparison.Ordinal)
                && agentsMd.Contains("connection_list", StringComparison.Ordinal)
                && agentsMd.Contains("whole application", StringComparison.Ordinal)
                && agentsMd.Contains("%LocalAppData%", StringComparison.Ordinal)
                && !agentsMd.Contains(Environment.UserName, StringComparison.Ordinal));
            Check(
                "JSON config launches the portable project launcher",
                entry?["command"]?.GetValue<string>() == "cmd"
                && jsonArgs.SequenceEqual(
                    AgentMcpConfigCatalog.AdapterLaunch.PortableProject(
                        connectionPath: null,
                        AgentWorkspaceLink.AdapterInstanceId).Arguments)
                && entry["cwd"]?.GetValue<string>() == "."
                && entry["url"] is null);
            Check(
                "Codex config is application-wide and portable",
                codex.Contains(
                    $"[mcp_servers.{AgentProjectLink.ApplicationMcpServerName}]",
                    StringComparison.Ordinal)
                && codex.Contains("command = \"cmd\"", StringComparison.Ordinal)
                && !codex.Contains("--connection", StringComparison.Ordinal)
                && PortableArgsPinThisInstance(codex)
                && !codex.Contains(Environment.UserName, StringComparison.Ordinal));
            Check(
                "portable launcher expands LocalAppData without a username",
                launcher.Contains("%LocalAppData%", StringComparison.Ordinal)
                && launcher.Contains(AgentMcpConfigCatalog.ProjectLauncherMarker, StringComparison.Ordinal)
                && !launcher.Contains("--app", StringComparison.Ordinal)
                && !launcher.Contains(Environment.UserName, StringComparison.Ordinal));
            Check(
                "fixed adapter and current instance registration exist",
                File.Exists(McpAdapterRegistry.AdapterPath)
                && McpAdapterRegistration.IsCurrentInstanceRegistered());
            Check(
                "existing project configuration is preserved",
                root?["mcpServers"]?["other"] is not null
                && codex.Contains("model = \"gpt-5\"", StringComparison.Ordinal)
                && agentsMd.Contains("Existing rules", StringComparison.Ordinal));

            var emptyLast = await OnUiAsync(() =>
            {
                if (Desktop?.MainWindow is not Views.MainWindow main)
                    throw new InvalidOperationException("The main window is not available.");
                if (main.DataContext is not MainWindowViewModel vm)
                    throw new InvalidOperationException("The main window view model is not available.");

                vm.LastMcpProjectDirectory = null;
                vm.LastMcpWrittenTargetPaths = [];
                dialog = main.CreateApplicationMcpLinkDialog();
                dialog.Show(main);
                var emptyDisabled = !dialog.WriteEnabled && !dialog.RemoveAllEnabled
                                    && dialog.TargetDirectoryText.Length == 0;
                var lastUsedDisabled = !dialog.LastUsedEnabled;
                dialog.ClickSelectAll();
                var allCount = dialog.SelectedTargetPaths.Count;
                dialog.ClickSelectNone();
                var noneCount = dialog.SelectedTargetPaths.Count;
                dialog.EnterDirectoryFromTextBox(project);
                return (
                    Title: dialog.Title ?? "",
                    Agents: dialog.AgentLabels.ToArray(),
                    EmptyDisabled: emptyDisabled,
                    AllCount: allCount,
                    NoneCount: noneCount,
                    AutoChecked: dialog.SelectedTargetPaths.ToArray(),
                    ActionsEnabled: dialog.WriteEnabled && dialog.RemoveAllEnabled,
                    LastUsedDisabled: lastUsedDisabled);
            });

            Check("MCP write dialog uses the software title",
                emptyLast.Title == Localizer.Get("McpLinkDialogApplicationTitle"));
            Check(
                "MCP write dialog lists every catalog agent by name",
                emptyLast.Agents.SequenceEqual(
                    AgentMcpConfigCatalog.All
                        .Select(target => target.Label)
                        .OrderBy(label => label, StringComparer.OrdinalIgnoreCase)));
            Check("empty folder disables Write and Remove all", emptyLast.EmptyDisabled);
            Check(
                "Select all / Select none click every agent",
                emptyLast.AllCount == AgentMcpConfigCatalog.All.Count && emptyLast.NoneCount == 0);
            Check("typing an existing folder enables Write and Remove all", emptyLast.ActionsEnabled);
            Check("Last used is disabled before any successful Write", emptyLast.LastUsedDisabled);
            Check("dialog checks agents already written after typing the folder",
                emptyLast.AutoChecked.Contains(".mcp.json", StringComparer.OrdinalIgnoreCase)
                && emptyLast.AutoChecked.Contains(".codex/config.toml", StringComparer.OrdinalIgnoreCase)
                && emptyLast.AutoChecked.Length == AgentMcpConfigCatalog.All.Count);

            var afterSubset = await OnUiAsync(() =>
            {
                if (dialog is null)
                    throw new InvalidOperationException("The MCP write dialog is not open.");
                dialog.ClickSelectNone();
                dialog.SetSelectedTargetPaths([".mcp.json", ".grok/config.toml"]);
                dialog.ClickWrite();
                return (
                    Status: dialog.StatusText,
                    Closed: !dialog.IsVisible);
            });
            dialog = null;

            var remembered = await OnUiAsync(() =>
                Desktop?.MainWindow is Views.MainWindow { DataContext: MainWindowViewModel vm }
                    ? vm.LastMcpProjectDirectory ?? ""
                    : "");
            Check(
                "Write button of a subset leaves only those agents",
                AgentProjectLink.ListWrittenApplicationTargetPaths(project)
                    .SequenceEqual([".mcp.json", ".grok/config.toml"])
                && File.Exists(Path.Combine(project, ".mcp.json"))
                && File.Exists(Path.Combine(project, ".grok", "config.toml"))
                && !Directory.Exists(Path.Combine(project, ".cursor"))
                && !Directory.Exists(Path.Combine(project, ".vscode")));
            Check(
                "Write button reports success",
                afterSubset.Status.Contains(project, StringComparison.Ordinal));
            Check("successful Write closes the dialog", afterSubset.Closed);
            Check("dialog remembers the last project folder", remembered == project);

            var missingDirState = await OnUiAsync(() =>
            {
                if (Desktop?.MainWindow is not Views.MainWindow main)
                    throw new InvalidOperationException("The main window is not available.");
                dialog = main.CreateApplicationMcpLinkDialog();
                dialog.Show(main);
                var prefill = dialog.TargetDirectoryText;
                var restored = dialog.SelectedTargetPaths.ToArray();
                dialog.ClickSelectAll();
                dialog.ClickLastUsed();
                var lastUsed = dialog.SelectedTargetPaths.ToArray();
                dialog.EnterDirectoryFromTextBox(
                    Path.Combine(project, "does-not-exist-" + Guid.NewGuid().ToString("N")[..8]));
                var missing = !dialog.WriteEnabled && !dialog.RemoveAllEnabled
                              && dialog.SelectedTargetPaths.Count == 0;
                dialog.EnterDirectoryFromTextBox(project);
                return (prefill, restored, lastUsed, missing, dialog.SelectedTargetPaths.ToArray());
            });
            Check("dialog prefills the last project folder on open", missingDirState.prefill == project);
            Check(
                "reopening restores the written subset",
                missingDirState.restored.SequenceEqual([".mcp.json", ".grok/config.toml"]));
            Check(
                "Last used restores the agents from the last successful Write",
                missingDirState.lastUsed.SequenceEqual([".mcp.json", ".grok/config.toml"]));
            Check("a missing folder disables actions and clears checks", missingDirState.missing);
            Check(
                "re-entering the project restores the written subset",
                missingDirState.Item5.SequenceEqual([".mcp.json", ".grok/config.toml"]));

            var worktree = Path.Combine(project, "worktree-reject");
            Directory.CreateDirectory(worktree);
            File.WriteAllText(Path.Combine(worktree, "JeekRemoteManagerDebugMcp.cmd"), "@echo off\n");
            var worktreeStatus = await OnUiAsync(() =>
            {
                if (dialog is null)
                    throw new InvalidOperationException("The MCP write dialog is not open.");
                dialog.EnterDirectoryFromTextBox(worktree);
                dialog.ClickSelectAll();
                dialog.ClickWrite();
                return (Status: dialog.StatusText, StillOpen: dialog.IsVisible);
            });
            Check(
                "Write button surfaces a worktree rejection without writing",
                worktreeStatus.Status.Contains("Debug MCP", StringComparison.Ordinal)
                && worktreeStatus.StillOpen
                && !File.Exists(Path.Combine(worktree, AgentMcpConfigCatalog.ProjectLauncherFileName)));

            var afterRemove = await OnUiAsync(() =>
            {
                if (dialog is null)
                    throw new InvalidOperationException("The MCP write dialog is not open.");
                dialog.EnterDirectoryFromTextBox(project);
                dialog.ClickRemoveAll();
                return (Status: dialog.StatusText, Closed: !dialog.IsVisible);
            });
            dialog = null;

            agentsMd = File.ReadAllText(Path.Combine(project, "AGENTS.md"));
            root = TryParseJsonObject(File.ReadAllText(Path.Combine(project, ".mcp.json")));
            codex = File.ReadAllText(Path.Combine(project, ".codex", "config.toml"));
            Check("successful Remove all closes the dialog", afterRemove.Closed);
            Check(
                "Remove all reports success",
                afterRemove.Status.Contains(project, StringComparison.Ordinal));
            Check(
                "unlink removes only JeekRemoteManager's application entry",
                !agentsMd.Contains("JeekRemoteManager link: application", StringComparison.Ordinal)
                && root?["mcpServers"]?[AgentProjectLink.ApplicationMcpServerName] is null
                && root?["mcpServers"]?["other"] is not null
                && !codex.Contains(AgentProjectLink.ApplicationMcpServerName, StringComparison.Ordinal)
                && codex.Contains("model = \"gpt-5\"", StringComparison.Ordinal)
                && !File.Exists(Path.Combine(project, AgentMcpConfigCatalog.ProjectLauncherFileName)));

            await OnUiAsync(() =>
            {
                if (Desktop?.MainWindow is not Views.MainWindow main)
                    throw new InvalidOperationException("The main window is not available.");
                main.WriteApplicationMcpToProject(project);
                return true;
            });
            var cancelLeftFiles = File.Exists(
                Path.Combine(project, AgentMcpConfigCatalog.ProjectLauncherFileName));
            var cancelResult = await OnUiAsync(() =>
            {
                if (Desktop?.MainWindow is not Views.MainWindow main)
                    throw new InvalidOperationException("The main window is not available.");
                var cancelDialog = main.CreateApplicationMcpLinkDialog();
                cancelDialog.Show(main);
                var prefill = cancelDialog.TargetDirectoryText;
                cancelDialog.ClickSelectNone();
                cancelDialog.ClickCancel();
                return (Closed: !cancelDialog.IsVisible, Prefill: prefill);
            });
            Check("Cancel dialog also prefills the last project folder", cancelResult.Prefill == project);
            Check("Cancel closes without writing the cleared selection",
                cancelResult.Closed
                && cancelLeftFiles
                && File.Exists(Path.Combine(project, AgentMcpConfigCatalog.ProjectLauncherFileName))
                && AgentProjectLink.ListWrittenApplicationTargetPaths(project).Count
                    == AgentMcpConfigCatalog.All.Count);

            var connectionWorkspace = Path.Combine(project, "connection-workspace");
            Directory.CreateDirectory(connectionWorkspace);
            var connectionServer = "";
            var connectionTitle = "";
            var connectionWritten = Array.Empty<string>();
            var connectionCleared = Array.Empty<string>();
            AgentCliPanelViewModel? connectionPanel = null;
            var connectionClosed = await OnUiAsync(() =>
            {
                if (Desktop?.MainWindow is not Views.MainWindow main)
                    throw new InvalidOperationException("The main window is not available.");
                if (main.DataContext is not MainWindowViewModel vm)
                    throw new InvalidOperationException("The main window view model is not available.");

                connectionPanel = new AgentCliPanelViewModel(connectionWorkspace);
                connectionPanel.ResolveLinkContext = () => new AgentWorkspaceLink(
                    connectionWorkspace,
                    "vps/bwg",
                    "vps/bwg",
                    "bwg",
                    "SSH",
                    "root@10.0.0.1:22");
                connectionServer = connectionPanel.ResolveLinkContext()!.ProjectMcpServerName;
                var connectionDialog = McpProjectLinkDialog.CreateConnection(connectionPanel, vm);
                connectionDialog.Show(main);
                connectionTitle = connectionDialog.Title ?? "";
                connectionDialog.EnterDirectoryFromTextBox(project);
                connectionDialog.SetSelectedTargetPaths([".mcp.json"]);
                connectionDialog.ClickWrite();
                var writeClosed = !connectionDialog.IsVisible;
                connectionWritten = AgentProjectLink
                    .ListWrittenTargetPaths(project, connectionServer)
                    .ToArray();
                connectionDialog = McpProjectLinkDialog.CreateConnection(connectionPanel, vm);
                connectionDialog.Show(main);
                connectionDialog.EnterDirectoryFromTextBox(project);
                connectionDialog.ClickRemoveAll();
                var removeClosed = !connectionDialog.IsVisible;
                connectionCleared = AgentProjectLink
                    .ListWrittenTargetPaths(project, connectionServer)
                    .ToArray();
                return (writeClosed, removeClosed);
            });
            if (connectionPanel is not null)
                await connectionPanel.DisposeAsync();
            Check(
                "connection dialog uses the connection title",
                connectionTitle == Localizer.Get("McpLinkDialogConnectionTitle"));
            Check(
                "connection Write button writes only the checked agent",
                connectionWritten.SequenceEqual([".mcp.json"]) && connectionClosed.writeClosed);
            Check(
                "connection Remove all button clears this connection",
                connectionCleared.Length == 0 && connectionClosed.removeClosed);

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
            await OnUiAsync(() =>
            {
                try { dialog?.Close(); }
                catch { /* already closed */ }
                if (Desktop?.MainWindow is Views.MainWindow { DataContext: MainWindowViewModel vm })
                {
                    vm.LastMcpProjectDirectory = previousLastDirectory;
                    if (previousLastWritten is not null)
                        vm.LastMcpWrittenTargetPaths = previousLastWritten;
                }
                return true;
            });
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
                "product MCP exposes one command tool for each execution mode",
                productToolNames.Contains("terminal_run_batch")
                && productToolNames.Contains("terminal_run")
                && !productToolNames.Contains("terminal_run_danger")
                && !productToolNames.Contains("terminal_run_batch_danger"));

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
                    // A surface with no local executable is either a packaged app reached
                    // through its registered scheme or a hosted web launcher; report which.
                    { IsAvailableWithoutExecutable: true } =>
                        DescribeExecutablelessSurface(descriptor.Kind, kind),
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

    /// <summary>
    /// Reports what each desktop-capable agent would launch for a workspace, without launching
    /// anything: the mechanism the catalog picks right now, and the resulting URI or command
    /// line. Codex is why this exists — <c>codex app [PATH]</c> starts the MSIX app with no
    /// arguments and drops the path, so the workspace has to ride on the deep link instead.
    /// </summary>
    private static Task<JsonObject> AgentDesktopLaunchCheckAsync(JsonObject args)
    {
        var workspace = args["workspace"]?.GetValue<string>() is { Length: > 0 } requested
            ? requested
            : Path.Combine(AgentCliWorkspace.RootPath, "_application");

        var sb = new StringBuilder();
        sb.AppendLine($"workspace: {workspace}");

        // Rediscover so a desktop app installed since the last probe is reported as installed.
        foreach (var descriptor in AgentCliCatalog.Rediscover())
        {
            if (!AgentCliCatalog.RunModesFor(descriptor.Kind).Contains(AgentCliRunMode.Desktop))
                continue;

            var launch = AgentCliCatalog.DesktopLaunch(descriptor.Kind);
            var surface = descriptor.Surfaces.GetValueOrDefault(AgentSurfaceKind.Desktop);
            var plan = (launch, surface) switch
            {
                // Nothing to plan for an agent whose desktop app is not installed; the panel
                // shows its install hint instead of launching.
                (_, null or { IsAvailable: false }) => "not installed",
                (AgentDesktopLaunch.Protocol, _) =>
                    AgentCliCatalog.BuildDesktopProtocolUri(descriptor.Kind, workspace)
                    ?? "FAIL: protocol launch with no URI",
                (AgentDesktopLaunch.Executable, { ExecutablePath: { Length: > 0 } exe }) =>
                    $"{exe} {string.Join(
                        ' ',
                        AgentCliCatalog.BuildDesktopArguments(descriptor.Kind, workspace))}",
                (AgentDesktopLaunch.Executable, _) => "FAIL: executable launch with no executable",
                _ => "FAIL: offers a desktop run mode with no launch mechanism",
            };
            sb.AppendLine(
                $"{descriptor.Label}: launch={launch} available={surface?.IsAvailable == true} -> {plan}");
        }

        foreach (var scheme in new[] { "claude", "codex" })
            sb.AppendLine($"scheme {scheme}: registered={AgentCliLocator.IsUriSchemeRegistered(scheme)}");

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
                        && AgentCliCatalog.RemoteToolNames.All(tool =>
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
}
