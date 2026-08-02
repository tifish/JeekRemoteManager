using System;
using System.Collections.Generic;
using System.Text;

namespace JeekRemoteManager.Services;

/// <summary>
/// Rewrites SGR dim (code 2) into an explicit soft-gray foreground for terminals that
/// parse dim in XTerm.NET but never paint it (SvcSystems.UI.Terminal ignores
/// <c>IsDim</c>). Must not treat the <c>2</c> inside <c>38;2;r;g;b</c> / <c>48;2;r;g;b</c>
/// true-color sequences as dim — that used to corrupt Grok's palette and leave user
/// input partially darkened.
/// </summary>
public sealed class TerminalDimColorFilter
{
    private bool _injectedDimGray;
    private bool _pendingEsc;
    // Reused across frames: a full-screen TUI repaint runs through here dozens of times a
    // second, and handing back a fresh array each time was pure garbage.
    private byte[] _output = new byte[4096];
    private int _outputLength;
    private readonly List<byte> _csiParams = new(64);
    // Reused per SGR sequence; a TUI repaint carries hundreds of them per frame.
    private readonly List<int> _parts = new(16);
    private readonly List<int> _rebuilt = new(24);
    private bool _inCsi;
    private bool _csiSawQuestion;

    /// <summary>Clears dim/CSI state for a new ConPTY session.</summary>
    public void Reset()
    {
        _injectedDimGray = false;
        _pendingEsc = false;
        _inCsi = false;
        _csiSawQuestion = false;
        _csiParams.Clear();
        _outputLength = 0;
    }

    /// <summary>
    /// Transforms a chunk of VT output. Safe to call for partial sequences across chunks.
    /// The segment points into a buffer this instance owns and is valid only until the
    /// next call.
    /// </summary>
    public ArraySegment<byte> Process(ReadOnlySpan<byte> data)
    {
        _outputLength = 0;
        var i = 0;
        if (_pendingEsc)
        {
            _pendingEsc = false;
            if (data.Length == 0)
            {
                _pendingEsc = true;
                return ArraySegment<byte>.Empty;
            }

            if (data[0] == (byte)'[')
            {
                _inCsi = true;
                _csiSawQuestion = false;
                _csiParams.Clear();
                i = 1;
            }
            else
            {
                Emit(0x1b);
            }
        }

        for (; i < data.Length; i++)
        {
            var b = data[i];
            if (!_inCsi)
            {
                if (b == 0x1b)
                {
                    if (i + 1 >= data.Length)
                    {
                        _pendingEsc = true;
                        break;
                    }

                    if (data[i + 1] == (byte)'[')
                    {
                        _inCsi = true;
                        _csiSawQuestion = false;
                        _csiParams.Clear();
                        i++; // consume '['
                        continue;
                    }

                    Emit(b);
                    continue;
                }

                Emit(b);
                continue;
            }

            // Inside CSI: collect until a final byte (0x40-0x7E).
            if (b is >= 0x40 and <= 0x7E)
            {
                FlushCsi((char)b);
                _inCsi = false;
                continue;
            }

            if (b == (byte)'?' && _csiParams.Count == 0)
            {
                _csiSawQuestion = true;
                continue;
            }

            _csiParams.Add(b);
        }

        return new ArraySegment<byte>(_output, 0, _outputLength);
    }

    private void Emit(byte value)
    {
        if (_outputLength == _output.Length)
            Array.Resize(ref _output, _output.Length * 2);
        _output[_outputLength++] = value;
    }

