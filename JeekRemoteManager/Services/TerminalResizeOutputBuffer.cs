using System;
using System.Buffers;

namespace JeekRemoteManager.Services;

/// <summary>
/// Temporarily combines display bytes produced in response to a terminal resize.
/// Readline redraws a prompt as carriage-return plus replacement text, which may
/// arrive in separate packets; rendering those packets separately makes the caret
/// visibly jump to the left edge and back.
/// </summary>
public sealed class TerminalResizeOutputBuffer
{
    private const int MaxRetainedCapacity = 64 * 1024;
    private const int MaxPendingBytes = 4 * 1024 * 1024;

    private readonly object _gate = new();
    private ArrayBufferWriter<byte> _pending = new();
    private bool _isActive;

    public bool IsActive
    {
        get
        {
            lock (_gate)
                return _isActive;
        }
    }

    public int PendingByteCount
    {
        get
        {
            lock (_gate)
                return _pending.WrittenCount;
        }
    }

    public void Start()
    {
        lock (_gate)
            _isActive = true;
    }

    public bool TryAppend(ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            if (!_isActive)
                return false;

            if (_pending.WrittenCount + data.Length > MaxPendingBytes)
            {
                // Cap runaway output during the short resize settle window; keep the newest tail.
                var keep = Math.Min(data.Length, MaxPendingBytes);
                _pending = new ArrayBufferWriter<byte>(Math.Min(keep, MaxRetainedCapacity));
                _pending.Write(data[^keep..]);
                return true;
            }

            _pending.Write(data);
            return true;
        }
    }

    public byte[] StopAndDrain()
    {
        lock (_gate)
        {
            _isActive = false;
            if (_pending.WrittenCount == 0)
                return [];

            var result = _pending.WrittenSpan.ToArray();
            if (_pending.Capacity > MaxRetainedCapacity)
                _pending = new ArrayBufferWriter<byte>();
            else
                _pending.ResetWrittenCount();
            return result;
        }
    }
}
