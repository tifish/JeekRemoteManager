using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace JeekRemoteManager.Services;

/// <summary>
/// Runs the official preferred one-line installers for agent surfaces that publish one on
/// Windows, then re-probes <see cref="AgentCliLocator"/>. Surfaces whose official guidance
/// prefers a graphical installer show the vendor's download page instead.
/// </summary>
public static class AgentCliInstaller
{
    public sealed record InstallResult(bool Success, string Message, string? ExecutablePath);

    private const string AntigravityDownload = "https://antigravity.google/download";
    private const string ClaudeDownload = "https://claude.com/download";
    private const string CursorDownload = "https://cursor.com/download";
    private const string VsCodeDownload = "https://code.visualstudio.com/Download";

    /// <summary>
    /// Official install line for one surface of an agent, shown to the user before and while it
    /// runs. Surfaces with no published command show a download page instead — see
    /// <see cref="CanAutoInstall"/>.
    /// </summary>
    public static string GetInstallCommandSummary(AgentCliKind kind, AgentSurfaceKind surface) =>
        (kind, surface) switch
        {
            (AgentCliKind.Claude, AgentSurfaceKind.Desktop) => ClaudeDownload,
            (AgentCliKind.Claude, _) => "irm https://claude.ai/install.ps1 | iex",
            (AgentCliKind.Codex, _) =>
                "irm https://chatgpt.com/codex/install.ps1 | iex",
            (AgentCliKind.Grok, _) => "irm https://x.ai/cli/install.ps1 | iex",
            (AgentCliKind.Copilot, AgentSurfaceKind.Terminal) =>
                "winget install GitHub.Copilot",
            (AgentCliKind.Copilot, AgentSurfaceKind.Desktop) =>
                "https://github.com/copilot/app",
            (AgentCliKind.OpenCode, _) => "npm install -g opencode-ai",
            (AgentCliKind.Pi, _) =>
                "npm install -g --ignore-scripts @earendil-works/pi-coding-agent",
            (AgentCliKind.Omp, _) => "irm https://omp.sh/install.ps1 | iex",
            (AgentCliKind.Antigravity, AgentSurfaceKind.Terminal) =>
                "irm https://antigravity.google/cli/install.ps1 | iex",
            // The 2.0 desktop app and the IDE are downloads, not winget packages.
            (AgentCliKind.Antigravity, _) => AntigravityDownload,
            // Both editors recommend their graphical Windows user installer.
            (AgentCliKind.VsCode, _) => VsCodeDownload,
            (AgentCliKind.Cursor, _) => CursorDownload,
            _ => "",
        };

    /// <summary>
    /// Whether the panel can install this surface for the user, or only point at a download page.
    /// </summary>
    public static bool CanAutoInstall(AgentCliKind kind, AgentSurfaceKind surface) =>
        GetInstallCommandSummary(kind, surface) is { Length: > 0 } hint
        && !IsDownloadPage(hint);

    public static bool IsDownloadPage(string? hint) =>
        Uri.TryCreate(hint, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>Opens an official download page through the user's default browser.</summary>
    public static bool TryOpenDownloadPage(string? url, out string error)
    {
        error = "";
        if (!IsDownloadPage(url))
        {
            error = "The download URL is invalid.";
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url!,
                UseShellExecute = true,
            });
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static async Task<InstallResult> InstallAsync(
        AgentCliKind kind,
        AgentSurfaceKind surface,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report($"Running: {GetInstallCommandSummary(kind, surface)}");

        try
        {
            var (fileName, arguments) = BuildProcess(kind, surface);
            var output = await RunProcessAsync(fileName, arguments, progress, cancellationToken)
                .ConfigureAwait(false);

            // PATH may have changed in the child process only; probe known install locations too.
            var path = Locate(kind, surface);
            if (path is not null)
            {
                return new InstallResult(true, $"Installed successfully.\n{path}", path);
            }

            // One more pass after a short delay (some installers finish writing asynchronously).
            await Task.Delay(800, cancellationToken).ConfigureAwait(false);
            path = Locate(kind, surface);
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

    /// <summary>Resolves the executable behind one surface of an agent.</summary>
    public static string? Locate(AgentCliKind kind, AgentSurfaceKind surface) => (kind, surface) switch
    {
        (AgentCliKind.Claude, _) => AgentCliLocator.FindClaude(),
        (AgentCliKind.Codex, _) => AgentCliLocator.FindCodex(),
        (AgentCliKind.Grok, _) => AgentCliLocator.FindGrok(),
        (AgentCliKind.Copilot, AgentSurfaceKind.Terminal) => AgentCliLocator.FindCopilot(),
        (AgentCliKind.OpenCode, _) => AgentCliLocator.FindOpenCode(),
        (AgentCliKind.Pi, _) => AgentCliLocator.FindPi(),
        (AgentCliKind.Omp, _) => AgentCliLocator.FindOmp(),
        (AgentCliKind.Antigravity, AgentSurfaceKind.Terminal) => AgentCliLocator.FindAntigravityCli(),
        (AgentCliKind.Antigravity, AgentSurfaceKind.Desktop) => AgentCliLocator.FindAntigravityDesktop(),
        (AgentCliKind.Antigravity, AgentSurfaceKind.Ide) => AgentCliLocator.FindAntigravityIde(),
        (AgentCliKind.VsCode, _) => AgentCliLocator.FindVsCode(),
        (AgentCliKind.Cursor, _) => AgentCliLocator.FindCursor(),
        _ => null,
    };

    private static (string FileName, string Arguments) BuildProcess(
        AgentCliKind kind,
        AgentSurfaceKind surface)
    {
        if (!CanAutoInstall(kind, surface))
        {
            throw new InvalidOperationException(
                $"{kind} ({surface}) has no install command; it is a download.");
        }

        // Use powershell.exe so install.ps1 scripts and npm.cmd all work on stock Windows.
        return kind switch
        {
            AgentCliKind.Claude => (
                "powershell.exe",
                "-NoProfile -ExecutionPolicy Bypass -Command \"irm https://claude.ai/install.ps1 | iex\""),
            AgentCliKind.Codex => (
                "powershell.exe",
                "-NoProfile -ExecutionPolicy Bypass -Command "
                + "\"irm https://chatgpt.com/codex/install.ps1 | iex\""),
            AgentCliKind.Grok => (
                "powershell.exe",
                "-NoProfile -ExecutionPolicy Bypass -Command \"irm https://x.ai/cli/install.ps1 | iex\""),
            AgentCliKind.Copilot => (
                "winget.exe",
                "install --id GitHub.Copilot --exact --silent "
                + "--accept-package-agreements --accept-source-agreements"),
            AgentCliKind.OpenCode => (
                "powershell.exe",
                "-NoProfile -ExecutionPolicy Bypass -Command \"npm install -g opencode-ai\""),
            AgentCliKind.Pi => (
                "powershell.exe",
                "-NoProfile -ExecutionPolicy Bypass -Command "
                + "\"npm install -g --ignore-scripts @earendil-works/pi-coding-agent\""),
            AgentCliKind.Omp => (
                "powershell.exe",
                "-NoProfile -ExecutionPolicy Bypass -Command "
                + "\"irm https://omp.sh/install.ps1 | iex\""),
            AgentCliKind.Antigravity => (
                "powershell.exe",
                "-NoProfile -ExecutionPolicy Bypass -Command "
                + "\"irm https://antigravity.google/cli/install.ps1 | iex\""),
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
