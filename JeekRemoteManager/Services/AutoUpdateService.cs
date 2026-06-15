using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;

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
/// </summary>
public static class AutoUpdateService
{
    private const string ReleaseZipUrl =
        "https://github.com/tifish/JeekRemoteManager/releases/download/latest_release/JeekRemoteManager.zip";

    private const string VersionTxtUrl =
        "https://github.com/tifish/JeekRemoteManager/releases/download/latest_release/version.txt";

    private const string UpdateScriptName = "AutoUpdate.ps1";

    public static string DownloadUrl { get; private set; } = "";
    public static int LocalCommitCount { get; private set; }
    public static int RemoteCommitCount { get; private set; }
    public static string FailureReason { get; private set; } = "";

    public static async Task<UpdateCheckOutcome> HasUpdateAsync()
    {
        DownloadUrl = ReleaseZipUrl;
        RemoteCommitCount = 0;
        FailureReason = "";
        LocalCommitCount = GetLocalCommitCount();

        try
        {
            var remote = await DownloadTextAsync(VersionTxtUrl).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(remote))
                return Fail($"empty version.txt from {VersionTxtUrl}");

            if (!int.TryParse(remote.Trim(), out var remoteCount) || remoteCount <= 0)
                return Fail($"version.txt did not contain a positive integer: '{remote.Trim()}'");
            RemoteCommitCount = remoteCount;

            // Treat anything below this as a local dev build — CI bakes in the
            // real commit count, which is always well above this threshold.
            if (LocalCommitCount < 10)
                return Fail("local version unavailable (dev build?)");

            if (RemoteCommitCount > LocalCommitCount)
                return UpdateCheckOutcome.Available;

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
                return false;

            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" \"{DownloadUrl}\"",
                WorkingDirectory = workDir,
                UseShellExecute = true,
            });

            return true;
        }
        catch
        {
            return false;
        }
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
