using System;
using Renci.SshNet;

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
    private readonly ShellStream _shell;

    public SshTerminalChannel(ShellStream shell)
    {
        _shell = shell;
        shell.DataReceived += (_, e) => DataReceived?.Invoke(e.Data);
        shell.ErrorOccurred += (_, e) => ErrorMessage?.Invoke(e.Exception.Message);
        shell.Closed += (_, _) => Closed?.Invoke();
    }

    public event Action<byte[]>? DataReceived;
    public event Action<string>? ErrorMessage;
    public event Action? Closed;

    public bool SupportsBinaryTransfers => true;

    public void Write(byte[] data)
    {
        _shell.Write(data, 0, data.Length);
        _shell.Flush();
    }

    public void Resize(uint cols, uint rows) => _shell.ChangeWindowSize(cols, rows, 0, 0);

    public void Dispose()
    {
        try { _shell.Dispose(); } catch { /* ignore */ }
    }
}

/// <summary>A local WSL (ConPTY) process as a terminal channel.</summary>
public sealed class WslTerminalChannel : ITerminalChannel
{
    private readonly ConPtySession _session;

    public WslTerminalChannel(ConPtySession session)
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