    private void FlushCsi(char final)
    {
        // Only rewrite SGR (…m). Pass every other CSI through unchanged.
        if (final != 'm' || _csiSawQuestion)
        {
            EmitRawCsi(final);
            return;
        }

        var parts = _parts;
        ParseSgrParamsInto(parts);

        var hasDim = false;
        var hasNormalIntensity = false;
        var hasReset = false;
        var hasExplicitFg = false;
        var rebuilt = _rebuilt;
        rebuilt.Clear();

        for (var i = 0; i < parts.Count; i++)
        {
            var code = parts[i];
            switch (code)
            {
                case 0:
                    hasReset = true;
                    _injectedDimGray = false;
                    rebuilt.Add(0);
                    break;
                case 1:
                    // Bold cancels dim in common terminal practice.
                    hasNormalIntensity = true;
                    rebuilt.Add(1);
                    break;
                case 2:
                    // Dim — only when not the RGB mode byte of 38;2 / 48;2 / 58;2
                    // (those are handled under case 38/48/58 via CopyExtendedColor).
                    hasDim = true;
                    break;
                case 22:
                    hasNormalIntensity = true;
                    rebuilt.Add(22);
                    break;
                case 39:
                    hasExplicitFg = true;
                    _injectedDimGray = false;
                    rebuilt.Add(39);
                    break;
                case >= 30 and <= 37:
                case >= 90 and <= 97:
                    hasExplicitFg = true;
                    _injectedDimGray = false;
                    rebuilt.Add(code);
                    break;
                case 38:
                case 48:
                case 58:
                    // 38/48/58 ; 5 ; n  or  ; 2 ; r ; g ; b — must not treat mode 2 as dim.
                    if (code == 38)
                    {
                        hasExplicitFg = true;
                        _injectedDimGray = false;
                    }

                    rebuilt.Add(code);
                    CopyExtendedColor(parts, ref i, rebuilt);
                    break;
                default:
                    rebuilt.Add(code);
                    break;
            }
        }

        if (hasDim && !hasExplicitFg)
        {
            // Soft gray close to "dimmed white" on dark themes (not bright-black 90,
            // which made secondary/UI text look almost black and leaked into input).
            rebuilt.Add(38);
            rebuilt.Add(2);
            rebuilt.Add(168);
            rebuilt.Add(168);
            rebuilt.Add(168);
            _injectedDimGray = true;
        }
        else if (hasNormalIntensity && _injectedDimGray && !hasExplicitFg && !hasReset)
        {
            // Leaving dim without a new FG: restore default foreground (bright white).
            rebuilt.Add(39);
            _injectedDimGray = false;
        }

        if (hasReset)
            _injectedDimGray = false;

        EmitSgr(rebuilt);
    }

    private static void CopyExtendedColor(List<int> parts, ref int i, List<int> rebuilt)
    {
        if (i + 1 >= parts.Count)
            return;
        var mode = parts[++i];
        rebuilt.Add(mode);
        if (mode == 5 && i + 1 < parts.Count)
        {
            rebuilt.Add(parts[++i]);
            return;
        }

        // True-color: mode 2, then R;G;B — the 2 is NOT SGR dim.
        if (mode == 2)
        {
            for (var n = 0; n < 3 && i + 1 < parts.Count; n++)
                rebuilt.Add(parts[++i]);
        }
    }

    private void ParseSgrParamsInto(List<int> list)
    {
        list.Clear();
        if (_csiParams.Count == 0)
        {
            list.Add(0);
            return;
        }

        if (TryParseSgrParamsFast(list))
            return;

        // Rare: sub-parameters (38:2:…) or other non-numeric bytes. Fall back to the
        // string parser so unusual input keeps behaving exactly as it did before.
        list.Clear();
        var raw = Encoding.ASCII.GetString(_csiParams.ToArray());
        foreach (var piece in raw.Split(';'))
        {
            if (piece.Length == 0)
            {
                list.Add(0);
                continue;
            }

            if (int.TryParse(piece, out var n))
                list.Add(n);
        }

        if (list.Count == 0)
            list.Add(0);
    }

    /// <summary>
    /// Parses plain <c>digit;digit;…</c> parameters straight off the bytes. Bails out on
    /// anything else, including runs long enough to overflow, so the slow path stays
    /// authoritative for input this cannot reproduce exactly.
    /// </summary>
    private bool TryParseSgrParamsFast(List<int> list)
    {
        var value = 0;
        var digits = 0;
        foreach (var b in _csiParams)
        {
            if (b == (byte)';')
            {
                list.Add(value);
                value = 0;
                digits = 0;
                continue;
            }

            if (b is < (byte)'0' or > (byte)'9' || ++digits > 9)
                return false;

            value = value * 10 + (b - (byte)'0');
        }

        list.Add(value);
        return true;
    }

    private void EmitSgr(List<int> codes)
    {
        Emit(0x1b);
        Emit((byte)'[');
        for (var i = 0; i < codes.Count; i++)
        {
            if (i > 0)
                Emit((byte)';');
            EmitInt(codes[i]);
        }

        Emit((byte)'m');
    }

    private void EmitInt(int value)
    {
        if (value < 0)
        {
            Emit((byte)'-');
            // Widen first so int.MinValue negates correctly.
            EmitDigits((uint)-(long)value);
            return;
        }

        EmitDigits((uint)value);
    }

    private void EmitDigits(uint value)
    {
        if (value >= 10)
            EmitDigits(value / 10);
        Emit((byte)('0' + value % 10));
    }

    private void EmitRawCsi(char final)
    {
        Emit(0x1b);
        Emit((byte)'[');
        if (_csiSawQuestion)
            Emit((byte)'?');
        foreach (var b in _csiParams)
            Emit(b);
        Emit((byte)final);
    }
}
