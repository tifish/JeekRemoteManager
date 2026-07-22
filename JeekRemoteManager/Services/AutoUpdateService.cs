using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using JeekTools;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace JeekRemoteManager.Services;

public enum UpdateCheckOutcome
{
    Available,
    UpToDate,
    Failed,
}

public sealed record UpdateDownloadProgress(
    int MirrorIndex,
    int MirrorCount,
    long ReceivedBytes,
    long? TotalBytes,
    double BytesPerSecond);

/// <summary>
/// Checks GitHub Releases for a newer build, downloads and stages the update
/// package in-app (so a failed download never leaves the user without a running
/// app), and finally hands the verified staged folder to the PowerShell updater
/// that swaps the files on disk and restarts the app.
/// The build number is the commit count, baked in by CI as the assembly's
/// major version.
///
/// Downloads are routed through the fastest reachable GitHub mirror (see
/// <see cref="GitHubMirrors"/>) so updates keep working where github.com is blocked.
/// </summary>
public static class AutoUpdateService
{
    private static readonly ILogger Log = LogManager.CreateLogger(nameof(AutoUpdateService));

    private const string AppExeName = "JeekRemoteManager.exe";

    private const string ReleaseZipUrl =
        "https://github.com/tifish/JeekRemoteManager/releases/download/latest_release/JeekRemoteManager.zip";

    private const string VersionTxtUrl =
        "https://github.com/tifish/JeekRemoteManager/releases/download/latest_release/version.txt";

    private const string UpdateScriptName = "AutoUpdate.ps1";
    private static readonly TimeSpan VersionCheckTimeout = TimeSpan.FromSeconds(5);

    // Mirror-switching policy, ported from the old AutoUpdate.ps1 downloader:
    // abandon a mirror that stalls completely for DownloadIdleTimeout, and — as
    // long as another mirror remains — one that stays below
    // MinimumDownloadBytesPerSecond for a full SlowDownloadWindow.
    private static readonly TimeSpan DownloadIdleTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SlowDownloadWindow = TimeSpan.FromSeconds(10);
    private const long MinimumDownloadBytesPerSecond = 512 * 1024;

    public static string DownloadUrl { get; private set; } = "";
    public static IReadOnlyList<string> DownloadUrls { get; private set; } = [ReleaseZipUrl];
    public static int LocalCommitCount { get; private set; }
    public static int RemoteCommitCount { get; private set; }
    public static string FailureReason { get; private set; } = "";

    public static IReadOnlyList<string> GetDefaultDownloadUrls() => GitHubMirrors.GetMirrors(ReleaseZipUrl);

    public static async Task<UpdateCheckOutcome> HasUpdateAsync()
    {
        DownloadUrl = ReleaseZipUrl;
        DownloadUrls = [ReleaseZipUrl];
        RemoteCommitCount = 0;
        FailureReason = "";
        LocalCommitCount = GetLocalCommitCount();

        if (DebugInstanceContext.IsDebugBuild)
            return Fail("updates are disabled in Debug builds");

        try
        {
            // Race version.txt mirrors directly. The first successful response
            // gives us both the remote version and the preferred release mirror,
            // avoiding a separate probe and a second version.txt request.
            var versionCheck = await DownloadFirstVersionTextAsync().ConfigureAwait(false);
            if (versionCheck is null)
                return Fail("version.txt unavailable or invalid from all mirrors");

            RemoteCommitCount = versionCheck.RemoteCommitCount;

            DownloadUrl = versionCheck.DownloadUrl;
            DownloadUrls = BuildDownloadUrls(DownloadUrl);

            // Treat anything below this as a local dev build — CI bakes in the
            // real commit count, which is always well above this threshold.
            if (LocalCommitCount < 10)
                return Fail("local version unavailable (dev build?)");

            if (RemoteCommitCount > LocalCommitCount)
            {
                Log.ZLogInformation($"Update available: local={LocalCommitCount}, remote={RemoteCommitCount}, url={DownloadUrl}");
                return UpdateCheckOutcome.Available;
            }

            Log.ZLogInformation($"Already up to date: local={LocalCommitCount}, remote={RemoteCommitCount}");
            return UpdateCheckOutcome.UpToDate;
        }
        catch (Exception ex)
        {
            return Fail($"exception: {ex.Message}");
        }
    }

