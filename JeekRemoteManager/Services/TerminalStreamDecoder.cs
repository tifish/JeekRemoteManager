using System;
using System.Text;

namespace JeekRemoteManager.Services;

/// <summary>
/// Stateful decoder for terminal byte streams. SSH/ConPTY packets frequently split
/// multi-byte characters (e.g. Chinese) across reads; <see cref="Encoding.GetString(byte[])"/>
/// on each packet alone replaces incomplete sequences with U+FFFD (tofu boxes). The
/// encoding is the connection's terminal encoding — UTF-8 unless the server speaks a
/// legacy code page such as GBK (see <see cref="TerminalEncoding"/>).
/// </summary>
public sealed class TerminalStreamDecoder(Encoding? encoding = null)
{
    private readonly Encoding _encoding = encoding ?? TerminalEncoding.Utf8;
    private readonly Decoder _decoder = (encoding ?? TerminalEncoding.Utf8).GetDecoder();
    private char[] _charBuffer = new char[1024];

    public Encoding Encoding => _encoding;

    /// <summary>Decodes the next chunk. Incomplete trailing multi-byte sequences are held until more bytes arrive.</summary>
    public string Decode(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
            return string.Empty;

        // Must call GetChars even when no complete character is ready: GetCharCount does not
        // retain incomplete multi-byte tails, but GetChars does.
        EnsureCharBuffer(_encoding.GetMaxCharCount(data.Length));
        var written = _decoder.GetChars(data, _charBuffer, flush: false);
        return written == 0 ? string.Empty : new string(_charBuffer, 0, written);
    }

    /// <summary>Flushes any held incomplete sequence (emits replacement chars for invalid tails).</summary>
    public string Flush()
    {
        ReadOnlySpan<byte> empty = [];
        EnsureCharBuffer(8);
        var written = _decoder.GetChars(empty, _charBuffer, flush: true);
        _decoder.Reset();
        return written == 0 ? string.Empty : new string(_charBuffer, 0, written);
    }

    private void EnsureCharBuffer(int minLength)
    {
        if (_charBuffer.Length < minLength)
            _charBuffer = new char[Math.Max(minLength, _charBuffer.Length * 2)];
    }

    public void Reset() => _decoder.Reset();
}
