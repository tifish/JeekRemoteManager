using System;
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
public sealed class SftpSession : IDisposable
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

    /// <summary>
    /// Runs <paramref name="operation"/> against the connected client on a worker
    /// thread. Operations are serialized: SSH.NET's SftpClient is not safe for
    /// concurrent use on one channel, and serializing also keeps a slow transfer
    /// from interleaving with directory listings (transfers get their own session).
    /// </summary>
    public async Task<T> RunAsync<T>(Func<SftpClient, T> operation, CancellationToken cancellationToken = default)
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
                    return operation(client);
                }
                catch (Exception ex) when (ShouldRetryAfterReconnect(ex, client))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    DisposeClient();
                    return operation(EnsureConnected());
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
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
