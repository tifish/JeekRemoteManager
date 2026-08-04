using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace JeekRemoteManager.Services;

/// <summary>One directory entry as the file browser consumes it. Paths are always
/// Unix-style ("/home/user"), whatever the backing store.</summary>
public sealed record FileSystemEntry(
    string Name,
    string FullName,
    bool IsDirectory,
    bool IsSymbolicLink,
    long Length,
    DateTime LastWriteTime,
    string Permissions);

/// <summary>
/// The operations the file browser performs, executed on a session worker thread.
/// Implemented over SFTP for SSH connections and over \\wsl.localhost UNC paths
/// for WSL connections.
/// </summary>
public interface IFileSystemOps
{
    /// <summary>The directory a fresh session starts in (the user's home).</summary>
    string WorkingDirectory { get; }

    IEnumerable<FileSystemEntry> ListDirectory(string path);

    void CreateDirectory(string path);

    void RenameFile(string oldPath, string newPath);

    void DeleteFile(string path);

    void DeleteDirectory(string path);

    bool Exists(string path);

    /// <summary>Only valid when the session's <see cref="IFileSystemSession.SupportsPermissions"/> is true.</summary>
    void ChangePermissions(string path, short mode);

    void UploadFile(Stream source, string remotePath, Action<ulong> progress);

    void DownloadFile(string remotePath, Stream destination, Action<ulong> progress);
}

/// <summary>
/// Whether an operation may be replayed after the session reconnects. Only the caller
/// knows: a listing or a transfer that reopens its streams can simply run again, but a
/// delete or a rename that already took effect server-side — with the transport dying
/// before the reply arrived — comes back as "no such file" on the second attempt and
/// reports a failure for work that actually succeeded.
/// </summary>
public enum FileSystemRetry
{
    /// <summary>Run at most once. The safe default for anything that mutates.</summary>
    Once,

    /// <summary>Safe to run a second time on a fresh connection.</summary>
    Idempotent,
}

/// <summary>
/// One lazily-connected file-system session with all operations serialized
/// through a single queue (see <see cref="SftpSession"/> for why).
/// </summary>
public interface IFileSystemSession : IDisposable
{
    /// <summary>The user's home directory, known after the first operation.</summary>
    string? HomePath { get; }

    /// <summary>False when the backing store cannot read or change Unix permissions
    /// (WSL over UNC) — the browser hides the permissions column and chmod command.</summary>
    bool SupportsPermissions { get; }

    Task<T> RunAsync<T>(
        Func<IFileSystemOps, T> operation,
        FileSystemRetry retry = FileSystemRetry.Once,
        CancellationToken cancellationToken = default);
}
