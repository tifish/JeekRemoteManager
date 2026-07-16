using System;
using Avalonia.Input;

namespace JeekRemoteManager.Services;

/// <summary>
/// Encodes function keys using the xterm-compatible sequences expected by
/// interactive CLIs. Kept outside the terminal control dependency so F21-F24
/// and modifier combinations are covered consistently for every provider.
/// </summary>
public static class TerminalFunctionKeySequence
{
    private static readonly int[] CsiCodes =
    [
        15, 17, 18, 19, 20, 21, 23, 24, // F5-F12
        25, 26, 28, 29, 31, 32, 33, 34, // F13-F20
        42, 43, 44, 45,                 // F21-F24
    ];

    public static bool TryEncode(
        Key key,
        KeyModifiers modifiers,
        out int functionKeyNumber,
        out string sequence)
    {
        functionKeyNumber = (int)key - (int)Key.F1 + 1;
        sequence = string.Empty;
        if (functionKeyNumber is < 1 or > 24)
            return false;

        // Keep the Windows host's standard close-window shortcut. Other function
        // key modifiers are terminal input and use xterm modifier parameters.
        if (key == Key.F4 && modifiers.HasFlag(KeyModifiers.Alt))
            return false;

        const KeyModifiers supported =
            KeyModifiers.Shift | KeyModifiers.Alt | KeyModifiers.Control;
        if ((modifiers & ~supported) != KeyModifiers.None)
            return false;

        var modifierCode = 1;
        if (modifiers.HasFlag(KeyModifiers.Shift))
            modifierCode += 1;
        if (modifiers.HasFlag(KeyModifiers.Alt))
            modifierCode += 2;
        if (modifiers.HasFlag(KeyModifiers.Control))
            modifierCode += 4;

        if (functionKeyNumber <= 4)
        {
            var final = (char)('P' + functionKeyNumber - 1);
            sequence = modifiers == KeyModifiers.None
                ? $"\u001bO{final}"
                : $"\u001b[1;{modifierCode}{final}";
            return true;
        }

        var code = CsiCodes[functionKeyNumber - 5];
        sequence = modifiers == KeyModifiers.None
            ? $"\u001b[{code}~"
            : $"\u001b[{code};{modifierCode}~";
        return true;
    }
}
