using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace JeekRemoteManager.Services;

/// <summary>
/// Plain-text transcript of one terminal session, written under
/// <c>%LocalAppData%\JeekRemoteManager\SessionLogs</c>. What lands in the file is what the
/// terminal decoded — already in the session's encoding — with escape sequences removed,
/// so the log reads like the screen rather than like a byte dump.
/// </summary>
/// <remarks>
/// Fed from the output frame drain on the UI thread, so a write only appends to a buffered
/// stream; a timer flushes it once a second, off the UI thread, and <see cref="Dispose"/>
/// flushes the rest. Machine-local (never in portable data): transcripts can hold anything
/// a server printed, and they belong to this machine.
/// </remarks>
public sealed class TerminalSessionLog : IDisposable
{
    private readonly object _gate = new();
    private readonly StreamWriter _writer;
    private readonly Timer _flushTimer;
    private readonly AnsiTextStripper _stripper = new();
    private bool _disposed;

    private TerminalSessionLog(string path)
    {
        Path = path;
        _writer = new StreamWriter(
            new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read | FileShare.Delete),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        _flushTimer = new Timer(_ => Flush(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public string Path { get; }

    /// <summary>Folder all transcripts are written to.</summary>
    public static string Folder { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JeekRemoteManager",
        "SessionLogs");

    /// <summary>Starts a new transcript named after the connection and the start time.</summary>
    public static TerminalSessionLog Create(string connectionName, string? folder = null)
    {
        var root = folder ?? Folder;
        Directory.CreateDirectory(root);
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var safe = string.Concat((connectionName ?? "").Select(ch => invalid.Contains(ch) ? '_' : ch)).Trim();
        if (safe.Length == 0)
            safe = "session";
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var path = System.IO.Path.Combine(root, $"{safe}-{stamp}.log");
        for (var i = 2; File.Exists(path); i++)
            path = System.IO.Path.Combine(root, $"{safe}-{stamp}-{i}.log");

        var log = new TerminalSessionLog(path);
        log.WriteHeader(connectionName ?? "");
        return log;
    }

    private void WriteHeader(string connectionName)
    {
        lock (_gate)
            _writer.Write($"# {connectionName} - session log started {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n");
    }

    /// <summary>Appends decoded terminal output; escape sequences are stripped.</summary>
    public void Write(string terminalText)
    {
        if (terminalText.Length == 0)
            return;

        lock (_gate)
        {
            if (_disposed)
                return;
            var plain = _stripper.Strip(terminalText);
            if (plain.Length > 0)
                _writer.Write(plain);
        }
    }

    public void Flush()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            try
            {
                _writer.Flush();
            }
            catch
            {
                // Disk full or the file went away; keep the session running.
            }
        }
    }

    public void Dispose()
    {
        _flushTimer.Dispose();
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            try
            {
                _writer.Write($"\n# session log ended {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n");
                _writer.Dispose();
            }
            catch
            {
                // Best effort.
            }
        }
    }
}

/// <summary>
/// Removes VT escape sequences and control characters from terminal output, keeping text
/// and line breaks. Stateful: an escape sequence split across two chunks is still removed.
/// "\r\n" becomes "\n"; a lone "\r" (a progress bar redrawing its line) is dropped, so the
/// redraws run together on one line instead of producing a line each.
/// </summary>
public sealed class AnsiTextStripper
{
    private enum State
    {
        Text,
        Escape,
        Csi,
        String,
        StringEscape,
        Charset,
    }

    private State _state;
    private bool _pendingCr;

    public string Strip(string input)
    {
        var output = new StringBuilder(input.Length);
        foreach (var ch in input)
        {
            switch (_state)
            {
                case State.Text:
                    if (_pendingCr)
                    {
                        _pendingCr = false;
                        if (ch == '\n')
                        {
                            output.Append('\n');
                            continue;
                        }
                    }

                    switch (ch)
                    {
                        case '\u001b':
                            _state = State.Escape;
                            break;
                        case '\u009b':
                            _state = State.Csi;
                            break;
                        case '\u009d' or '\u0090' or '\u009e' or '\u009f':
                            _state = State.String;
                            break;
                        case '\r':
                            _pendingCr = true;
                            break;
                        case '\n' or '\t':
                            output.Append(ch);
                            break;
                        default:
                            if (!char.IsControl(ch))
                                output.Append(ch);
                            break;
                    }

                    break;

                case State.Escape:
                    _state = ch switch
                    {
                        '[' => State.Csi,
                        // OSC, DCS, SOS, PM, APC: strings terminated by BEL or ST.
                        ']' or 'P' or 'X' or '^' or '_' => State.String,
                        // Character-set designation takes one more byte: ESC ( B.
                        '(' or ')' or '*' or '+' or '-' or '.' or '/' or '#' or '%' => State.Charset,
                        _ => State.Text,
                    };
                    break;

                case State.Csi:
                    // Parameters and intermediates run until a final byte in 0x40-0x7E.
                    if (ch is >= '@' and <= '~')
                        _state = State.Text;
                    break;

                case State.String:
                    if (ch == '\u0007' || ch == '\u009c')
                        _state = State.Text;
                    else if (ch == '\u001b')
                        _state = State.StringEscape;
                    break;

                case State.StringEscape:
                    _state = ch == '\\' ? State.Text : State.String;
                    break;

                case State.Charset:
                    _state = State.Text;
                    break;
            }
        }

        return output.ToString();
    }
}
