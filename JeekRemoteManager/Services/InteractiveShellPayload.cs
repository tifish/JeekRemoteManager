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
    public const string CurrentShellHookVariable = "JEEKREMOTE_CURRENT_SHELL_HOOK";

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

        // Everything the interactive shell reads as a separate command makes it print
        // another prompt (and, once echo is back on, echo the command text) between the
        // script output and the exit marker. So the setup runs in prepareCommand (its
        // noise precedes the BEGIN marker and is never displayed), and the entire
        // epilogue — status capture, hook sourcing, echo restore, exit marker — shares
        // ONE logical line with the heredoc pipeline, so the shell reads a single
        // command and nothing is prompted or echoed after the script's own output.
        // Disable interactive pagers (less/more). Otherwise git/systemctl/man open `less`,
        // print "(END)", and never reach the EXIT marker — the AI then calls terminal_interrupt.
        var prepareCommand =
            "stty -echo 2>/dev/null || true; " +
            "export PAGER=cat GIT_PAGER=cat SYSTEMD_PAGER=cat MANPAGER=cat " +
            "BAT_PAGER=cat DELTA_PAGER=cat GH_PAGER=cat LESS= LV=; " +
            "__jrm_old_ps2=${PS2-}; PS2=; " +
            "__jrm_current_shell_hook=${TMPDIR:-/tmp}/jeekremote-current-shell-" + token + "-$$.sh; " +
            "if ( umask 077 && : > \"$__jrm_current_shell_hook\" ) 2>/dev/null; then " +
            CurrentShellHookVariable + "=$__jrm_current_shell_hook; " +
            "else __jrm_current_shell_hook=; " + CurrentShellHookVariable + "=; fi; " +
            "export " + CurrentShellHookVariable + "; " +
            "printf '\\n%s%s\\n' '__JRM_READY_' '" + token + "__'\n";

        var executeCommand =
            "base64 -d <<'" + payloadDelimiter + "' | gzip -dc | { printf '\\n%s%s\\n' '__JRM_BEGIN_' '" + token + "__'; sh -s; }; " +
            "__jrm_status=$?; " +
            "if [ \"$__jrm_status\" -eq 0 ] && [ -n \"${__jrm_current_shell_hook:-}\" ] && [ -s \"$__jrm_current_shell_hook\" ]; then . \"$__jrm_current_shell_hook\" >/dev/null 2>&1 || true; fi; " +
            "if [ -n \"${__jrm_current_shell_hook:-}\" ]; then rm -f \"$__jrm_current_shell_hook\" 2>/dev/null || true; fi; " +
            "unset " + CurrentShellHookVariable + "; " +
            "PS2=$__jrm_old_ps2; " +
            "stty echo 2>/dev/null || true; " +
            "printf '\\n%s%s:%s\\n' '__JRM_EXIT_' '" + token + "__' \"$__jrm_status\"\n" +
            string.Join('\n', encodedPayloadLines) + "\n" +
            payloadDelimiter + "\n";

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

    // The complete command output is kept, because that is what the result hands back.
    // It lives in a char buffer rather than a StringBuilder so each marker scan can run
    // over a span of the newly appended region: materializing the whole buffer once per
    // packet made a large command's output cost O(n^2) in both copying and scanning.
    private char[] _output = new char[4096];
    private int _outputLength;

    private readonly TaskCompletionSource<bool> _ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<InteractiveShellPayloadResult> _exit =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _readyFound;
    private int _readyScanFrom;
    private int _beginScanFrom;
    private int _exitScanFrom;
    private int _exitMarkerIndex = -1;
    private bool _exitReported;
    private int _displayOffset;
    private bool _displayStarted;
    private bool _displayCompleted;
    private string _pendingDisplayNewlines = "";

    public InteractiveShellPayloadMonitor(InteractiveShellPayload payload)
    {
        _payload = payload;
    }

    public byte[] Append(byte[] data)
    {
        if (data.Length == 0)
            return Array.Empty<byte>();

        string displayText;
        bool markReady;
        InteractiveShellPayloadResult? result;

        lock (_gate)
        {
            AppendDecoded(data);
            markReady = ScanForReadyMarker();
            result = !_exitReported && TryParseExit(out var exitCode)
                ? new InteractiveShellPayloadResult(exitCode, new string(_output, 0, _outputLength))
                : null;
            if (result is not null)
                _exitReported = true;
            displayText = ExtractDisplayText();
        }

        if (markReady)
            _ready.TrySetResult(true);
        if (result is not null)
            _exit.TrySetResult(result);
        return displayText.Length == 0
            ? Array.Empty<byte>()
            : Encoding.UTF8.GetBytes(displayText);
    }

    private void AppendDecoded(byte[] data)
    {
        var maxChars = _decoder.GetCharCount(data, 0, data.Length);
        EnsureCapacity(_outputLength + maxChars);
        _outputLength += _decoder.GetChars(data, 0, data.Length, _output, _outputLength);
    }

    private void EnsureCapacity(int required)
    {
        if (_output.Length >= required)
            return;

        var size = _output.Length;
        while (size < required)
            size *= 2;
        Array.Resize(ref _output, size);
    }

    /// <summary>
    /// Searches only the region appended since the last call, overlapping by the marker
    /// length so a marker split across packets is still found, and stops once it is seen.
    /// </summary>
    private bool ScanForReadyMarker()
    {
        if (_readyFound)
            return false;

        var marker = _payload.ReadyMarker;
        var from = Math.Max(0, _readyScanFrom - (marker.Length - 1));
        var found = _output.AsSpan(from, _outputLength - from).IndexOf(marker.AsSpan()) >= 0;
        _readyScanFrom = _outputLength;
        if (found)
            _readyFound = true;
        return found;
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

    /// <summary>
    /// Keeps the last marker seen, so scanning only the newly appended region stays
    /// equivalent to a LastIndexOf over the whole output.
    /// </summary>
    private bool TryParseExit(out int exitCode)
    {
        exitCode = -1;
        var prefix = _payload.ExitMarkerPrefix;

        var from = Math.Max(0, _exitScanFrom - (prefix.Length - 1));
        var relative = _output.AsSpan(from, _outputLength - from).LastIndexOf(prefix.AsSpan());
        if (relative >= 0)
            _exitMarkerIndex = from + relative;
        _exitScanFrom = _outputLength;

        if (_exitMarkerIndex < 0)
            return false;

        var start = _exitMarkerIndex + prefix.Length;
        var end = start;
        if (end < _outputLength && _output[end] == '-')
            end++;
        while (end < _outputLength && char.IsDigit(_output[end]))
            end++;

        if (end <= start || !int.TryParse(_output.AsSpan(start, end - start), out exitCode))
            return false;

        return end < _outputLength && _output[end] is '\r' or '\n';
    }

    private string ExtractDisplayText()
    {
        if (_displayCompleted)
        {
            _displayOffset = _outputLength;
            return "";
        }

        if (!_displayStarted)
        {
            var beginMarker = _payload.BeginMarker;
            var beginFrom = Math.Max(0, _beginScanFrom - (beginMarker.Length - 1));
            var beginRelative = _output
                .AsSpan(beginFrom, _outputLength - beginFrom)
                .IndexOf(beginMarker.AsSpan());
            _beginScanFrom = _outputLength;
            if (beginRelative < 0)
            {
                _displayOffset = _outputLength;
                return "";
            }

            _displayStarted = true;
            _displayOffset = beginFrom + beginRelative + beginMarker.Length;
        }

        // From here the scan is already incremental: _displayOffset only moves forward.
        var exitRelative = _output
            .AsSpan(_displayOffset, _outputLength - _displayOffset)
            .IndexOf(_payload.ExitMarkerPrefix.AsSpan());
        if (exitRelative < 0)
        {
            if (_outputLength <= _displayOffset)
                return "";

            var displayText = new string(_output, _displayOffset, _outputLength - _displayOffset);
            _displayOffset = _outputLength;
            return WithholdTrailingNewlines(displayText);
        }

        var exitIndex = _displayOffset + exitRelative;
        var beforeExitMarker = new string(_output, _displayOffset, exitIndex - _displayOffset);
        var markerLineEnd = _output.AsSpan(exitIndex, _outputLength - exitIndex).IndexOf('\n');
        if (markerLineEnd < 0)
        {
            _displayOffset = exitIndex;
            return WithholdTrailingNewlines(beforeExitMarker);
        }

        _displayOffset = _outputLength;
        _displayCompleted = true;

        // Drop trailing newlines entirely: the exit-marker printf always injects one,
        // and the caller's completion line supplies its own line break, so anything
        // kept here would render as blank lines before the "[... exit N]" status.
        var completed = _pendingDisplayNewlines + beforeExitMarker;
        _pendingDisplayNewlines = "";
        return completed[..LengthWithoutTrailingNewlines(completed)];
    }

    /// <summary>
    /// Holds back a trailing run of newline characters instead of emitting it, prepending
    /// it to the next chunk that carries real content. This lets the completion path drop
    /// the newlines that would otherwise show as blank lines before the exit status line.
    /// </summary>
    private string WithholdTrailingNewlines(string text)
    {
        text = _pendingDisplayNewlines + text;
        var end = LengthWithoutTrailingNewlines(text);
        _pendingDisplayNewlines = text[end..];
        return text[..end];
    }

    private static int LengthWithoutTrailingNewlines(string text)
    {
        var end = text.Length;
        while (end > 0 && text[end - 1] is '\r' or '\n')
            end--;
        return end;
    }
}
