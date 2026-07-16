using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace JeekRemoteManager.Services;

/// <summary>
/// One file transfer for <c>file_upload</c> / <c>file_download</c> MCP tools.
/// Upload: <paramref name="Sources"/> are local Windows files, <paramref name="Destination"/>
/// is a remote directory (null = the shell's current directory). Download: sources are remote
/// files, destination is a local directory (null = the user's Downloads folder).
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

    Task<string> RunCommandAsync(string command, CancellationToken cancellationToken = default);

    Task<string> TransferFilesAsync(AgentFileTransfer transfer, CancellationToken cancellationToken = default);

    Task<string> RunTerminalActionAsync(AgentTerminalAction action, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks the user to approve a dangerous remote command. Returns false when cancelled.
    /// </summary>
    Task<bool> ConfirmDangerousCommandAsync(string command, CancellationToken cancellationToken = default);
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

    private static IReadOnlyList<string> BuildClaudeArguments(bool autoRun)
    {
        // MCP URL + instructions: workspace .mcp.json and AGENTS.md/CLAUDE.md (cwd = workspace).
        if (!autoRun)
            return Array.Empty<string>();

        return
        [
            "--allowedTools",
            "mcp__jrm-remote__terminal_run,mcp__jrm-remote__terminal_run_danger",
        ];
    }

    private static IReadOnlyList<string> BuildCodexArguments(bool autoRun)
    {
        // --no-alt-screen: host scrollback/scrollbar (Codex default TUI uses alternate screen).
        // MCP URL: workspace .codex/config.toml. Approval is the only optional runtime policy.
        var args = new List<string> { "--no-alt-screen" };
        if (autoRun)
        {
            args.Add("-c");
            args.Add("mcp_servers.jrm-remote.tools.terminal_run.approval_mode=\"approve\"");
            args.Add("-c");
            args.Add("mcp_servers.jrm-remote.tools.terminal_run_danger.approval_mode=\"approve\"");
        }
        else
        {
            args.Add("-c");
            args.Add("mcp_servers.jrm-remote.tools.terminal_run.approval_mode=\"prompt\"");
            args.Add("-c");
            args.Add("mcp_servers.jrm-remote.tools.terminal_run_danger.approval_mode=\"prompt\"");
        }

        return args;
    }

    private static IReadOnlyList<string> BuildGrokArguments(bool autoRun) => autoRun
        ?
        [
            "--allow", "MCPTool(jrm-remote__terminal_run)",
            "--allow", "MCPTool(jrm-remote__terminal_run_danger)",
        ]
        : Array.Empty<string>();
}
