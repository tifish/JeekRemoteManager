using System;

namespace JeekRemoteManager.Services;

/// <summary>
/// Keeps the most recent bytes of a stream in a fixed-size ring.
///
/// Used to hold the tail of a child process's output so an early exit can be explained
/// after the fact. A list that drops from the front instead would shift the whole window
/// on every chunk, for the entire life of the session, which is a lot of copying for
/// something only read when something goes wrong.
/// </summary>
public sealed class RecentOutputBuffer
{
    private readonly byte[] _buffer;
    private readonly object _gate = new();
    private int _start;
    private int _length;

    public RecentOutputBuffer(int capacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        _buffer = new byte[capacity];
    }

    public int Capacity => _buffer.Length;

    public int Length
    {
        get
        {
            lock (_gate)
                return _length;
        }
    }

    public void Append(ReadOnlySpan<byte> chunk)
    {
        if (chunk.Length == 0)
            return;

        lock (_gate)
        {
            var capacity = _buffer.Length;
            if (chunk.Length >= capacity)
            {
                chunk[^capacity..].CopyTo(_buffer);
                _start = 0;
                _length = capacity;
                return;
            }

            var writeAt = (_start + _length) % capacity;
            var toEnd = Math.Min(chunk.Length, capacity - writeAt);
            chunk[..toEnd].CopyTo(_buffer.AsSpan(writeAt));
            if (toEnd < chunk.Length)
                chunk[toEnd..].CopyTo(_buffer);

            var length = _length + chunk.Length;
            if (length > capacity)
            {
                _start = (_start + length - capacity) % capacity;
                length = capacity;
            }

            _length = length;
        }
    }

    /// <summary>Copies the retained bytes out in stream order, oldest first.</summary>
    public byte[] Snapshot()
    {
        lock (_gate)
        {
            if (_length == 0)
                return [];

            var copy = new byte[_length];
            var toEnd = Math.Min(_length, _buffer.Length - _start);
            _buffer.AsSpan(_start, toEnd).CopyTo(copy);
            if (toEnd < _length)
                _buffer.AsSpan(0, _length - toEnd).CopyTo(copy.AsSpan(toEnd));
            return copy;
        }
    }
}
