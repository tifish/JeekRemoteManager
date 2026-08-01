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
    public const int ShellOpenTimeoutSeconds = 10;

    private readonly object _gate = new();
    private readonly SemaphoreSlim _shellOpenGate = new(1, 1);
    private readonly ShellChannelCapacityTracker _shellCapacity = new();
    private int _refCount = 1;

    public SharedSshClient(SshClient client)
    {
        Client = client;
        SessionId = Guid.NewGuid().ToString("N")[..8];
    }

    public SshClient Client { get; }

    /// <summary>Non-secret process-local identifier used in pool diagnostics.</summary>
    public string SessionId { get; }

    public int ReferenceCount
    {
        get
        {
            lock (_gate)
                return _refCount;
        }
    }

    public int ActiveShellChannelCount => _shellCapacity.ActiveChannels;

    /// <summary>The observed shell-channel ceiling after one open timed out or the
    /// server explicitly rejected it. Null means this transport has not reached its
    /// capacity yet.</summary>
    public int? KnownShellChannelLimit => _shellCapacity.KnownLimit;

    public bool HasShellChannelCapacity => _shellCapacity.HasCapacity;

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

    /// <summary>
    /// Opens a shell channel with a hard timeout. SSH.NET's synchronous channel-open
    /// call can otherwise wait indefinitely when a bastion has reached its per-
    /// connection channel limit. A channel that completes after the timeout is
    /// disposed immediately instead of leaking.
    /// </summary>
    public async Task<SharedShellStreamLease> CreateShellStreamAsync(
        string terminalType,
        uint columns,
        uint rows,
        uint width,
        uint height,
        int bufferSize,
        CancellationToken cancellationToken = default)
    {
        await _shellOpenGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_shellCapacity.HasCapacity)
            {
                throw new SshChannelCapacityException(
                    _shellCapacity.ActiveChannels,
                    _shellCapacity.KnownLimit ?? _shellCapacity.ActiveChannels,
                    "This SSH transport has no free shell channels.");
            }

            var openTask = Task.Run(
                () => Client.CreateShellStream(
                    terminalType,
                    columns,
                    rows,
                    width,
                    height,
                    bufferSize));
            ShellStream shell;
            try
            {
                shell = await WaitForChannelOpenAsync(
                        openTask,
                        TimeSpan.FromSeconds(ShellOpenTimeoutSeconds),
                        cancellationToken,
                        late => late.Dispose())
                    .ConfigureAwait(false);
            }
            catch (TimeoutException ex)
            {
                var limit = _shellCapacity.RecordObservedLimit();
                throw new SshChannelCapacityException(
                    limit,
                    limit,
                    $"SSH shell channel did not open within {ShellOpenTimeoutSeconds} seconds. "
                    + $"The observed channel limit for this transport is {limit}.",
                    ex);
            }
            catch (Exception ex) when (LooksLikeChannelCapacityFailure(ex))
            {
                var limit = _shellCapacity.RecordObservedLimit();
                throw new SshChannelCapacityException(
                    limit,
                    limit,
                    $"The server rejected another shell channel. "
                    + $"The observed channel limit for this transport is {limit}.",
                    ex);
            }

            _shellCapacity.MarkOpened();
            return new SharedShellStreamLease(this, shell);
        }
        finally
        {
            _shellOpenGate.Release();
        }
    }

    private static bool LooksLikeChannelCapacityFailure(Exception exception)
    {
        var message = exception.GetBaseException().Message;
        return message.Contains("channel", StringComparison.OrdinalIgnoreCase)
               && (message.Contains("administratively prohibited", StringComparison.OrdinalIgnoreCase)
                   || message.Contains("open failed", StringComparison.OrdinalIgnoreCase)
                   || message.Contains("resource shortage", StringComparison.OrdinalIgnoreCase)
                   || message.Contains("maximum", StringComparison.OrdinalIgnoreCase)
                   || message.Contains("limit", StringComparison.OrdinalIgnoreCase));
    }

    internal void ReleaseShellChannel() => _shellCapacity.MarkClosed();

    internal static async Task<T> WaitForChannelOpenAsync<T>(
        Task<T> openTask,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Action<T> disposeLateResult)
    {
        try
        {
            return await openTask.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            _ = openTask.ContinueWith(
                completed =>
                {
                    if (completed.Status == TaskStatus.RanToCompletion)
                        disposeLateResult(completed.Result);
                    else
                        _ = completed.Exception;
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            throw;
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

internal sealed class ShellChannelCapacityTracker
{
    private readonly object _gate = new();
    private int _activeChannels;
    private int? _knownLimit;

    public int ActiveChannels
    {
        get
        {
            lock (_gate)
                return _activeChannels;
        }
    }

    public int? KnownLimit
    {
        get
        {
            lock (_gate)
                return _knownLimit;
        }
    }

    public bool HasCapacity
    {
        get
        {
            lock (_gate)
                return _knownLimit is null || _activeChannels < _knownLimit.Value;
        }
    }

    public void MarkOpened()
    {
        lock (_gate)
            _activeChannels++;
    }

    public void MarkClosed()
    {
        lock (_gate)
            _activeChannels = Math.Max(0, _activeChannels - 1);
    }

    public int RecordObservedLimit()
    {
        lock (_gate)
        {
            _knownLimit = _knownLimit is { } current
                ? Math.Min(current, _activeChannels)
                : _activeChannels;
            return _knownLimit.Value;
        }
    }
}

public sealed class SharedShellStreamLease : IDisposable
{
    private readonly SharedSshClient _owner;
    private int _disposed;

    internal SharedShellStreamLease(SharedSshClient owner, ShellStream stream)
    {
        _owner = owner;
        Stream = stream;
        stream.Closed += (_, _) => ReleaseCapacity();
    }

    public ShellStream Stream { get; }

    public void Dispose()
    {
        try { Stream.Dispose(); } catch { /* ignore */ }
        ReleaseCapacity();
    }

    private void ReleaseCapacity()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _owner.ReleaseShellChannel();
    }
}

public sealed class SshChannelCapacityException : InvalidOperationException
{
    public SshChannelCapacityException(
        int activeChannels,
        int channelLimit,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ActiveChannels = activeChannels;
        ChannelLimit = channelLimit;
    }

    public int ActiveChannels { get; }
    public int ChannelLimit { get; }
}