    /// <summary>
    /// Downloads the update package (trying each mirror in order), extracts it
    /// to a staging folder, and verifies it contains the app executable.
    /// Returns the staged package directory, or null with
    /// <see cref="FailureReason"/> set. Safe to call while the app keeps running.
    /// </summary>
    public static async Task<string?> DownloadAndStageAsync(
        IReadOnlyList<string>? urls = null,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var mirrors = (urls ?? DownloadUrls)
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Distinct()
            .ToArray();
        if (mirrors.Length == 0)
        {
            FailureReason = "no download URLs";
            return null;
        }

        // Debug instances get an isolated staging root so parallel worktree
        // instances never fight over the same temp files.
        var tempRoot = DebugInstanceContext.IsDebugBuild
            ? DebugInstanceContext.RuntimeTempRoot
            : Path.GetTempPath();
        var zipPath = Path.Combine(tempRoot, "JeekRemoteManager-update.zip");
        var stageRoot = Path.Combine(tempRoot, "JeekRemoteManager-update");
        var stageDir = Path.Combine(stageRoot, "package");

        try
        {
            TryDelete(() => Directory.Delete(stageRoot, recursive: true));
            TryDelete(() => File.Delete(zipPath));
            Directory.CreateDirectory(stageDir);

            var downloaded = false;
            var lastError = "";
            for (var i = 0; i < mirrors.Length; i++)
            {
                TryDelete(() => File.Delete(zipPath));

                // Give up early on a slow mirror only while a faster fallback
                // is still available; the last mirror may crawl to the finish.
                var minimumSpeed = i < mirrors.Length - 1 ? MinimumDownloadBytesPerSecond : 0;
                try
                {
                    await DownloadFileAsync(mirrors[i], zipPath, minimumSpeed, i, mirrors.Length, progress, cancellationToken)
                        .ConfigureAwait(false);
                    downloaded = true;
                    break;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Log.ZLogWarning($"Update download failed from {mirrors[i]}: {ex.Message}");
                }
            }

            if (!downloaded)
            {
                FailureReason = $"download failed from all mirrors: {lastError}";
                Log.ZLogWarning($"Update download failed: {FailureReason}");
                Cleanup();
                return null;
            }

            ZipFile.ExtractToDirectory(zipPath, stageDir, overwriteFiles: true);
            TryDelete(() => File.Delete(zipPath));

            if (!File.Exists(Path.Combine(stageDir, AppExeName)))
            {
                FailureReason = $"update package is missing {AppExeName}";
                Log.ZLogWarning($"Update download failed: {FailureReason}");
                Cleanup();
                return null;
            }

            Log.ZLogInformation($"Update staged at {stageDir}");
            return stageDir;
        }
        catch (OperationCanceledException)
        {
            FailureReason = "download cancelled";
            Cleanup();
            return null;
        }
        catch (Exception ex)
        {
            FailureReason = ex.Message;
            Log.ZLogError(ex, $"Failed to download and stage update");
            Cleanup();
            return null;
        }

        void Cleanup()
        {
            TryDelete(() => File.Delete(zipPath));
            TryDelete(() => Directory.Delete(stageRoot, recursive: true));
        }
    }

    private static async Task DownloadFileAsync(
        string url,
        string destination,
        long minimumBytesPerSecond,
        int mirrorIndex,
        int mirrorCount,
        IProgress<UpdateDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        // Stalls are policed per-read below, so the client itself never times out.
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("JeekRemoteManager-Updater/1.0");

        using var headerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        headerCts.CancelAfter(DownloadIdleTimeout);
        using var response = await client
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, headerCts.Token)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");

        var totalBytes = response.Content.Headers.ContentLength;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var file = File.Open(destination, FileMode.Create, FileAccess.Write, FileShare.None);

        var buffer = new byte[1024 * 1024];
        long received = 0;
        var stopwatch = Stopwatch.StartNew();
        long windowReceived = 0;
        var speedWindow = Stopwatch.StartNew();
        // Slightly negative so the very first chunk reports immediately.
        var lastReport = TimeSpan.FromMilliseconds(-200);

