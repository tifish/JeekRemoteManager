using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

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

    /// <summary>Asks the user a free-form or multi-choice question in the UI.</summary>
    Task<string> AskUserAsync(
        string prompt,
        IReadOnlyList<string>? options,
        CancellationToken cancellationToken = default);

    /// <summary>Latest server-monitor panel snapshot when available.</summary>
    Task<string> GetMonitorSnapshotAsync(CancellationToken cancellationToken = default);
}

/// <summary>Identity of a local agent CLI the AI panel can launch.</summary>
public enum AgentCliKind
{
    Claude,
    Codex,
    Grok,
}

/// <summary>Resolved install path and launch metadata for one agent CLI.</summary>
public sealed record AgentCliDescriptor(
    AgentCliKind Kind,
    string Label,
    string? ExecutablePath,
    string InstallHint)
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
        "ask_user",
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
    ];

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
            _ => Array.Empty<string>(),
        };

    /// <summary>
    /// Desktop protocol launch is only implemented for Claude Code and Codex desktop apps.
    /// Grok has no registered workspace protocol here.
    /// </summary>
    public static bool SupportsDesktop(AgentCliKind kind) =>
        kind is AgentCliKind.Claude or AgentCliKind.Codex;

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
