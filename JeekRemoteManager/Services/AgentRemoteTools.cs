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

/// <summary>Identity of a local agent the AI panel can launch.</summary>
public enum AgentCliKind
{
    Claude,
    Codex,
    Grok,
    Copilot,
    OpenCode,
    Pi,
    Omp,

    /// <summary>
    /// Google's replacement for Gemini CLI. One agent shipping three programs — the
    /// <c>agy</c> terminal agent, the 2.0 desktop app, and the IDE — so it is one provider
    /// whose run mode picks the surface.
    /// </summary>
    Antigravity,

    /// <summary>
    /// Cursor ships a terminal agent (<c>agent</c>, formerly <c>cursor-agent</c>) beside the
    /// editor. Both read the same <c>.cursor/mcp.json</c> and the same rules, so this is one
    /// provider whose run mode picks the surface.
    /// </summary>
    Cursor,

    /// <summary>Editors opened on the workspace folder rather than run as a CLI.</summary>
    VsCode,
    Zed,
}

/// <summary>
/// Which of an agent's programs backs a run mode. CLI and Windows Terminal are the same
/// program in two windows, so they share one surface.
/// </summary>
public enum AgentSurfaceKind
{
    Terminal,
    Desktop,
    Ide,
}

/// <summary>How an agent's desktop surface is opened on the workspace folder.</summary>
public enum AgentDesktopLaunch
{
    /// <summary>No desktop surface at all.</summary>
    None,

    /// <summary>A registered URI (or official web-to-app launcher) handed to the shell.</summary>
    Protocol,

    /// <summary>The app's own executable, given the folder as an argument.</summary>
    Executable,
}

/// <summary>One launchable surface of an agent: the program behind it, and how to get it.</summary>
public sealed record AgentSurface(
    string? ExecutablePath,
    string InstallHint,
    bool CanAutoInstall = true,
    bool IsAvailableWithoutExecutable = false)
{
    public bool IsAvailable =>
        IsAvailableWithoutExecutable || !string.IsNullOrWhiteSpace(ExecutablePath);
}

/// <summary>
/// One agent in the provider picker, with a surface per run mode it offers. Agents that ship
/// several programs (Antigravity) stay a single provider — the run-mode picker chooses which
/// program runs, so the user picks the agent first and the surface second.
/// </summary>
public sealed record AgentCliDescriptor(
    AgentCliKind Kind,
    string Label,
    IReadOnlyDictionary<AgentSurfaceKind, AgentSurface> Surfaces)
{
    /// <summary>The surface backing <paramref name="mode"/>, or null when the agent has none.</summary>
    public AgentSurface? SurfaceFor(AgentCliRunMode mode) =>
        Surfaces.GetValueOrDefault(AgentCliCatalog.SurfaceKindFor(mode));

    /// <summary>Whether any surface is installed — used to preselect an agent in the picker.</summary>
    public bool IsAvailable => Surfaces.Values.Any(surface => surface.IsAvailable);
}

