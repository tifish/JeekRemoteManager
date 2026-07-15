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
    private readonly List<byte> _output = new(4096);
    private readonly List<byte> _csiParams = new(64);
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
        _output.Clear();
    }

    /// <summary>Transforms a chunk of VT output. Safe to call for partial sequences across chunks.</summary>
    public byte[] Process(ReadOnlySpan<byte> data)
    {
        _output.Clear();
        var i = 0;
        if (_pendingEsc)
        {
            _pendingEsc = false;
            if (data.Length == 0)
            {
                _pendingEsc = true;
                return [];
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
                _output.Add(0x1b);
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

                    _output.Add(b);
                    continue;
                }

                _output.Add(b);
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

        return _output.Count == 0 ? [] : _output.ToArray();
    }

    private void FlushCsi(char final)
    {
        // Only rewrite SGR (…m). Pass every other CSI through unchanged.
        if (final != 'm' || _csiSawQuestion)
        {
            EmitRawCsi(final);
            return;
        }

        var raw = Encoding.ASCII.GetString(_csiParams.ToArray());
        var parts = string.IsNullOrEmpty(raw)
            ? new List<int> { 0 }
            : ParseSgrParams(raw);

        var hasDim = false;
        var hasNormalIntensity = false;
        var hasReset = false;
        var hasExplicitFg = false;
        var rebuilt = new List<int>(parts.Count + 4);

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

    private static List<int> ParseSgrParams(string raw)
    {
        var list = new List<int>();
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
        return list;
    }

    private void EmitSgr(List<int> codes)
    {
        _output.Add(0x1b);
        _output.Add((byte)'[');
        if (codes.Count == 0)
        {
            _output.Add((byte)'m');
            return;
        }

        var text = string.Join(';', codes);
        foreach (var c in text)
            _output.Add((byte)c);
        _output.Add((byte)'m');
    }

    private void EmitRawCsi(char final)
    {
        _output.Add(0x1b);
        _output.Add((byte)'[');
        if (_csiSawQuestion)
            _output.Add((byte)'?');
        _output.AddRange(_csiParams);
        _output.Add((byte)final);
    }
}
