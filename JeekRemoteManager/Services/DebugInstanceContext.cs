using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace JeekRemoteManager.Services;

public sealed record DebugInstanceInfo(
    bool IsDebugBuild,
    string InstanceId,
    string InstanceLabel,
    string WorkspaceRoot,
    int ProcessId,
    string McpUrl,
    string ConfigRoot,
    string RuntimeTempRoot);

/// <summary>Stable identity and per-process runtime paths for a Debug worktree.</summary>
public static class DebugInstanceContext
{
    public static bool IsDebugBuild { get; } =
#if DEBUG
        true;
#else
        false;
#endif

    public static string WorkspaceRoot { get; } = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, ".."));

    public static string InstanceId { get; } = IsDebugBuild
        ? CreateInstanceId(AppContext.BaseDirectory)
        : "release";

    public static string BranchLabel { get; }
    public static string CommitLabel { get; }
    public static string WorktreeLabel { get; }
    public static string InstanceLabel { get; }

    public static string RuntimeTempRoot { get; } = IsDebugBuild
        ? Path.Combine(Path.GetTempPath(), "JeekRemoteManager", InstanceId)
        : Path.Combine(Path.GetTempPath(), "JeekRemoteManager");

    public static string SingleInstanceMutexName => IsDebugBuild
        ? $"JeekRemoteManager.App.SingleInstance.{InstanceId}"
        : "JeekRemoteManager.App.SingleInstance";

    public static string ActivationEventName => IsDebugBuild
        ? $"JeekRemoteManager.App.Activate.{InstanceId}"
        : "JeekRemoteManager.App.Activate";

    public static string AppUserModelId => IsDebugBuild
        ? $"JeekRemoteManager.App.Debug.{InstanceId}"
        : "JeekRemoteManager.App";

    public static string DiscoveryPath => Path.Combine(AppContext.BaseDirectory, "debug-mcp.json");

    private static string _mcpUrl = "";
    private static string _configRoot = SettingsService.ResolveConfigRoot(Models.StorageLocation.UserDirectory);

    static DebugInstanceContext()
    {
        var git = ReadGitIdentity(WorkspaceRoot);
        BranchLabel = git.Branch;
        CommitLabel = git.Commit;
        WorktreeLabel = git.Worktree;
        InstanceLabel = IsDebugBuild
            ? $"{BranchLabel}@{CommitLabel} / {WorktreeLabel}"
            : "Release";
    }

    public static DebugInstanceInfo Info => new(
        IsDebugBuild,
        InstanceId,
        InstanceLabel,
        WorkspaceRoot,
        Environment.ProcessId,
        _mcpUrl,
        _configRoot,
        RuntimeTempRoot);

    internal static void SetMcpUrl(string value) => _mcpUrl = value ?? "";
    internal static void SetConfigRoot(string value) => _configRoot = Path.GetFullPath(value);

    public static string DecorateTitle(string title) => IsDebugBuild
        ? $"{title} [Debug: {InstanceLabel}]"
        : title;

    public static string CreateInstanceId(string executableDirectory) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            NormalizePath(executableDirectory))))[..12].ToLowerInvariant();

    public static bool IsCurrentExecutable(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            return false;
        var current = Environment.ProcessPath;
        return !string.IsNullOrWhiteSpace(current)
            && string.Equals(Path.GetFullPath(executablePath), Path.GetFullPath(current),
                StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();

    private static (string Branch, string Commit, string Worktree) ReadGitIdentity(string root)
    {
        try
        {
            var dotGit = Path.Combine(root, ".git");
            string gitDir;
            if (Directory.Exists(dotGit))
            {
                gitDir = dotGit;
            }
            else
            {
                var marker = File.ReadAllText(dotGit).Trim();
                if (!marker.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Invalid .git worktree marker.");
                var value = marker[7..].Trim();
                gitDir = Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(root, value));
            }

            var head = File.ReadAllText(Path.Combine(gitDir, "HEAD")).Trim();
            var worktree = Directory.Exists(dotGit)
                ? Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar))
                : Path.GetFileName(gitDir.TrimEnd(Path.DirectorySeparatorChar));

            if (head.StartsWith("ref:", StringComparison.OrdinalIgnoreCase))
            {
                var reference = head[4..].Trim();
                var branch = reference.StartsWith("refs/heads/", StringComparison.Ordinal)
                    ? reference["refs/heads/".Length..]
                    : reference;
                var refPath = Path.Combine(gitDir, reference.Replace('/', Path.DirectorySeparatorChar));
                var commonDirPath = Path.Combine(gitDir, "commondir");
                if (!File.Exists(refPath) && File.Exists(commonDirPath))
                {
                    var common = File.ReadAllText(commonDirPath).Trim();
                    var commonDir = Path.GetFullPath(Path.Combine(gitDir, common));
                    refPath = Path.Combine(commonDir, reference.Replace('/', Path.DirectorySeparatorChar));
                }
                var commit = File.Exists(refPath) ? File.ReadAllText(refPath).Trim() : "unknown";
                return (branch, ShortCommit(commit), worktree);
            }

            return ($"detached-{ShortCommit(head)}", ShortCommit(head), worktree);
        }
        catch
        {
            return ("unknown", "unknown", Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar)));
        }
    }

    private static string ShortCommit(string value) => value.Length >= 7 ? value[..7] : value;
}
