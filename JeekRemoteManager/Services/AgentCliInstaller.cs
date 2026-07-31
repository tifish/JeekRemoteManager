using System;
using System.Diagnostics;
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
            var startInfo = CreateInstallProcessStartInfo(kind, surface);
            var output = await RunProcessInExternalConsoleAsync(
                    startInfo,
                    progress,
                    cancellationToken)
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

    /// <summary>
    /// Builds the visible external PowerShell process used for installation. Output remains in
    /// that console instead of being redirected into the app, so users can follow prompts,
    /// progress, and errors directly.
    /// </summary>
    public static ProcessStartInfo CreateInstallProcessStartInfo(
        AgentCliKind kind,
        AgentSurfaceKind surface)
    {
        if (!CanAutoInstall(kind, surface))
        {
            throw new InvalidOperationException(
                $"{kind} ({surface}) has no install command; it is a download.");
        }

        var command = GetInstallCommandSummary(kind, surface);
        return new ProcessStartInfo
        {
            // A single external shell handles install.ps1, npm, and winget consistently.
            FileName = "powershell.exe",
            Arguments =
                $"-NoProfile -ExecutionPolicy Bypass -Command \"{command.Replace("\"", "\\\"")}\"",
            UseShellExecute = true,
            CreateNoWindow = false,
            WindowStyle = ProcessWindowStyle.Normal,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
    }

    private static async Task<string> RunProcessInExternalConsoleAsync(
        ProcessStartInfo startInfo,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true,
        };

        if (!process.Start())
            throw new InvalidOperationException($"Failed to start {startInfo.FileName}.");

        progress?.Report("Installer opened in an external command window.");
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch { /* already exited */ }
            throw;
        }

        return process.ExitCode == 0
            ? ""
            : $"Installer exited with code {process.ExitCode}. See the external command window for details.";
    }

    private static string Truncate(string text, int max)
    {
        text = text.Trim();
        if (text.Length <= max)
            return text;
        return text[..max] + "…";
    }
}