/// <summary>
/// Locates every supported agent surface and builds launch argument lists.
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

    private static readonly object DiscoveryGate = new();
    private static IReadOnlyList<AgentCliDescriptor>? _cachedDiscovery;

    /// <summary>
    /// The installed agents and their surfaces. Cached for the process: a probe walks
    /// PATH for every tool plus the registry, which is hundreds of synchronous lookups,
    /// and it ran twice on the UI thread every time an AI panel was opened. Installing
    /// an agent is the only thing that changes the answer, so that path calls
    /// <see cref="Rediscover"/>.
    /// </summary>
    public static IReadOnlyList<AgentCliDescriptor> Discover()
    {
        lock (DiscoveryGate)
            return _cachedDiscovery ??= Probe();
    }

    /// <summary>Re-probes the disk and replaces the cached result.</summary>
    public static IReadOnlyList<AgentCliDescriptor> Rediscover()
    {
        lock (DiscoveryGate)
            return _cachedDiscovery = Probe();
    }

    private static IReadOnlyList<AgentCliDescriptor> Probe() =>
    [
        new(AgentCliKind.Claude, "Claude", new Dictionary<AgentSurfaceKind, AgentSurface>
        {
            [AgentSurfaceKind.Terminal] = Surface(
                AgentCliKind.Claude, AgentSurfaceKind.Terminal, AgentCliLocator.FindClaude()),
            [AgentSurfaceKind.Desktop] = Surface(
                AgentCliKind.Claude, AgentSurfaceKind.Desktop,
                AgentCliLocator.FindProtocolHandler("claude")),
        }),
        new(AgentCliKind.Codex, "Codex", new Dictionary<AgentSurfaceKind, AgentSurface>
        {
            [AgentSurfaceKind.Terminal] = Surface(
                AgentCliKind.Codex, AgentSurfaceKind.Terminal, AgentCliLocator.FindCodex()),
            // The current official entry point is `codex app [PATH]`. The CLI opens the app
            // and starts its installer when the app is missing.
            [AgentSurfaceKind.Desktop] = Surface(
                AgentCliKind.Codex, AgentSurfaceKind.Desktop, AgentCliLocator.FindCodex()),
        }),
        Terminal(AgentCliKind.Grok, "Grok", AgentCliLocator.FindGrok()),
        new(AgentCliKind.Copilot, "GitHub Copilot", new Dictionary<AgentSurfaceKind, AgentSurface>
        {
            [AgentSurfaceKind.Terminal] = Surface(
                AgentCliKind.Copilot, AgentSurfaceKind.Terminal, AgentCliLocator.FindCopilot()),
            // GitHub's hosted launcher opens the app when installed and otherwise presents the
            // download page, so this surface is useful without a locally discoverable binary.
            [AgentSurfaceKind.Desktop] = Surface(
                AgentCliKind.Copilot, AgentSurfaceKind.Desktop, path: null,
                isAvailableWithoutExecutable: true),
        }),
        Terminal(AgentCliKind.OpenCode, "OpenCode", AgentCliLocator.FindOpenCode()),
        Terminal(AgentCliKind.Pi, "Pi", AgentCliLocator.FindPi()),
        Terminal(AgentCliKind.Omp, "OMP", AgentCliLocator.FindOmp()),
        new(AgentCliKind.Antigravity, "Antigravity", new Dictionary<AgentSurfaceKind, AgentSurface>
        {
            [AgentSurfaceKind.Terminal] = Surface(
                AgentCliKind.Antigravity, AgentSurfaceKind.Terminal,
                AgentCliLocator.FindAntigravityCli()),
            [AgentSurfaceKind.Desktop] = Surface(
                AgentCliKind.Antigravity, AgentSurfaceKind.Desktop,
                AgentCliLocator.FindAntigravityDesktop()),
            [AgentSurfaceKind.Ide] = Surface(
                AgentCliKind.Antigravity, AgentSurfaceKind.Ide,
                AgentCliLocator.FindAntigravityIde()),
        }),
        new(AgentCliKind.Cursor, "Cursor", new Dictionary<AgentSurfaceKind, AgentSurface>
        {
            [AgentSurfaceKind.Terminal] = Surface(
                AgentCliKind.Cursor, AgentSurfaceKind.Terminal, AgentCliLocator.FindCursorCli()),
            [AgentSurfaceKind.Ide] = Surface(
                AgentCliKind.Cursor, AgentSurfaceKind.Ide, AgentCliLocator.FindCursor()),
        }),
        Editor(AgentCliKind.VsCode, "VS Code", AgentCliLocator.FindVsCode()),
        Editor(AgentCliKind.Zed, "Zed", AgentCliLocator.FindZed()),
    ];

    private static AgentCliDescriptor Terminal(AgentCliKind kind, string label, string? path) =>
        new(kind, label, new Dictionary<AgentSurfaceKind, AgentSurface>
        {
            [AgentSurfaceKind.Terminal] = Surface(kind, AgentSurfaceKind.Terminal, path),
        });

    private static AgentCliDescriptor Editor(AgentCliKind kind, string label, string? path) =>
        new(kind, label, new Dictionary<AgentSurfaceKind, AgentSurface>
        {
            [AgentSurfaceKind.Ide] = Surface(kind, AgentSurfaceKind.Ide, path),
        });

    private static AgentSurface Surface(
        AgentCliKind kind,
        AgentSurfaceKind surface,
        string? path,
        bool isAvailableWithoutExecutable = false) =>
        new(
            path,
            AgentCliInstaller.GetInstallCommandSummary(kind, surface),
            AgentCliInstaller.CanAutoInstall(kind, surface),
            isAvailableWithoutExecutable);

    /// <summary>Which program a run mode starts. CLI and Windows Terminal share one.</summary>
    public static AgentSurfaceKind SurfaceKindFor(AgentCliRunMode mode) => mode switch
    {
        AgentCliRunMode.Desktop => AgentSurfaceKind.Desktop,
        AgentCliRunMode.Ide => AgentSurfaceKind.Ide,
        _ => AgentSurfaceKind.Terminal,
    };

    /// <summary>
    /// The launch modes one agent offers, in picker order. Agents with a single mode make the
    /// picker a label rather than a choice — and that mode is never worth persisting.
    /// </summary>
    public static IReadOnlyList<AgentCliRunMode> RunModesFor(AgentCliKind kind) => kind switch
    {
        AgentCliKind.VsCode or AgentCliKind.Zed => [AgentCliRunMode.Ide],
        AgentCliKind.Cursor =>
            [AgentCliRunMode.Cli, AgentCliRunMode.WindowsTerminal, AgentCliRunMode.Ide],
        AgentCliKind.Antigravity =>
        [
            AgentCliRunMode.Cli,
            AgentCliRunMode.WindowsTerminal,
            AgentCliRunMode.Desktop,
            AgentCliRunMode.Ide,
        ],
        AgentCliKind.Claude or AgentCliKind.Codex or AgentCliKind.Copilot =>
            [AgentCliRunMode.Cli, AgentCliRunMode.WindowsTerminal, AgentCliRunMode.Desktop],
        _ => [AgentCliRunMode.Cli, AgentCliRunMode.WindowsTerminal],
    };

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
            AgentCliKind.Copilot => BuildCopilotArguments(autoRun),
            AgentCliKind.Cursor => BuildCursorArguments(autoRun),
            AgentCliKind.Pi => BuildPiArguments(autoRun),
            AgentCliKind.Antigravity => BuildAntigravityArguments(autoRun),
            _ => Array.Empty<string>(),
        };

    /// <summary>
    /// How this agent's desktop surface is opened, if it has one. Claude registers a URI,
    /// Copilot has an official hosted app launcher, and Codex/Antigravity are launched through
    /// their executables.
    /// </summary>
    public static AgentDesktopLaunch DesktopLaunch(AgentCliKind kind) => kind switch
    {
        AgentCliKind.Claude or AgentCliKind.Copilot => AgentDesktopLaunch.Protocol,
        AgentCliKind.Codex or AgentCliKind.Antigravity => AgentDesktopLaunch.Executable,
        _ => AgentDesktopLaunch.None,
    };

    public static bool SupportsDesktop(AgentCliKind kind) =>
        DesktopLaunch(kind) != AgentDesktopLaunch.None;

    /// <summary>
    /// Builds the registered-protocol URI that opens the workspace in the desktop app.
    /// Claude: <c>claude://code/new?folder=...</c>. Copilot's documented deep links cannot carry
    /// an arbitrary local path, so its official web launcher opens the app home; the generated
    /// workspace is still prepared first. Codex uses <c>codex app [PATH]</c>, not a protocol.
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
            AgentCliKind.Copilot =>
                "https://github.com/copilot/app/launch?open=ghapp%3A%2F%2F",
            _ => null,
        };
    }

    /// <summary>Arguments for executable-backed desktop surfaces.</summary>
    public static IReadOnlyList<string> BuildDesktopArguments(
        AgentCliKind kind,
        string workspacePath)
    {
        if (string.IsNullOrWhiteSpace(workspacePath))
            return Array.Empty<string>();

        return kind switch
        {
            AgentCliKind.Codex => ["app", workspacePath],
            AgentCliKind.Antigravity => [workspacePath],
            _ => Array.Empty<string>(),
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

    private static IReadOnlyList<string> BuildCopilotArguments(bool autoRun)
    {
        if (!autoRun)
            return Array.Empty<string>();

        // Copilot CLI accepts an MCP server name here and grants all tools from that one server.
        // This is narrower than --yolo, which would also auto-approve local shell/file work.
        return ["--allow-tool=jrm-remote"];
    }

    private static IReadOnlyList<string> BuildCursorArguments(bool autoRun)
    {
        if (!autoRun)
            return Array.Empty<string>();

        // Cursor auto-approves MCP servers coming from the user's own global config, but a
        // project-level .cursor/mcp.json — which is what this workspace holds — still has to be
        // approved. The workspace declares exactly one server (ours), so this approves nothing
        // else, and it stays far narrower than --force/--yolo, which would also allow local
        // shell work. Which of that server's tools may then run unattended is decided by the
        // Mcp(jrm-remote:*) permission written into .cursor/cli.json.
        return ["--approve-mcps"];
    }

    private static IReadOnlyList<string> BuildPiArguments(bool autoRun)
    {
        // Upstream Pi deliberately has no built-in MCP client. JeekRemoteManager ships a small
        // first-party extension that reads this workspace's .mcp.json and exposes only that
        // server's tools. Keeping the extension under bin/Data makes it part of the runtime.
        var extension = Path.Combine(
            AppContext.BaseDirectory,
            "Data",
            "AgentSupport",
            "Pi",
            "jrm-mcp.ts");
        return autoRun
            ? ["--extension", extension, "--jrm-auto-run"]
            : ["--extension", extension];
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
