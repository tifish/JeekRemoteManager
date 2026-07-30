using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JeekRemoteManager.Models;

namespace JeekRemoteManager.Services;

/// <summary>
/// One file transfer for <c>file_upload</c> / <c>file_download</c> MCP tools.
/// Upload: <paramref name="Sources"/> are local Windows files, <paramref name="Destination"/>
/// is a remote directory (null = the shell's current directory). Download: sources are remote
/// files, destination is a local directory (null = the user's Downloads folder).
/// Transfers share the interactive shell (ZMODEM on SSH) so bastion/jump-host logins still work;
/// there is no separate SFTP channel.
/// </summary>
public sealed record AgentFileTransfer(bool IsUpload, IReadOnlyList<string> Sources, string? Destination);

/// <summary>Terminal recovery operations the assistant can request explicitly.</summary>
public enum AgentTerminalAction
{
    ForceInterrupt,
    Reconnect,
}

/// <summary>
/// Remote-terminal capabilities exposed to agent CLIs through the product MCP server.
/// Implementations run on the owning <c>TerminalView</c> and share the interactive SSH/WSL shell.
/// </summary>
public interface IAgentRemoteTools
{
    string ConnectionLabel { get; }

    bool IsWsl { get; }

    /// <summary>
    /// Runs a command on the shared interactive shell. Optional
    /// <paramref name="timeoutSeconds"/> aborts with interrupt when exceeded
    /// (null = no product-side timeout).
    /// </summary>
    Task<string> RunCommandAsync(
        string command,
        int? timeoutSeconds = null,
        CancellationToken cancellationToken = default);

    Task<string> TransferFilesAsync(AgentFileTransfer transfer, CancellationToken cancellationToken = default);

