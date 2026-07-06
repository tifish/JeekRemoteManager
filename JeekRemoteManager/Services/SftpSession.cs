using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace JeekRemoteManager.Services;

/// <summary>
/// One SFTP connection with all operations serialized through a single queue.
/// SSH.NET offers no public way to open an SFTP subsystem channel on an existing
/// <see cref="SshClient"/> transport, so this dials its own connection with the
/// same programmatic credentials — lazily, on the first operation, so a terminal
/// tab that never opens the file browser never pays for it. A dropped connection
/// (idle timeout, server restart) is redialed once per operation before the
/// failure is surfaced.
/// </summary>
public sealed class SftpSession : IFileSystemSession
{
    private readonly Func<ConnectionInfo> _buildConnectionInfo;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SftpClient? _client;
    private volatile bool _disposed;

    public SftpSession(Func<ConnectionInfo> buildConnectionInfo) =>
        _buildConnectionInfo = buildConnectionInfo;

    /// <summary>The remote user's home directory, captured on first connect
    /// (an SFTP session always starts there).</summary>
    public string? HomePath { get; private set; }

    public bool SupportsPermissions => true;

    /// <summary>
    /// Runs <paramref name="operation"/> against the connected client on a worker
    /// thread. Operations are serialized: SSH.NET's SftpClient is not safe for
    /// concurrent use on one channel, and serializing also keeps a slow transfer
    /// from interleaving with directory listings (transfers get their own session).
    /// </summary>
    public async Task<T> RunAsync<T>(Func<IFileSystemOps, T> operation, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                var client = EnsureConnected();
                try
                {
                    return operation(new SftpOps(client));
                }
                catch (Exception ex) when (ShouldRetryAfterReconnect(ex, client))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    DisposeClient();
                    return operation(new SftpOps(EnsureConnected()));
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The browser's operations mapped 1:1 onto SSH.NET's SftpClient.</summary>
    private sealed class SftpOps : IFileSystemOps
    {
        private readonly SftpClient _client;

        public SftpOps(SftpClient client) => _client = client;

        public string WorkingDirectory => _client.WorkingDirectory;

        public IEnumerable<FileSystemEntry> ListDirectory(string path)
        {
            foreach (var file in _client.ListDirectory(path))
            {
                if (file.Name is "." or "..")
                    continue;
                yield return new FileSystemEntry(
                    file.Name,
                    file.FullName,
                    file.IsDirectory,
                    file.IsSymbolicLink,
                    file.Length,
                    file.LastWriteTime,
                    BuildPermissionString(file));
            }
        }

        public void CreateDirectory(string path) => _client.CreateDirectory(path);

        public void RenameFile(string oldPath, string newPath) => _client.RenameFile(oldPath, newPath);

        public void DeleteFile(string path) => _client.DeleteFile(path);

        public void DeleteDirectory(string path) => _client.DeleteDirectory(path);

        public bool Exists(string path) => _client.Exists(path);

        public void ChangePermissions(string path, short mode) => _client.ChangePermissions(path, mode);

        public void UploadFile(Stream source, string remotePath, Action<ulong> progress) =>
            _client.UploadFile(source, remotePath, canOverride: true, progress);

        public void DownloadFile(string remotePath, Stream destination, Action<ulong> progress) =>
            _client.DownloadFile(remotePath, destination, progress);

        private static string BuildPermissionString(Renci.SshNet.Sftp.ISftpFile file)
        {
            Span<char> chars = stackalloc char[10];
            chars[0] = file.IsSymbolicLink ? 'l' : file.IsDirectory ? 'd' : '-';
            chars[1] = file.OwnerCanRead ? 'r' : '-';
            chars[2] = file.OwnerCanWrite ? 'w' : '-';
            chars[3] = file.OwnerCanExecute ? 'x' : '-';
            chars[4] = file.GroupCanRead ? 'r' : '-';
            chars[5] = file.GroupCanWrite ? 'w' : '-';
            chars[6] = file.GroupCanExecute ? 'x' : '-';
            chars[7] = file.OthersCanRead ? 'r' : '-';
            chars[8] = file.OthersCanWrite ? 'w' : '-';
            chars[9] = file.OthersCanExecute ? 'x' : '-';
            return new string(chars);
        }
    }

    private SftpClient EnsureConnected()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_client is { IsConnected: true } live)
            return live;

        DisposeClient();
        var client = new SftpClient(_buildConnectionInfo());
        client.OperationTimeout = TimeSpan.FromSeconds(30);
        client.KeepAliveInterval = TimeSpan.FromSeconds(30);
        try
        {
            client.Connect();
            HomePath ??= client.WorkingDirectory;
        }
        catch
        {
            try { client.Dispose(); } catch { /* ignore */ }
            throw;
        }

        _client = client;
        return client;
    }

    /// <summary>A failure is worth one reconnect+retry only when the transport itself
    /// died mid-operation — never for SFTP-level errors like "no such file".</summary>
    private static bool ShouldRetryAfterReconnect(Exception ex, SftpClient client)
    {
        if (ex is OperationCanceledException)
            return false;
        if (ex is SshConnectionException or SocketException or ObjectDisposedException)
            return true;

        try
        {
            return !client.IsConnected;
        }
        catch
        {
            return true;
        }
    }

    private void DisposeClient()
    {
        var client = _client;
        _client = null;
        if (client is null)
            return;
        try { client.Disconnect(); } catch { /* ignore */ }
        try { client.Dispose(); } catch { /* ignore */ }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        // Tear down on a worker: disposing a live network client can block, and
        // Dispose is called from the UI thread when a tab closes. A concurrent
        // operation will fault and surface through its own error path.
        _ = Task.Run(DisposeClient);
    }
}