        while (true)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken)
                    .AsTask()
                    .WaitAsync(DownloadIdleTimeout, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException(
                    $"No download data received for {DownloadIdleTimeout.TotalSeconds:0} seconds.");
            }

            if (read <= 0)
                break;

            await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            received += read;
            windowReceived += read;

            if (minimumBytesPerSecond > 0 && speedWindow.Elapsed >= SlowDownloadWindow)
            {
                var windowBytesPerSecond = windowReceived / speedWindow.Elapsed.TotalSeconds;
                if (windowBytesPerSecond < minimumBytesPerSecond)
                {
                    throw new InvalidOperationException(
                        $"Download speed stayed below {minimumBytesPerSecond / 1024} KB/s " +
                        $"for {SlowDownloadWindow.TotalSeconds:0} seconds " +
                        $"(current: {windowBytesPerSecond / 1024:0} KB/s).");
                }

                windowReceived = 0;
                speedWindow.Restart();
            }

            if (progress != null && (stopwatch.Elapsed - lastReport).TotalMilliseconds >= 200)
            {
                lastReport = stopwatch.Elapsed;
                var speed = received / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.1);
                progress.Report(new UpdateDownloadProgress(mirrorIndex, mirrorCount, received, totalBytes, speed));
            }
        }

        await file.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (totalBytes is > 0 && received < totalBytes)
            throw new InvalidOperationException($"Download ended early: {received} of {totalBytes} bytes.");

        var finalSpeed = received / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.1);
        progress?.Report(new UpdateDownloadProgress(mirrorIndex, mirrorCount, received, totalBytes, finalSpeed));
        Log.ZLogInformation($"Downloaded {received} bytes from {url} in {stopwatch.Elapsed.TotalSeconds:0}s");
    }

    /// <summary>
    /// Launches the PowerShell updater with a staged package produced by
    /// <see cref="DownloadAndStageAsync"/>. The script waits for the app to
    /// exit, swaps the files, and restarts the app.
    /// </summary>
    public static bool LaunchInstall(string stagedPackageDir)
    {
        if (DebugInstanceContext.IsDebugBuild)
            return false;

        try
        {
            if (!File.Exists(Path.Combine(stagedPackageDir, AppExeName)))
            {
                Log.ZLogWarning($"Staged package is missing {AppExeName}: {stagedPackageDir}");
                return false;
            }

            var exePath = Environment.ProcessPath ?? Assembly.GetEntryAssembly()?.Location;
            if (string.IsNullOrEmpty(exePath))
                return false;

            var workDir = Path.GetDirectoryName(exePath);
            if (string.IsNullOrEmpty(workDir))
                return false;

            var scriptPath = Path.Combine(workDir, UpdateScriptName);
            if (!File.Exists(scriptPath))
            {
                Log.ZLogWarning($"Updater script not found: {scriptPath}");
                return false;
            }

            Log.ZLogInformation($"Launching updater for staged package {stagedPackageDir}");
            var updateArguments = new[]
                {
                    "-NoProfile",
                    "-ExecutionPolicy",
                    "Bypass",
                    "-File",
                    scriptPath,
                    stagedPackageDir,
                }
                .Select(QuoteProcessArgument);
            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = string.Join(" ", updateArguments),
                WorkingDirectory = workDir,
                UseShellExecute = true,
            });

            return true;
        }
        catch (Exception ex)
        {
            Log.ZLogError(ex, $"Failed to launch updater");
            return false;
        }
    }

    private static void TryDelete(Action delete)
    {
        try
        {
            delete();
        }
        catch
        {
            // Best-effort cleanup of temp files.
        }
    }

    private static string[] BuildDownloadUrls(string preferredUrl)
    {
        return GitHubMirrors.GetMirrors(ReleaseZipUrl)
            .OrderBy(url => string.Equals(url, preferredUrl, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ToArray();
    }

    private sealed record VersionCheckResult(string DownloadUrl, int RemoteCommitCount);

    private static async Task<VersionCheckResult?> DownloadFirstVersionTextAsync()
    {
        var versionUrls = GitHubMirrors.GetMirrors(VersionTxtUrl);
        var downloadUrls = GitHubMirrors.GetMirrors(ReleaseZipUrl);
        using var cts = new CancellationTokenSource();
        var tasks = versionUrls
            .Select((url, index) => DownloadVersionTextAsync(url, downloadUrls[index], cts.Token))
            .ToList();

        try
        {
            while (tasks.Count > 0)
            {
                var finished = await Task.WhenAny(tasks).ConfigureAwait(false);
                tasks.Remove(finished);
                var result = await finished.ConfigureAwait(false);
                if (result is null)
                    continue;

                cts.Cancel();
                return result;
            }
        }
        finally
        {
            cts.Cancel();
        }

        return null;
    }

    private static async Task<VersionCheckResult?> DownloadVersionTextAsync(
        string versionUrl,
        string downloadUrl,
        CancellationToken cancellationToken)
    {
        var text = await DownloadTextAsync(versionUrl, VersionCheckTimeout, cancellationToken).ConfigureAwait(false);
        if (!int.TryParse(text?.Trim(), out var remoteCount) || remoteCount <= 0)
            return null;

        return new VersionCheckResult(downloadUrl, remoteCount);
    }

    private static string QuoteProcessArgument(string value)
    {
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }

    public static int GetLocalCommitCount()
    {
        try
        {
            return Assembly.GetExecutingAssembly().GetName().Version?.Major ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    private static UpdateCheckOutcome Fail(string reason)
    {
        FailureReason = reason;
        Log.ZLogWarning($"Update check failed: {reason}");
        return UpdateCheckOutcome.Failed;
    }

    private static async Task<string?> DownloadTextAsync(
        string url,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = new HttpClient { Timeout = timeout };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("JeekRemoteManager-Updater/1.0");
            using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;
            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }
}