    Task<string> RunTerminalActionAsync(AgentTerminalAction action, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks the user to approve a dangerous remote command. Returns false when cancelled.
    /// </summary>
    Task<bool> ConfirmDangerousCommandAsync(string command, CancellationToken cancellationToken = default);

    /// <summary>Connection + shell lock/running snapshot (does not acquire the command lock).</summary>
    Task<string> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>Connection metadata safe for the agent (no secrets).</summary>
    Task<string> GetConnectionInfoAsync(CancellationToken cancellationToken = default);

    /// <summary>Last N lines of terminal scrollback / viewport text.</summary>
    Task<string> GetScrollbackAsync(int lines, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes raw text to the live shell without capturing output (e.g. pager keys).
    /// Does not acquire the command lock.
    /// </summary>
    Task<string> SendKeysAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>Latest server-monitor panel snapshot when available.</summary>
    Task<string> GetMonitorSnapshotAsync(CancellationToken cancellationToken = default);
}

/// <summary>Identity of a local agent CLI the AI panel can launch.</summary>
public enum AgentCliKind
{
    Claude,
    Codex,
    Grok,

    /// <summary>Antigravity's terminal agent (<c>agy</c>), Google's replacement for Gemini CLI.</summary>
    Antigravity,

    /// <summary>Antigravity 2.0, the standalone agent-orchestration app, opened on a folder.</summary>
    AntigravityDesktop,

    /// <summary>Editors opened on the workspace folder rather than run as a CLI.</summary>
    VsCode,
    Cursor,
    AntigravityIde,
}

/// <summary>How an agent's non-terminal surface is opened on the workspace folder.</summary>
public enum AgentDesktopLaunch
{
    /// <summary>No desktop surface — CLI-only agents.</summary>
    None,

    /// <summary>A registered URI the shell hands off (<c>claude://</c>, <c>codex://</c>).
    /// Needs no local binary, so the panel never asks the user to install anything.</summary>
    Protocol,

    /// <summary>The app's own executable, given the folder as an argument.</summary>
    Executable,
}

/// <summary>Resolved install path and launch metadata for one agent.</summary>
public sealed record AgentCliDescriptor(
    AgentCliKind Kind,
    string Label,
    string? ExecutablePath,
    string InstallHint,
    bool CanAutoInstall = true)
{
    public bool IsAvailable => !string.IsNullOrWhiteSpace(ExecutablePath);
}

/// <summary>
/// Locates the three supported agent CLIs and builds launch argument lists.
/// Remote-server context and MCP endpoints live in the workspace (<c>AGENTS.md</c>,
/// project MCP configs) — not on the command line.
/// </summary>
public static class AgentCliCatalog
{
    /// <summary>
    /// Remote tools that auto-run mode may allow without extra prompts.
    /// Destructive shell work still goes through terminal_run / terminal_run_danger
    /// (and host-side danger confirmation where applicable).
    /// </summary>
    public static readonly string[] AutoRunSafeToolNames =
    [
        "terminal_run",
        "terminal_run_danger",
        "terminal_interrupt",
        "terminal_reconnect",
        "terminal_status",
        "terminal_scrollback",
        "terminal_send_keys",
        "connection_info",
        "monitor_snapshot",
        "file_upload",
        "file_download",
    ];

    public static IReadOnlyList<AgentCliDescriptor> Discover() =>
    [
        new(AgentCliKind.Claude, "Claude", AgentCliLocator.FindClaude(),
            AgentCliInstaller.GetInstallCommandSummary(AgentCliKind.Claude)),
        new(AgentCliKind.Codex, "Codex", AgentCliLocator.FindCodex(),
            AgentCliInstaller.GetInstallCommandSummary(AgentCliKind.Codex)),
        new(AgentCliKind.Grok, "Grok", AgentCliLocator.FindGrok(),
            AgentCliInstaller.GetInstallCommandSummary(AgentCliKind.Grok)),
        new(AgentCliKind.Antigravity, "Antigravity", AgentCliLocator.FindAntigravityCli(),
            AgentCliInstaller.GetInstallCommandSummary(AgentCliKind.Antigravity)),
        new(AgentCliKind.AntigravityDesktop, "Antigravity 2.0",
            AgentCliLocator.FindAntigravityDesktop(),
            AgentCliInstaller.GetInstallCommandSummary(AgentCliKind.AntigravityDesktop),
            CanAutoInstall: false),
        new(AgentCliKind.VsCode, "VS Code", AgentCliLocator.FindVsCode(),
            AgentCliInstaller.GetInstallCommandSummary(AgentCliKind.VsCode)),
        new(AgentCliKind.Cursor, "Cursor", AgentCliLocator.FindCursor(),
            AgentCliInstaller.GetInstallCommandSummary(AgentCliKind.Cursor)),
        new(AgentCliKind.AntigravityIde, "Antigravity IDE",
            AgentCliLocator.FindAntigravityIde(),
            AgentCliInstaller.GetInstallCommandSummary(AgentCliKind.AntigravityIde),
            CanAutoInstall: false),
    ];

    /// <summary>
    /// Editors, which have exactly one launch shape: open the workspace folder in the editor
    /// window. They are not run in the side panel or Windows Terminal.
    /// </summary>
    public static bool IsIde(AgentCliKind kind) =>
        kind is AgentCliKind.VsCode or AgentCliKind.Cursor or AgentCliKind.AntigravityIde;

    /// <summary>
    /// Whether this agent runs as a terminal program at all. Editors and the Antigravity
    /// desktop app do not — they are windows we point at the workspace folder.
    /// </summary>
    public static bool IsTerminalAgent(AgentCliKind kind) =>
        !IsIde(kind) && kind != AgentCliKind.AntigravityDesktop;

    /// <summary>
    /// The launch modes one agent offers, in picker order. Agents with a single mode make the
    /// picker a label rather than a choice — and that mode is never worth persisting.
    /// </summary>
    public static IReadOnlyList<AgentCliRunMode> RunModesFor(AgentCliKind kind)
    {
        if (IsIde(kind))
            return [AgentCliRunMode.Ide];
        if (kind == AgentCliKind.AntigravityDesktop)
            return [AgentCliRunMode.Desktop];
        return SupportsDesktop(kind)
            ? [AgentCliRunMode.Cli, AgentCliRunMode.WindowsTerminal, AgentCliRunMode.Desktop]
            : [AgentCliRunMode.Cli, AgentCliRunMode.WindowsTerminal];
    }

    /// <summary>
    /// Runtime-only CLI flags. Connection context, system guidance, and MCP URL are
    /// written into the workspace by <see cref="AgentCliWorkspace.Ensure"/> before launch.
    /// </summary>
    public static IReadOnlyList<string> BuildInteractiveArguments(
        AgentCliKind kind,
        bool autoRun = true) =>
        kind switch
        {
            AgentCliKind.Claude => BuildClaudeArguments(autoRun),
            AgentCliKind.Codex => BuildCodexArguments(autoRun),
            AgentCliKind.Grok => BuildGrokArguments(autoRun),
            AgentCliKind.Antigravity => BuildAntigravityArguments(autoRun),
            _ => Array.Empty<string>(),
        };

    /// <summary>
    /// How this agent's desktop surface is opened, if it has one. Claude and Codex register a
    /// URI the shell hands off; Antigravity 2.0 is an app we launch on the folder; everything
    /// else has no desktop surface at all.
    /// </summary>
    public static AgentDesktopLaunch DesktopLaunch(AgentCliKind kind) => kind switch
    {
        AgentCliKind.Claude or AgentCliKind.Codex => AgentDesktopLaunch.Protocol,
        AgentCliKind.AntigravityDesktop => AgentDesktopLaunch.Executable,
        _ => AgentDesktopLaunch.None,
    };

    public static bool SupportsDesktop(AgentCliKind kind) =>
        DesktopLaunch(kind) != AgentDesktopLaunch.None;

    /// <summary>
    /// Builds the registered-protocol URI that opens the workspace in the desktop app.
    /// Claude: <c>claude://code/new?folder=...</c>; Codex: <c>codex://threads/new?path=...</c>.
    /// Returns null when the kind has no desktop protocol.
    /// </summary>
    public static string? BuildDesktopProtocolUri(AgentCliKind kind, string workspacePath)
    {
        if (string.IsNullOrWhiteSpace(workspacePath))
            return null;

        string absolute;
        try
        {
            absolute = Path.GetFullPath(workspacePath);
        }
        catch
        {
            return null;
        }

        var encoded = Uri.EscapeDataString(absolute);
        return kind switch
        {
            AgentCliKind.Claude => $"claude://code/new?folder={encoded}",
            AgentCliKind.Codex => $"codex://threads/new?path={encoded}",
            _ => null,
        };
    }

    private static IReadOnlyList<string> BuildClaudeArguments(bool autoRun)
    {
        // MCP URL + instructions: workspace .mcp.json and AGENTS.md/CLAUDE.md (cwd = workspace).
        if (!autoRun)
            return Array.Empty<string>();

        return
        [
            "--allowedTools",
            string.Join(',', AutoRunSafeToolNames.Select(n => $"mcp__jrm-remote__{n}")),
        ];
    }

    private static IReadOnlyList<string> BuildCodexArguments(bool autoRun)
    {
        // --no-alt-screen: host scrollback/scrollbar (Codex default TUI uses alternate screen).
        // MCP URL + tool approval: workspace .codex/config.toml only.
        // Do not pass `-c mcp_servers.jrm-remote...` here — Codex treats partial MCP server
        // overrides as a new entry without url/command and fails with "invalid transport".
        _ = autoRun; // Applied when rewriting .codex/config.toml (PrepareWorkspace / Ensure).
        return ["--no-alt-screen"];
    }

    private static IReadOnlyList<string> BuildAntigravityArguments(bool autoRun)
    {
        // No per-server auto-approve is documented for Antigravity's .agents/mcp_config.json,
        // and its blanket auto-approve would cover local shell and file writes too — far wider
        // than the remote tools the other agents are granted here. So auto-run adds no flags and
        // the user confirms tool calls in the agent itself.
        _ = autoRun;
        return Array.Empty<string>();
    }

    private static IReadOnlyList<string> BuildGrokArguments(bool autoRun)
    {
        if (!autoRun)
            return Array.Empty<string>();

        var args = new List<string>();
        foreach (var name in AutoRunSafeToolNames)
        {
            args.Add("--allow");
            args.Add($"MCPTool(jrm-remote__{name})");
        }

        return args;
    }
}
