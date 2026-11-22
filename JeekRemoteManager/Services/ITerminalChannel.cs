using System;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace JeekRemoteManager.Services;

/// <summary>
/// The byte stream behind a terminal tab: an SSH shell channel or a local
/// ConPTY process. TerminalView talks only to this, so the terminal control,
/// login commands, and script payloads work identically over both.
/// </summary>
public interface ITerminalChannel : IDisposable
{
    /// <summary>Raised on a background thread for every received chunk.</summary>
    event Action<byte[]>? DataReceived;

    /// <summary>Raised on a background thread with a human-readable error.</summary>
    event Action<string>? ErrorMessage;

    /// <summary>Raised once when the channel closes (remote EOF or process exit).</summary>
    event Action? Closed;

    /// <summary>
    /// True when the stream carries bytes unmodified in both directions (SSH).
    /// ConPTY re-synthesizes VT output, so binary protocols like ZMODEM would be
    /// corrupted and must be disabled.
    /// </summary>
    bool SupportsBinaryTransfers { get; }

    void Write(byte[] data);

    void Resize(uint cols, uint rows);
}

/// <summary>SSH shell channel (SSH.NET ShellStream) as a terminal channel.</summary>
public sealed class SshTerminalChannel : ITerminalChannel
{
    private readonly SharedShellStreamLease _lease;
    private readonly ShellStream _shell;
    private readonly object _readGate = new();
    private volatile bool _disposed;

    public SshTerminalChannel(SharedShellStreamLease lease)
    {
        _lease = lease;
        _shell = lease.Stream;
        _shell.ErrorOccurred += OnShellErrorOccurred;
        _shell.Closed += OnShellClosed;
    }

    public event Action<byte[]>? DataReceived;
    public event Action<string>? ErrorMessage;
    public event Action? Closed;

    public bool SupportsBinaryTransfers => true;

    // Drain SSH.NET's ordered read buffer so output produced while the shell
    // request was completing is delivered along with subsequent output.
    internal void StartReading()
    {
        if (_disposed)
            return;
        _shell.DataReceived += OnShellDataReceived;
        _ = Task.Run(DrainReceivedData);
    }

    private void OnShellDataReceived(object? sender, ShellDataEventArgs e) => DrainReceivedData();

    private void OnShellErrorOccurred(object? sender, ExceptionEventArgs e)
    {
        if (!_disposed)
            ErrorMessage?.Invoke(e.Exception.Message);
    }

    private void OnShellClosed(object? sender, EventArgs e)
    {
        if (!_disposed)
            Closed?.Invoke();
    }

    private void DrainReceivedData()
    {
        lock (_readGate)
        {
            if (_disposed)
                return;
            try
            {
                int available;
                while (!_disposed && (available = (int)Math.Min(_shell.Length, 32 * 1024)) > 0)
                {
                    var data = new byte[available];
                    var read = _shell.Read(data, 0, data.Length);
                    if (read == 0)
                        break;
                    if (read != data.Length)
                        Array.Resize(ref data, read);
                    DataReceived?.Invoke(data);
                }
            }
            catch (Exception ex) when (_disposed || ex is ObjectDisposedException or System.IO.IOException)
            {
                // Teardown raced with buffered read; ignore.
            }
        }
    }

    public void Write(byte[] data)
    {
        _shell.Write(data, 0, data.Length);
        _shell.Flush();
    }

    public void Resize(uint cols, uint rows) => _shell.ChangeWindowSize(cols, rows, 0, 0);

    public void Dispose()
    {
        _disposed = true;
        _shell.DataReceived -= OnShellDataReceived;
        _shell.ErrorOccurred -= OnShellErrorOccurred;
        _shell.Closed -= OnShellClosed;
        _lease.Dispose();
    }
}

/// <summary>A local process attached through ConPTY (WSL, agent CLIs, …).</summary>
public sealed class LocalConPtyTerminalChannel : ITerminalChannel
{
    private readonly ConPtySession _session;

    public LocalConPtyTerminalChannel(ConPtySession session)
    {
        _session = session;
        session.DataReceived += data => DataReceived?.Invoke(data);
        session.Exited += _ => Closed?.Invoke();
    }

    public event Action<byte[]>? DataReceived;
    public event Action<string>? ErrorMessage
    {
        add { }
        remove { }
    }
    public event Action? Closed;

    public bool SupportsBinaryTransfers => false;

    public void Write(byte[] data) => _session.Write(data);

    public void Resize(uint cols, uint rows) => _session.Resize((int)cols, (int)rows);

    public void Dispose() => _session.Dispose();
}
