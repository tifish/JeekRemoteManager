using System;
using System.Text;

namespace JeekRemoteManager.Services;

/// <summary>
/// Character encodings an SSH terminal can speak, by the stable name stored on the
/// connection. Older servers (and many in mainland China) still run a GBK/GB18030 locale;
/// decoding their output as UTF-8 turns every Chinese character into replacement boxes,
/// and typing Chinese sends bytes the remote shell cannot read.
/// </summary>
/// <remarks>
/// Only the text boundaries convert: the display decoder, typed and pasted input, command
/// text the app types for the user, and output captured for scripts and agent tools. The
/// channel itself stays byte-transparent, because ZMODEM needs the raw 8-bit stream.
/// WSL and local ConPTY sessions are always UTF-8.
/// </remarks>
public static class TerminalEncoding
{
    public const string DefaultName = "UTF-8";

    /// <summary>UTF-8 without a BOM — the default for every session.</summary>
    public static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Names offered in the connection editor, in display order.</summary>
    public static readonly string[] Names =
    [
        DefaultName,
        "GB18030",
        "GBK",
        "Big5",
        "Shift-JIS",
        "EUC-KR",
    ];

    static TerminalEncoding() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>The encoding for a stored name; anything unknown or empty is UTF-8.</summary>
    public static Encoding Resolve(string? name) => Normalize(name) switch
    {
        "GB18030" => Encoding.GetEncoding(54936),
        "GBK" => Encoding.GetEncoding(936),
        "Big5" => Encoding.GetEncoding(950),
        "Shift-JIS" => Encoding.GetEncoding(932),
        "EUC-KR" => Encoding.GetEncoding(51949),
        _ => Utf8,
    };

    /// <summary>Canonical stored name for user input such as "gbk", "cp936" or "sjis".</summary>
    public static string Normalize(string? name) =>
        (name ?? "").Trim().ToUpperInvariant() switch
        {
            "GB18030" => "GB18030",
            "GBK" or "CP936" or "GB2312" => "GBK",
            "BIG5" or "CP950" => "Big5",
            "SHIFT-JIS" or "SHIFT_JIS" or "SJIS" or "CP932" => "Shift-JIS",
            "EUC-KR" or "CP949" => "EUC-KR",
            _ => DefaultName,
        };

    public static bool IsUtf8(Encoding encoding) => encoding.CodePage == Encoding.UTF8.CodePage;
}

/// <summary>
/// Re-encodes terminal input from UTF-8 (what the terminal control emits) to the session's
/// encoding. Stateful, like the output decoder, in case one character arrives split.
/// </summary>
public sealed class TerminalInputEncoder(Encoding target)
{
    private readonly Decoder _utf8 = TerminalEncoding.Utf8.GetDecoder();
    private readonly Encoder _target = target.GetEncoder();
    private char[] _chars = new char[256];

    public Encoding Target => target;

    public byte[] Encode(ReadOnlySpan<byte> utf8)
    {
        if (TerminalEncoding.IsUtf8(target))
            return utf8.ToArray();

        if (_chars.Length < utf8.Length + 2)
            _chars = new char[Math.Max(utf8.Length + 2, _chars.Length * 2)];
        var count = _utf8.GetChars(utf8, _chars, flush: false);
        if (count == 0)
            return [];

        var chars = _chars.AsSpan(0, count);
        var bytes = new byte[_target.GetByteCount(chars, flush: false)];
        _target.GetBytes(chars, bytes, flush: false);
        return bytes;
    }
}
