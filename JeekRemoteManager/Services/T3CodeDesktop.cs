using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace JeekRemoteManager.Services;

/// <summary>
/// Opens a workspace folder in the T3 Code desktop app. Its <c>t3code:</c> links carry only a
/// prompt, repository, or thread — never a local folder — so the <c>t3 app</c> command the app
/// ships is the one public way in. That command only talks to a desktop app that is already
/// running (over the app's local control pipe), so a cold start launches the app and retries.
/// </summary>
public static partial class T3CodeDesktop
{
    public sealed record OpenResult(bool Success, string? Detail);

    /// <summary>
    /// How long a cold-started app gets to bring up its control pipe. A failed attempt returns
    /// in about half a second, so the retries pace themselves.
    /// </summary>
    private static readonly TimeSpan StartupDeadline = TimeSpan.FromSeconds(45);

    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Upper bound for one <c>t3 app</c> run. The command waits up to 17 s for the app to
    /// answer, which includes creating the project and opening its thread.
    /// </summary>
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(30);

    public static async Task<OpenResult> OpenWorkspaceAsync(
        string workspace,
        CancellationToken cancellationToken)
    {
        var cli = AgentCliLocator.FindT3CodeCli();
        string? lastError = null;
        if (cli is not null)
        {
            var first = await RunAppCommandAsync(cli, workspace, cancellationToken).ConfigureAwait(false);
            if (first.Success)
                return first;
            lastError = first.Detail;
        }

        // Unreachable is by far the common failure, but the CLI's wording is not a contract, so
        // any failure gets the app started. A second instance only reveals the running window.
        var app = AgentCliLocator.FindT3CodeDesktop();
        if (app is null)
            return new OpenResult(false, lastError);

        if (!TryStartApp(app, out var startError))
            return new OpenResult(false, startError);

        var deadline = Environment.TickCount64 + (long)StartupDeadline.TotalMilliseconds;
        while (true)
        {
            await Task.Delay(RetryInterval, cancellationToken).ConfigureAwait(false);

            // The app writes its launcher on startup, so an install never opened before gains
            // one now.
            cli ??= AgentCliLocator.FindT3CodeCli();
            if (cli is not null)
            {
                var attempt = await RunAppCommandAsync(cli, workspace, cancellationToken)
                    .ConfigureAwait(false);
                if (attempt.Success)
                    return attempt;
                lastError = attempt.Detail;
            }

            if (Environment.TickCount64 >= deadline)
                return new OpenResult(false, lastError);
        }
    }

    private static bool TryStartApp(string exePath, out string? error)
    {
        error = null;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = false,
                // Not the workspace: a long-lived process's current directory would keep the
                // folder from being renamed or deleted.
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? "",
            })?.Dispose();
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Runs <c>t3 app</c> with the workspace as its current directory, which the command opens
    /// when given no path. That keeps the folder out of the batch launcher's argument parsing,
    /// where characters such as <c>&amp;</c> or <c>%</c> in a connection name would not survive.
    /// </summary>
    private static async Task<OpenResult> RunAppCommandAsync(
        string cliPath,
        string workspace,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = cliPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workspace,
        };
        psi.ArgumentList.Add("app");
        // The command refuses to run when it believes it is inside an SSH session.
        psi.Environment.Remove("SSH_CONNECTION");
        psi.Environment.Remove("SSH_TTY");

        using var process = new Process { StartInfo = psi };
        try
        {
            if (!process.Start())
                return new OpenResult(false, $"Failed to start {cliPath}.");
        }
        catch (Exception ex)
        {
            return new OpenResult(false, ex.Message);
        }

        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(AttemptTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Already gone.
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new OpenResult(false, "t3 app did not finish in time.");
        }

        var output = (await stdout.ConfigureAwait(false)) + "\n" + (await stderr.ConfigureAwait(false));
        return process.ExitCode == 0
            ? new OpenResult(true, null)
            : new OpenResult(false, SummarizeError(output) ?? $"t3 app exited with code {process.ExitCode}.");
    }

    /// <summary>
    /// The first message line of the CLI's error log, without its timestamp, log level, and
    /// error class name — the stack trace that follows it is no use in a status bar.
    /// </summary>
    public static string? SummarizeError(string output)
    {
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;

            line = LogPrefix().Replace(line, "");
            line = ErrorClassPrefix().Replace(line, "");
            return line.Length > 0 ? line : null;
        }

        return null;
    }

    // "[11:52:50.223] ERROR (#11): "
    [GeneratedRegex(@"^\[[\d:.]+\]\s+[A-Z]+\s+(\(#\d+\):\s*)?")]
    private static partial Regex LogPrefix();

    // "DesktopAppUnreachableError: "
    [GeneratedRegex(@"^[A-Za-z]+Error:\s*")]
    private static partial Regex ErrorClassPrefix();
}
