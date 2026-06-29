using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace JeekRemoteManager.Services;

public sealed record InteractiveShellPayload(
    string ReadyMarker,
    string BeginMarker,
    string ExitMarkerPrefix,
    string PayloadDelimiter,
    string PrepareCommand,
    string ExecuteCommand);

public sealed record InteractiveShellPayloadResult(int ExitCode, string Output);

public static class InteractiveShellPayloadRunner
{
    public static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(10);
    public const int EncodedPayloadLineLength = 3000;

    public static InteractiveShellPayload Build(string payload, string? token = null)
    {
        token = NormalizeToken(token);
        var normalizedPayload = NormalizePayload(payload);

        var readyMarker = "__JRM_READY_" + token + "__";
        var beginMarker = "__JRM_BEGIN_" + token + "__";
        var exitMarkerPrefix = "__JRM_EXIT_" + token + "__:";
        var payloadDelimiter = BuildPayloadDelimiter(token);
        var encodedPayload = Convert.ToBase64String(CompressPayload(normalizedPayload));
        var encodedPayloadLines = SplitLines(encodedPayload, EncodedPayloadLineLength);

        var prepareCommand =
            "stty -echo 2>/dev/null || true\n" +
            "printf '\\n%s%s\\n' '__JRM_READY_' '" + token + "__'\n";

        var executeCommand =
            "__jrm_old_ps2=${PS2-}\n" +
            "PS2=\n" +
            "base64 -d <<'" + payloadDelimiter + "' | gzip -dc | { printf '\\n%s%s\\n' '__JRM_BEGIN_' '" + token + "__'; sh -s; }\n" +
            string.Join('\n', encodedPayloadLines) + "\n" +
            payloadDelimiter + "\n" +
            "__jrm_status=$?\n" +
            "PS2=$__jrm_old_ps2\n" +
            "stty echo 2>/dev/null || true\n" +
            "printf '\\n%s%s:%s\\n' '__JRM_EXIT_' '" + token + "__' \"$__jrm_status\"\n";

        return new InteractiveShellPayload(
            readyMarker,
            beginMarker,
            exitMarkerPrefix,
            payloadDelimiter,
            prepareCommand,
            executeCommand);
    }

    public static async Task<InteractiveShellPayloadResult> RunAsync(
        InteractiveShellPayload payload,
        InteractiveShellPayloadMonitor monitor,
        Action<string> write,
        CancellationToken cancellationToken = default)
    {
        write(payload.PrepareCommand);
        await monitor.WaitForReadyAsync(ReadyTimeout, cancellationToken).ConfigureAwait(false);

        write(payload.ExecuteCommand);
        return await monitor.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
    }

    public static string RestoreEchoCommand => "stty echo 2>/dev/null || true\n";

    private static string NormalizeToken(string? token)
    {
        token ??= Guid.NewGuid().ToString("N");
        foreach (var ch in token)
        {
            if (!char.IsAsciiLetterOrDigit(ch) && ch != '_')
                throw new InvalidOperationException("Shell marker token must contain only ASCII letters, digits, or underscores.");
        }

        return token;
    }

    private static string NormalizePayload(string payload)
    {
        var normalized = payload.Replace("\r\n", "\n").Replace('\r', '\n');
        return normalized.EndsWith('\n') ? normalized : normalized + "\n";
    }

    private static string BuildPayloadDelimiter(string token) => "__JRM_PAYLOAD_" + token + "__";

    public static string EncodePayloadForShell(string payload) =>
        Convert.ToBase64String(CompressPayload(NormalizePayload(payload)));

    private static byte[] CompressPayload(string payload)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            var bytes = Encoding.UTF8.GetBytes(payload);
            gzip.Write(bytes, 0, bytes.Length);
        }

        return output.ToArray();
    }

    private static IEnumerable<string> SplitLines(string value, int lineLength) =>
        Enumerable.Range(0, (value.Length + lineLength - 1) / lineLength)
            .Select(i => value.Substring(
                i * lineLength,
                Math.Min(lineLength, value.Length - i * lineLength)));
}

