using System;

namespace JeekRemoteManager.Services;

/// <summary>
/// Reassembles terminal byte chunks on UTF-8 character boundaries.
///
/// ConPTY and SSH packets routinely split a multi-byte character across two reads, and
/// feeding half a sequence to the VT parser renders a replacement character. The other
/// way to avoid that is to decode each chunk to a string with a stateful decoder — but
/// when the consumer wants bytes again that costs a full transcode round trip per frame.
/// This holds back only the incomplete trailing sequence, so the bytes reach the parser
/// untouched and a steady stream allocates nothing.
/// </summary>
public sealed class Utf8ChunkAssembler
{
    // A UTF-8 sequence is at most 4 bytes, so at most 3 can be left over.
    private readonly byte[] _carry = new byte[4];
    private int _carryLength;
    private byte[] _buffer = new byte[4096];

    /// <summary>
    /// Returns the bytes that are safe to feed now. The segment points into a buffer this
    /// instance owns and stays valid only until the next call.
    /// </summary>
    public ArraySegment<byte> Append(ReadOnlySpan<byte> data)
    {
        var total = _carryLength + data.Length;
        if (total == 0)
            return ArraySegment<byte>.Empty;

        if (_buffer.Length < total)
            Array.Resize(ref _buffer, Math.Max(total, _buffer.Length * 2));

        _carry.AsSpan(0, _carryLength).CopyTo(_buffer);
        data.CopyTo(_buffer.AsSpan(_carryLength));

        var complete = CompleteLength(_buffer.AsSpan(0, total));
        _carryLength = total - complete;
        _buffer.AsSpan(complete, _carryLength).CopyTo(_carry);
        return new ArraySegment<byte>(_buffer, 0, complete);
    }

    /// <summary>Drops any held incomplete sequence, for a new session.</summary>
    public void Reset() => _carryLength = 0;

    /// <summary>
    /// How many leading bytes end on a complete UTF-8 sequence. Invalid bytes are passed
    /// through rather than held: the parser, not this buffer, decides how to render them.
    /// </summary>
    internal static int CompleteLength(ReadOnlySpan<byte> data)
    {
        // Only the last three bytes can be the start of an unfinished sequence.
        var limit = Math.Min(3, data.Length);
        for (var back = 1; back <= limit; back++)
        {
            var b = data[^back];
            if ((b & 0b1100_0000) == 0b1000_0000)
                continue; // Continuation byte; keep walking back to its lead byte.

            // Only real lead bytes start a sequence worth waiting for. C0/C1 (overlong)
            // and F5-FF (beyond U+10FFFF) can never begin one, so withholding them would
            // strand bytes that the parser should have seen — and drop them outright when
            // they end the final packet.
            var needed = b switch
            {
                < 0x80 => 1, // ASCII
                >= 0xC2 and <= 0xDF => 2,
                >= 0xE0 and <= 0xEF => 3,
                >= 0xF0 and <= 0xF4 => 4,
                _ => 1, // 0x80-0xBF handled above; C0/C1 and F5-FF are invalid, pass them on.
            };

            return needed <= back ? data.Length : data.Length - back;
        }

        return data.Length;
    }
}
