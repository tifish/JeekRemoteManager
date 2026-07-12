using System;
using System.Collections.Generic;

namespace JeekRemoteManager.Services;

/// <summary>
/// Temporarily combines display bytes produced in response to a terminal resize.
/// Readline redraws a prompt as carriage-return plus replacement text, which may
/// arrive in separate packets; rendering those packets separately makes the caret
/// visibly jump to the left edge and back.
/// </summary>
public sealed class TerminalResizeOutputBuffer
{
    private readonly object _gate = new();
    private readonly List<byte> _pending = [];
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
                return _pending.Count;
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

            foreach (var value in data)
                _pending.Add(value);
            return true;
        }
    }

    public byte[] StopAndDrain()
    {
        lock (_gate)
        {
            _isActive = false;
            if (_pending.Count == 0)
                return [];

            var result = _pending.ToArray();
            _pending.Clear();
            return result;
        }
    }
}
