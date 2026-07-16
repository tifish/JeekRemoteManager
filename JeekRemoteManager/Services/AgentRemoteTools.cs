using System;
using System.Collections.Generic;
using System.IO;
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

/// <summary>Locates the three supported agent CLIs and builds launch argument lists.</summary>
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

    public static IReadOnlyList<string> BuildInteractiveArguments(
        AgentCliKind kind,
        string workingDirectory,
        string mcpUrl,
        bool autoRun = true)
    {
        WriteMcpConfig(kind, workingDirectory, mcpUrl);
        return kind switch
        {
            AgentCliKind.Claude =>
            BuildClaudeArguments(workingDirectory, autoRun),
            AgentCliKind.Codex =>
            BuildCodexArguments(mcpUrl, autoRun),
            AgentCliKind.Grok =>
            BuildGrokArguments(autoRun),
            _ => Array.Empty<string>(),
        };
    }

    private static IReadOnlyList<string> BuildClaudeArguments(string workingDirectory, bool autoRun)
    {
        var args = new List<string>
        {
            "--mcp-config",
            Path.Combine(workingDirectory, "jrm-mcp.json"),
            "--strict-mcp-config",
            "--append-system-prompt",
            BuildRemoteSystemPrompt(),
        };
        if (autoRun)
        {
            args.Add("--allowedTools");
            args.Add("mcp__jrm-remote__terminal_run,mcp__jrm-remote__terminal_run_danger");
        }
        return args;
    }

    private static IReadOnlyList<string> BuildCodexArguments(string mcpUrl, bool autoRun) =>
        [
            // Use the normal buffer so host scrollback/scrollbar work. Codex's default
            // alternate-screen TUI keeps history inside the app (unlike Claude/Grok
            // which scroll themselves) and cannot use our terminal scrollbar.
            "--no-alt-screen",
            // Force the per-session HTTP MCP server even if the project config is not trusted yet.
            "-c",
            $"mcp_servers.jrm-remote.url=\"{mcpUrl.Replace("\"", "\\\"", StringComparison.Ordinal)}\"",
            "-c",
            $"mcp_servers.jrm-remote.tools.terminal_run.approval_mode=\"{(autoRun ? "approve" : "prompt")}\"",
            "-c",
            $"mcp_servers.jrm-remote.tools.terminal_run_danger.approval_mode=\"{(autoRun ? "approve" : "prompt")}\"",
        ];

    private static IReadOnlyList<string> BuildGrokArguments(bool autoRun) => autoRun
        ?
        [
            "--allow", "MCPTool(jrm-remote__terminal_run)",
            "--allow", "MCPTool(jrm-remote__terminal_run_danger)",
        ]
        : Array.Empty<string>();

    public static string BuildRemoteSystemPrompt() =>
        "You are assisting inside JeekRemoteManager. The user's remote server is available only " +
        "through the jrm-remote MCP tools (terminal_run, terminal_run_danger, terminal_interrupt, " +
        "terminal_reconnect, file_upload, file_download). Your built-in shell/file tools act on the " +
        "local Windows machine where JeekRemoteManager runs — never confuse them with the remote " +
        "server. Prefer jrm-remote tools for anything that must run on the connected SSH/WSL session.";

    private static void WriteMcpConfig(AgentCliKind kind, string workingDirectory, string mcpUrl)
    {
        Directory.CreateDirectory(workingDirectory);
        switch (kind)
        {
            case AgentCliKind.Claude:
            {
                var path = Path.Combine(workingDirectory, "jrm-mcp.json");
                var json =
                    "{\n" +
                    "  \"mcpServers\": {\n" +
                    "    \"jrm-remote\": {\n" +
                    "      \"type\": \"http\",\n" +
                    $"      \"url\": \"{EscapeJson(mcpUrl)}\"\n" +
                    "    }\n" +
                    "  }\n" +
                    "}\n";
                File.WriteAllText(path, json);
                break;
            }
            case AgentCliKind.Codex:
            {
                var dir = Path.Combine(workingDirectory, ".codex");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "config.toml");
                var toml =
                    "# Generated by JeekRemoteManager — per-session remote tools\n" +
                    "[mcp_servers.jrm-remote]\n" +
                    $"url = \"{EscapeToml(mcpUrl)}\"\n";
                File.WriteAllText(path, toml);
                break;
            }
            case AgentCliKind.Grok:
            {
                var dir = Path.Combine(workingDirectory, ".grok");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "config.toml");
                // Grok project MCP uses [mcp_servers.name] with transport + url for HTTP.
                var toml =
                    "# Generated by JeekRemoteManager — per-session remote tools\n" +
                    "[mcp_servers.jrm-remote]\n" +
                    "transport = \"http\"\n" +
                    $"url = \"{EscapeToml(mcpUrl)}\"\n";
                File.WriteAllText(path, toml);
                break;
            }
        }
    }

    private static string EscapeJson(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private static string EscapeToml(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
}
