using System;
using System.IO;
using System.Linq;
using System.Text;

namespace JeekRemoteManager.Services;

public sealed class ZmodemTraceLog : IDisposable
{
    private const int DefaultByteLimit = 160;
    private readonly object _gate = new();
    private readonly StreamWriter _writer;
    private bool _disposed;

    private ZmodemTraceLog(string filePath)
    {
        FilePath = filePath;
        _writer = new StreamWriter(new FileStream(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true,
        };
        Write("trace started");
    }

    public string FilePath { get; }

    public static ZmodemTraceLog? CreateIfEnabled()
    {
        var enabled = Environment.GetEnvironmentVariable("JRM_ZMODEM_TRACE");
        if (!IsEnabled(enabled))
            return null;

        return Create();
    }

    private static ZmodemTraceLog Create()
    {
        var dir = Path.Combine(DebugInstanceContext.RuntimeTempRoot, "ZmodemLogs");
        Directory.CreateDirectory(dir);
        var name = $"zmodem-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Environment.ProcessId}-{Guid.NewGuid():N}.log";
        return new ZmodemTraceLog(Path.Combine(dir, name));
    }

    private static bool IsEnabled(string? value) =>
        value is not null
        && (value.Equals("1", StringComparison.OrdinalIgnoreCase)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase));

    public void Write(string message)
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            try
            {
                _writer.WriteLine($"{DateTimeOffset.Now:O} {message}");
            }
            catch (IOException)
            {
                _disposed = true;
            }
            catch (ObjectDisposedException)
            {
                _disposed = true;
            }
        }
    }

    public void WriteException(Exception exception) =>
        Write("EXCEPTION " + exception);

    public void WriteBytes(string label, ReadOnlySpan<byte> bytes, int limit = DefaultByteLimit)
    {
        var take = Math.Min(bytes.Length, limit);
        var hex = BitConverter.ToString(bytes[..take].ToArray());
        var ascii = ToPrintableAscii(bytes[..take]);
        var suffix = bytes.Length > take ? $" ... (+{bytes.Length - take} bytes)" : "";
        Write($"{label} len={bytes.Length} hex={hex}{suffix} ascii=\"{ascii}\"");
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            try
            {
                _writer.WriteLine($"{DateTimeOffset.Now:O} trace ended");
            }
            catch (IOException)
            {
                // Logging must never interfere with terminal shutdown.
            }

            _disposed = true;
            _writer.Dispose();
        }
    }

    private static string ToPrintableAscii(ReadOnlySpan<byte> bytes)
    {
        var chars = bytes
            .ToArray()
            .Select(b => b is >= 0x20 and <= 0x7e ? (char)b : '.')
            .ToArray();
        return new string(chars);
    }
}
