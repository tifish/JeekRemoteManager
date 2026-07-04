using Renci.SshNet;

namespace JeekRemoteManager.Services;

/// <summary>
/// A reference-counted wrapper around an authenticated <see cref="SshClient"/> so
/// several terminal tabs can share one SSH transport. SSH multiplexes independent
/// session channels over a single authenticated connection, so a duplicated tab
/// opens a new shell channel here instead of reconnecting and re-authenticating.
/// The last holder to <see cref="Release"/> disconnects and disposes the client.
/// </summary>
public sealed class SharedSshClient
{
    private readonly object _gate = new();
    private int _refCount = 1;

    public SharedSshClient(SshClient client) => Client = client;

    public SshClient Client { get; }

    public bool IsConnected
    {
        get
        {
            try
            {
                return Client.IsConnected;
            }
            catch
            {
                // Disposed or in a broken state — either way, not usable.
                return false;
            }
        }
    }

    /// <summary>Takes an additional reference. Fails only when the last holder
    /// already released (the client is disposed or about to be).</summary>
    public bool TryAddRef()
    {
        lock (_gate)
        {
            if (_refCount <= 0)
                return false;
            _refCount++;
            return true;
        }
    }

    /// <summary>Drops one reference; the final release tears the connection down.</summary>
    public void Release()
    {
        lock (_gate)
        {
            if (_refCount <= 0 || --_refCount > 0)
                return;
        }

        try { Client.Disconnect(); } catch { /* ignore */ }
        try { Client.Dispose(); } catch { /* ignore */ }
    }
}
