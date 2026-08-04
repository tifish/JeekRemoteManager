using System;
using System.Buffers;
using System.Collections.Generic;

namespace JeekRemoteManager.Services;

/// <summary>
/// Coalesces terminal packets until the UI thread is ready to render them. Full-screen
/// TUIs often split one repaint across several ConPTY reads; presenting every read can
/// expose intermediate cursor positions even though they belong to one logical frame.
/// </summary>
public sealed class TerminalSessionOutputBuffer
{
    /// <summary>
    /// Ceiling on queued-but-unrendered output. The queue only grows when the UI thread
    /// cannot keep up — a modal dialog, a long layout pass — and a remote running
    /// something like <c>cat /dev/urandom</c> fills it far faster than it drains. Without
    /// a bound that is an out-of-memory kill; with one it is a dropped scrollback region,
    /// which is what a real terminal does anyway. 16 MiB is orders of magnitude more than
    /// a stalled frame or two of legitimate output.
    /// </summary>
    public const int MaxPendingBytes = 16 * 1024 * 1024;

    private readonly object _gate = new();
    private readonly List<PendingChunk> _pending = [];
    private bool _drainScheduled;
    private int _pendingPacketCount;
    private int _pendingByteCount;
    private long _droppedByteCount;

    public int PendingPacketCount
    {
        get
        {
            lock (_gate)
                return _pendingPacketCount;
        }
    }

    public int PendingByteCount
    {
        get
        {
            lock (_gate)
                return _pendingByteCount;
        }
    }

    /// <summary>Total bytes discarded because the UI could not drain fast enough.</summary>
    public long DroppedByteCount
    {
        get
        {
            lock (_gate)
                return _droppedByteCount;
        }
    }

    /// <summary>
    /// Copies and queues a packet. Returns true only when the caller needs to schedule
    /// a drain; later packets join that already scheduled UI update.
    /// </summary>
    public bool Append(ReadOnlySpan<byte> data, int generation)
    {
        if (data.IsEmpty)
            return false;

        lock (_gate)
        {
            if (_pending.Count == 0 || _pending[^1].Generation != generation)
                _pending.Add(new PendingChunk(generation));
            _pending[^1].Buffer.Write(data);
            _pendingPacketCount++;
            _pendingByteCount += data.Length;
            TrimToCapacityLocked();
            if (_drainScheduled)
                return false;

            _drainScheduled = true;
            return true;
        }
    }

    /// <summary>
    /// Brings the queue back under the cap, oldest output first — the newest bytes are
    /// the ones the user is waiting to see. Whole chunks from earlier generations go
    /// first (a drain would discard those anyway), then the head of the newest chunk.
    /// </summary>
    private void TrimToCapacityLocked()
    {
        if (_pendingByteCount <= MaxPendingBytes)
            return;

        while (_pendingByteCount > MaxPendingBytes && _pending.Count > 1)
        {
            var oldest = _pending[0];
            _pending.RemoveAt(0);
            _pendingByteCount -= oldest.Buffer.WrittenCount;
            _droppedByteCount += oldest.Buffer.WrittenCount;
        }

        if (_pendingByteCount <= MaxPendingBytes)
            return;

        // A flood with no intervening reconnect all lands in one chunk. Keep its tail in
        // a fresh chunk so the oversized backing array is released rather than retained
        // at high-water mark for the rest of the session.
        var crowded = _pending[0];
        var keep = MaxPendingBytes / 2;
        var replacement = new PendingChunk(crowded.Generation);
        replacement.Buffer.Write(crowded.Buffer.WrittenSpan[^keep..]);
        _pending[0] = replacement;
        _droppedByteCount += _pendingByteCount - keep;
        _pendingByteCount = keep;
    }

    /// <summary>Drains packets for the current session and discards stale-session output.</summary>
    public byte[] Drain(int generation)
    {
        lock (_gate)
        {
            _drainScheduled = false;
            if (_pending.Count == 0)
                return [];

            var byteCount = 0;
            foreach (var packet in _pending)
            {
                if (packet.Generation == generation)
                    byteCount += packet.Buffer.WrittenCount;
            }

            if (byteCount == 0)
            {
                _pending.Clear();
                _pendingPacketCount = 0;
                _pendingByteCount = 0;
                return [];
            }

            var result = new byte[byteCount];
            var offset = 0;
            foreach (var packet in _pending)
            {
                if (packet.Generation != generation)
                    continue;

                packet.Buffer.WrittenSpan.CopyTo(result.AsSpan(offset));
                offset += packet.Buffer.WrittenCount;
            }

            _pending.Clear();
            _pendingPacketCount = 0;
            _pendingByteCount = 0;
            return result;
        }
    }

    /// <summary>Reads and resets the dropped-byte tally, so the terminal can report a
    /// truncation once instead of on every frame.</summary>
    public long TakeDroppedByteCount()
    {
        lock (_gate)
        {
            var dropped = _droppedByteCount;
            _droppedByteCount = 0;
            return dropped;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _pending.Clear();
            _pendingPacketCount = 0;
            _pendingByteCount = 0;
            _droppedByteCount = 0;
            _drainScheduled = false;
        }
    }

    private sealed class PendingChunk(int generation)
    {
        public int Generation { get; } = generation;

        public ArrayBufferWriter<byte> Buffer { get; } = new();
    }
}
