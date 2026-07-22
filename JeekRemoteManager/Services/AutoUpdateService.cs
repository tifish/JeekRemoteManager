using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JeekTools;

namespace JeekRemoteManager.Services;

/// <summary>
/// App-specific configuration over the generic <see cref="AutoUpdater"/> in
/// JeekTools. See that class for how checking, staging, and installing work.
/// </summary>
public static class AutoUpdateService
{
    private static readonly AutoUpdater Updater = new(new AutoUpdaterOptions
    {
        AppExeName = "JeekRemoteManager.exe",
        ReleaseZipUrl = "https://github.com/tifish/JeekRemoteManager/releases/download/latest_release/JeekRemoteManager.zip",
        VersionTxtUrl = "https://github.com/tifish/JeekRemoteManager/releases/download/latest_release/version.txt",
        UserAgent = "JeekRemoteManager-Updater/1.0",
        // Debug instances never self-update, and parallel worktree instances
        // stage into isolated temp roots so they never fight over files.
        Disabled = DebugInstanceContext.IsDebugBuild,
        TempRoot = DebugInstanceContext.IsDebugBuild ? DebugInstanceContext.RuntimeTempRoot : null,
    });

    public static string DownloadUrl => Updater.DownloadUrl;
    public static IReadOnlyList<string> DownloadUrls => Updater.DownloadUrls;
    public static int LocalCommitCount => Updater.LocalVersion;
    public static int RemoteCommitCount => Updater.RemoteVersion;
    public static string FailureReason => Updater.FailureReason;

    public static IReadOnlyList<string> GetDefaultDownloadUrls() => Updater.GetDefaultDownloadUrls();

    public static int GetLocalCommitCount() => Updater.GetLocalVersion();

    public static Task<UpdateCheckOutcome> HasUpdateAsync() => Updater.HasUpdateAsync();

    public static Task<string?> DownloadAndStageAsync(
        IReadOnlyList<string>? urls = null,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => Updater.DownloadAndStageAsync(urls, progress, cancellationToken);

    public static bool LaunchInstall(string stagedPackageDir) => Updater.LaunchInstall(stagedPackageDir);
}