public sealed class InteractiveShellPayloadMonitor
{
    private readonly InteractiveShellPayload _payload;
    private readonly object _gate = new();
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder _output = new();
    private readonly TaskCompletionSource<bool> _ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<InteractiveShellPayloadResult> _exit =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _displayOffset;
    private bool _displayStarted;
    private bool _displayCompleted;

    public InteractiveShellPayloadMonitor(InteractiveShellPayload payload)
    {
        _payload = payload;
    }

    public byte[] Append(byte[] data)
    {
        if (data.Length == 0)
            return Array.Empty<byte>();

        string text;
        string displayText;
        bool markReady;
        InteractiveShellPayloadResult? result;

        lock (_gate)
        {
            var chars = new char[_decoder.GetCharCount(data, 0, data.Length)];
            var count = _decoder.GetChars(data, 0, data.Length, chars, 0);
            text = new string(chars, 0, count);
            _output.Append(text);

            var output = _output.ToString();
            markReady = output.Contains(_payload.ReadyMarker, StringComparison.Ordinal);
            result = TryParseExit(output, out var exitCode)
                ? new InteractiveShellPayloadResult(exitCode, output)
                : null;
            displayText = ExtractDisplayText(output);
        }

        if (markReady)
            _ready.TrySetResult(true);
        if (result is not null)
            _exit.TrySetResult(result);
        return displayText.Length == 0
            ? Array.Empty<byte>()
            : Encoding.UTF8.GetBytes(displayText);
    }

    public void Fail(Exception exception)
    {
        _ready.TrySetException(exception);
        _exit.TrySetException(exception);
    }

    public async Task WaitForReadyAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            await _ready.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException(
                "Interactive shell did not acknowledge the script runner. Make sure the terminal is at a shell prompt.",
                ex);
        }
    }

    public Task<InteractiveShellPayloadResult> WaitForExitAsync(CancellationToken cancellationToken) =>
        _exit.Task.WaitAsync(cancellationToken);

    private bool TryParseExit(string output, out int exitCode)
    {
        exitCode = -1;
        var index = output.LastIndexOf(_payload.ExitMarkerPrefix, StringComparison.Ordinal);
        if (index < 0)
            return false;

        var start = index + _payload.ExitMarkerPrefix.Length;
        var end = start;
        if (end < output.Length && output[end] == '-')
            end++;
        while (end < output.Length && char.IsDigit(output[end]))
            end++;

        if (end <= start || !int.TryParse(output.AsSpan(start, end - start), out exitCode))
            return false;

        return end < output.Length && output[end] is '\r' or '\n';
    }

    private string ExtractDisplayText(string output)
    {
        if (_displayCompleted)
        {
            _displayOffset = output.Length;
            return "";
        }

        if (!_displayStarted)
        {
            var beginIndex = output.IndexOf(_payload.BeginMarker, StringComparison.Ordinal);
            if (beginIndex < 0)
            {
                _displayOffset = output.Length;
                return "";
            }

            _displayStarted = true;
            _displayOffset = beginIndex + _payload.BeginMarker.Length;
        }

        var exitIndex = output.IndexOf(_payload.ExitMarkerPrefix, _displayOffset, StringComparison.Ordinal);
        if (exitIndex < 0)
        {
            if (output.Length <= _displayOffset)
                return "";

            var displayText = output[_displayOffset..];
            _displayOffset = output.Length;
            return displayText;
        }

        var beforeExitMarker = output[_displayOffset..exitIndex];
        var markerLineEnd = output.IndexOf('\n', exitIndex);
        if (markerLineEnd < 0)
        {
            _displayOffset = exitIndex;
            return beforeExitMarker;
        }

        _displayOffset = output.Length;
        _displayCompleted = true;
        return beforeExitMarker;
    }
}
