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
    private readonly object _gate = new();
    private readonly List<PendingChunk> _pending = [];
    private bool _drainScheduled;
    private int _pendingPacketCount;

    public int PendingPacketCount
    {
        get
        {
            lock (_gate)
                return _pendingPacketCount;
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
            if (_drainScheduled)
                return false;

            _drainScheduled = true;
            return true;
        }
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
            return result;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _pending.Clear();
            _pendingPacketCount = 0;
            _drainScheduled = false;
        }
    }

    private sealed class PendingChunk(int generation)
    {
        public int Generation { get; } = generation;

        public ArrayBufferWriter<byte> Buffer { get; } = new();
    }
}
