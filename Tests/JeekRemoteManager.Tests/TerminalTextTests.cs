using System.Text;
using System.IO.Compression;
using JeekRemoteManager.Services;

namespace JeekRemoteManager.Tests;

public class TerminalTextTests
{
    private static readonly Encoding Gbk = TerminalEncoding.Resolve("GBK");

    [Theory]
    [InlineData("cp936", "GBK")]
    [InlineData("gb2312", "GBK")]
    [InlineData("sjis", "Shift-JIS")]
    [InlineData("", "UTF-8")]
    [InlineData("nonsense", "UTF-8")]
    public void Encoding_names_normalize(string input, string expected) =>
        Assert.Equal(expected, TerminalEncoding.Normalize(input));

    [Fact]
    public void Decoder_reassembles_a_character_split_across_packets()
    {
        var bytes = Gbk.GetBytes("中文");
        var decoder = new TerminalStreamDecoder(Gbk);

        var text = decoder.Decode(bytes.AsSpan(0, 3)) + decoder.Decode(bytes.AsSpan(3));

        Assert.Equal("中文", text);
    }

    [Fact]
    public void Input_is_reencoded_to_the_session_encoding()
    {
        var utf8 = Encoding.UTF8.GetBytes("中文");

        Assert.Equal(Gbk.GetBytes("中文"), new TerminalInputEncoder(Gbk).Encode(utf8));
        Assert.Equal(utf8, new TerminalInputEncoder(TerminalEncoding.Utf8).Encode(utf8));
    }

    [Fact]
    public void Script_payload_uses_the_session_encoding_before_compression()
    {
        const string script = "printf '%s' '中文'\r\n";
        foreach (var name in new[] { "UTF-8", "GBK", "GB18030", "Big5" })
        {
            var encoding = TerminalEncoding.Resolve(name);
            var payload = InteractiveShellPayloadRunner.Build(script, encoding: encoding);
            var encoded = string.Concat(payload.ExecuteCommand.Split('\n').Skip(1)
                .TakeWhile(line => line != payload.PayloadDelimiter));
            using var input = new MemoryStream(Convert.FromBase64String(encoded));
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            gzip.CopyTo(output);

            Assert.Equal(encoding.GetBytes("printf '%s' '中文'\n"), output.ToArray());
            Assert.Equal(encoded, InteractiveShellPayloadRunner.EncodePayloadForShell(script, encoding));
        }
    }

    [Fact]
    public async Task Script_output_decodes_and_redisplays_in_the_session_encoding()
    {
        var payload = InteractiveShellPayloadRunner.Build("echo probe\n", "encodingtest");
        var monitor = new InteractiveShellPayloadMonitor(payload, Gbk);
        monitor.Append(Gbk.GetBytes("\n" + payload.BeginMarker + "\n"));

        var display = monitor.Append(Gbk.GetBytes("你好\n" + payload.ExitMarkerPrefix + "0\n"));
        var result = await monitor.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Contains("你好", result.Output);
        Assert.Contains("你好", Gbk.GetString(display));
    }

    [Fact]
    public void Login_menu_capture_follows_the_session_encoding()
    {
        var capture = new LoginMenuOutputCapture();
        capture.SetEncoding(Gbk);
        var bytes = Gbk.GetBytes("1) 生产服务器");

        capture.Append(bytes.AsSpan(0, 5));
        capture.Append(bytes.AsSpan(5));

        Assert.Equal("1) 生产服务器", capture.Snapshot());
    }

    [Fact]
    public void Stripper_keeps_readable_text_only()
    {
        var stripper = new AnsiTextStripper();

        var text = stripper.Strip("\u001b]0;title\u0007\u001b[1;32mgreen\u001b[0m plain\r\n")
                   + stripper.Strip("split \u001b[3")
                   + stripper.Strip("1mred\u001b[0m\r\n")
                   + stripper.Strip("10%\r100%\r\n\u001b(Bdone\u0008!\r\n");

        Assert.Equal("green plain\nsplit red\n10%100%\ndone!\n", text);
    }

    [Fact]
    public void Stripper_handles_osc_terminated_by_string_terminator()
    {
        var stripper = new AnsiTextStripper();

        Assert.Equal("after", stripper.Strip("\u001b]8;;http://x\u001b\\after"));
    }
}
