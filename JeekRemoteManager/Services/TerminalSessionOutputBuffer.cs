using System;
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
    private readonly List<PendingPacket> _pending = [];
    private bool _drainScheduled;

    public int PendingPacketCount
    {
        get
        {
            lock (_gate)
                return _pending.Count;
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
            _pending.Add(new PendingPacket(generation, data.ToArray()));
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
                    byteCount += packet.Data.Length;
            }

            if (byteCount == 0)
            {
                _pending.Clear();
                return [];
            }

            var result = new byte[byteCount];
            var offset = 0;
            foreach (var packet in _pending)
            {
                if (packet.Generation != generation)
                    continue;

                packet.Data.CopyTo(result, offset);
                offset += packet.Data.Length;
            }

            _pending.Clear();
            return result;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _pending.Clear();
            _drainScheduled = false;
        }
    }

    private sealed record PendingPacket(int Generation, byte[] Data);
}
