using System;
using System.Diagnostics;
using System.IO;
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

/// <summary>
/// Checks GitHub Releases for a newer build and, if confirmed, launches the
/// PowerShell updater that replaces the install on disk and restarts the app.
/// The build number is the commit count, baked in by CI as the assembly's
/// major version.
///
/// Downloads are routed through the fastest reachable GitHub mirror (see
/// <see cref="GitHubMirrors"/>) so updates keep working where github.com is blocked.
/// </summary>
public static class AutoUpdateService
{
    private static readonly ILogger Log = LogManager.CreateLogger(nameof(AutoUpdateService));

    private const string ReleaseZipUrl =
        "https://github.com/tifish/JeekRemoteManager/releases/download/latest_release/JeekRemoteManager.zip";

    private const string VersionTxtUrl =
        "https://github.com/tifish/JeekRemoteManager/releases/download/latest_release/version.txt";

    private const string UpdateScriptName = "AutoUpdate.ps1";

    public static string DownloadUrl { get; private set; } = "";
    public static IReadOnlyList<string> DownloadUrls { get; private set; } = [ReleaseZipUrl];
    public static int LocalCommitCount { get; private set; }
    public static int RemoteCommitCount { get; private set; }
    public static string FailureReason { get; private set; } = "";

    public static async Task<UpdateCheckOutcome> HasUpdateAsync()
    {
        DownloadUrl = ReleaseZipUrl;
        DownloadUrls = [ReleaseZipUrl];
        RemoteCommitCount = 0;
        FailureReason = "";
        LocalCommitCount = GetLocalCommitCount();

        try
        {
            // github.com is unreachable in some regions, so pick the fastest
            // reachable mirror by probing the tiny version.txt, then reuse the
            // same mirror for the much larger release zip.
            GitHubMirrors.ResetFastestMirror();
            var versionUrl = await GitHubMirrors.GetFastestMirror(VersionTxtUrl).ConfigureAwait(false);
            if (string.IsNullOrEmpty(versionUrl))
                versionUrl = VersionTxtUrl;

            var remote = await DownloadTextAsync(versionUrl).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(remote))
                return Fail($"empty version.txt from {versionUrl}");

            if (!int.TryParse(remote.Trim(), out var remoteCount) || remoteCount <= 0)
                return Fail($"version.txt did not contain a positive integer: '{remote.Trim()}'");
            RemoteCommitCount = remoteCount;

            // Reuses the cached fastest-mirror index from the probe above.
            var zipUrl = await GitHubMirrors.GetFastestMirror(ReleaseZipUrl).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(zipUrl))
                DownloadUrl = zipUrl;
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

    public static bool LaunchUpdate()
    {
        try
        {
            if (string.IsNullOrEmpty(DownloadUrl))
                return false;

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

            Log.ZLogInformation($"Launching updater for {DownloadUrl}");
            var updateArguments = new[]
                {
                    "-NoProfile",
                    "-ExecutionPolicy",
                    "Bypass",
                    "-File",
                    scriptPath,
                }
                .Concat(DownloadUrls)
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

    private static string[] BuildDownloadUrls(string preferredUrl)
    {
        return GitHubMirrors.GetMirrors(ReleaseZipUrl)
            .OrderBy(url => string.Equals(url, preferredUrl, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ToArray();
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

    private static async Task<string?> DownloadTextAsync(string url)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("JeekRemoteManager-Updater/1.0");
            using var response = await client.GetAsync(url).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;
            return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }
}
