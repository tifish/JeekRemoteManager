using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace JeekRemoteManager.Services;

/// <summary>
/// Runs the official one-line installers for Claude Code, Codex CLI, and Grok Build
/// on Windows, then re-probes <see cref="AgentCliLocator"/>.
/// </summary>
public static class AgentCliInstaller
{
    public sealed record InstallResult(bool Success, string Message, string? ExecutablePath);

    /// <summary>Official PowerShell/npm install line shown to the user before/while running.</summary>
    public static string GetInstallCommandSummary(AgentCliKind kind) => kind switch
    {
        AgentCliKind.Claude => "irm https://claude.ai/install.ps1 | iex",
        AgentCliKind.Codex => "npm install -g @openai/codex",
        AgentCliKind.Grok => "irm https://x.ai/cli/install.ps1 | iex",
        _ => "",
    };

    public static async Task<InstallResult> InstallAsync(
        AgentCliKind kind,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report($"Running: {GetInstallCommandSummary(kind)}");

        try
        {
            var (fileName, arguments) = BuildProcess(kind);
            var output = await RunProcessAsync(fileName, arguments, progress, cancellationToken)
                .ConfigureAwait(false);

            // PATH may have changed in the child process only; probe known install locations too.
            var path = Locate(kind);
            if (path is not null)
            {
                return new InstallResult(true, $"Installed successfully.\n{path}", path);
            }

            // One more pass after a short delay (some installers finish writing asynchronously).
            await Task.Delay(800, cancellationToken).ConfigureAwait(false);
            path = Locate(kind);
            if (path is not null)
                return new InstallResult(true, $"Installed successfully.\n{path}", path);

            var detail = string.IsNullOrWhiteSpace(output)
                ? "Installer finished but the CLI was not found on PATH or in the usual install folders."
                : "Installer finished but the CLI was not found.\n" + Truncate(output, 1500);
            return new InstallResult(false, detail, null);
        }
        catch (OperationCanceledException)
        {
            return new InstallResult(false, "Installation cancelled.", null);
        }
        catch (Exception ex)
        {
            return new InstallResult(false, ex.Message, null);
        }
    }

    private static string? Locate(AgentCliKind kind) => kind switch
    {
        AgentCliKind.Claude => AgentCliLocator.FindClaude(),
        AgentCliKind.Codex => AgentCliLocator.FindCodex(),
        AgentCliKind.Grok => AgentCliLocator.FindGrok(),
        _ => null,
    };

    private static (string FileName, string Arguments) BuildProcess(AgentCliKind kind)
    {
        // Use powershell.exe so install.ps1 scripts and npm.cmd all work on stock Windows.
        return kind switch
        {
            AgentCliKind.Claude => (
                "powershell.exe",
                "-NoProfile -ExecutionPolicy Bypass -Command \"irm https://claude.ai/install.ps1 | iex\""),
            AgentCliKind.Codex => (
                "powershell.exe",
                "-NoProfile -ExecutionPolicy Bypass -Command \"npm install -g @openai/codex\""),
            AgentCliKind.Grok => (
                "powershell.exe",
                "-NoProfile -ExecutionPolicy Bypass -Command \"irm https://x.ai/cli/install.ps1 | iex\""),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    private static async Task<string> RunProcessAsync(
        string fileName,
        string arguments,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            // Inherit user PATH so npm / irm resolve correctly.
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var log = new StringBuilder();
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Append(string? line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return;
            lock (log)
                log.AppendLine(line);
            progress?.Report(line.Trim());
        }

        process.OutputDataReceived += (_, e) => Append(e.Data);
        process.ErrorDataReceived += (_, e) => Append(e.Data);
        process.Exited += (_, _) => tcs.TrySetResult(process.ExitCode);

        if (!process.Start())
            throw new InvalidOperationException($"Failed to start {fileName}.");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await using (cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
                // ignore
            }

            tcs.TrySetCanceled(cancellationToken);
        }))
        {
            var exit = await tcs.Task.ConfigureAwait(false);
            // Drain a moment for late stderr lines.
            try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
            catch { /* already exited */ }

            lock (log)
            {
                if (exit != 0 && log.Length == 0)
                    log.AppendLine($"Installer exited with code {exit}.");
                else if (exit != 0)
                    log.AppendLine($"(exit code {exit})");
                return log.ToString();
            }
        }
    }

    private static string Truncate(string text, int max)
    {
        text = text.Trim();
        if (text.Length <= max)
            return text;
        return text[..max] + "…";
    }
}
