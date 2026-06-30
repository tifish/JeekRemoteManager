using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace JeekRemoteManager.Services;

public enum ZmodemTransferDirection
{
    Download,
    Upload,
}

public sealed record ZmodemTransferResult(IReadOnlyList<string> Files);

public sealed class ZmodemTransferCanceledException : Exception
{
    public ZmodemTransferCanceledException(string message) : base(message)
    {
    }
}

public sealed record ZmodemDetection(
    ZmodemTransferDirection Direction,
    byte[] DisplayBytes,
    byte[] ProtocolBytes);

public sealed class ZmodemTriggerDetector
{
    private const int RetainedBytes = 5;
    private readonly List<byte> _pending = new();

    public ZmodemDetection? Append(ReadOnlySpan<byte> data, out byte[] displayBytes)
    {
        displayBytes = [];
        if (data.Length == 0)
            return null;

        _pending.AddRange(data.ToArray());
        var trigger = FindTrigger(_pending);
        if (trigger is not null)
        {
            var (index, direction) = trigger.Value;
            var prefix = _pending.Take(index).ToArray();
            var protocol = _pending.Skip(index).ToArray();
            _pending.Clear();
            return new ZmodemDetection(direction, prefix, protocol);
        }

        if (_pending.Count <= RetainedBytes)
            return null;

        var flushCount = _pending.Count - RetainedBytes;
        displayBytes = _pending.Take(flushCount).ToArray();
        _pending.RemoveRange(0, flushCount);
        return null;
    }

    public byte[] Flush()
    {
        if (_pending.Count == 0)
            return [];

        var bytes = _pending.ToArray();
        _pending.Clear();
        return bytes;
    }

    private static (int Index, ZmodemTransferDirection Direction)? FindTrigger(IReadOnlyList<byte> bytes)
    {
        for (var i = 0; i < bytes.Count; i++)
        {
            if (Matches(bytes, i, ZmodemConstants.ZPAD, ZmodemConstants.ZPAD, ZmodemConstants.ZDLE, ZmodemConstants.ZHEX, (byte)'0', (byte)'0'))
                return (i, ZmodemTransferDirection.Download);
            if (Matches(bytes, i, ZmodemConstants.ZPAD, ZmodemConstants.ZPAD, ZmodemConstants.ZDLE, ZmodemConstants.ZHEX, (byte)'0', (byte)'1'))
                return (i, ZmodemTransferDirection.Upload);

            if (Matches(bytes, i, ZmodemConstants.ZPAD, ZmodemConstants.ZDLE, ZmodemConstants.ZBIN, (byte)ZmodemHeaderType.ZRQINIT)
                || Matches(bytes, i, ZmodemConstants.ZPAD, ZmodemConstants.ZDLE, ZmodemConstants.ZBIN32, (byte)ZmodemHeaderType.ZRQINIT))
            {
                return (i, ZmodemTransferDirection.Download);
            }

            if (Matches(bytes, i, ZmodemConstants.ZPAD, ZmodemConstants.ZDLE, ZmodemConstants.ZBIN, (byte)ZmodemHeaderType.ZRINIT)
                || Matches(bytes, i, ZmodemConstants.ZPAD, ZmodemConstants.ZDLE, ZmodemConstants.ZBIN32, (byte)ZmodemHeaderType.ZRINIT))
            {
                return (i, ZmodemTransferDirection.Upload);
            }
        }

        return null;
    }

    private static bool Matches(IReadOnlyList<byte> bytes, int offset, params byte[] pattern)
    {
        if (offset + pattern.Length > bytes.Count)
            return false;

        for (var i = 0; i < pattern.Length; i++)
            if (bytes[offset + i] != pattern[i])
                return false;

        return true;
    }
}

public sealed class ZmodemByteQueue
{
    private readonly Channel<byte> _channel = Channel.CreateUnbounded<byte>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    public void Append(ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
            _channel.Writer.TryWrite(b);
    }

    public ValueTask<byte> ReadByteAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAsync(cancellationToken);

    public byte[] DrainAvailable()
    {
        var bytes = new List<byte>();
        while (_channel.Reader.TryRead(out var b))
            bytes.Add(b);

        return bytes.ToArray();
    }

    public void Complete(Exception? error = null)
    {
        if (error is null)
            _channel.Writer.TryComplete();
        else
            _channel.Writer.TryComplete(error);
    }
}

public sealed class ZmodemSession
{
    private readonly Func<byte[], CancellationToken, Task> _writeAsync;
    private readonly Func<CancellationToken, ValueTask<byte>> _readByteAsync;
    private readonly Action<string>? _trace;
    private readonly Queue<byte> _pushback = new();
    private ZmodemHeaderFormat _lastHeaderFormat = ZmodemHeaderFormat.Hex;
    private bool _peerSupportsCrc32 = true;

    private static readonly TimeSpan HeaderTimeout = TimeSpan.FromSeconds(30);

    public ZmodemSession(
        Func<byte[], CancellationToken, Task> writeAsync,
        Func<CancellationToken, ValueTask<byte>> readByteAsync,
        Action<string>? trace = null)
    {
        _writeAsync = writeAsync;
        _readByteAsync = readByteAsync;
        _trace = trace;
    }

    public async Task<ZmodemTransferResult> ReceiveAsync(
        string destinationFolder,
        CancellationToken cancellationToken,
        bool sendInitialHeader = true)
    {
        Directory.CreateDirectory(destinationFolder);
        var received = new List<string>();
        Trace($"RX session start destination=\"{destinationFolder}\" sendInitialHeader={sendInitialHeader}");
        if (sendInitialHeader)
            await BeginReceiveAsync(cancellationToken).ConfigureAwait(false);

        while (true)
        {
            var header = await ReadHeaderAsync(HeaderTimeout, cancellationToken).ConfigureAwait(false);
            switch (header.Type)
            {
                case ZmodemHeaderType.ZRQINIT:
                    await SendReceiveInitAsync(cancellationToken).ConfigureAwait(false);
                    break;

                case ZmodemHeaderType.ZSINIT:
                    _ = await ReadDataSubpacketAsync(cancellationToken).ConfigureAwait(false);
                    await SendHeaderHexAsync(ZmodemHeaderType.ZACK, 0, cancellationToken).ConfigureAwait(false);
                    break;

                case ZmodemHeaderType.ZFILE:
                    var saved = await ReceiveFileAsync(destinationFolder, cancellationToken).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(saved))
                        received.Add(saved);
                    break;

                case ZmodemHeaderType.ZFIN:
                    await SendHeaderHexAsync(ZmodemHeaderType.ZFIN, 0, cancellationToken).ConfigureAwait(false);
                    await WriteRawAsync(Encoding.ASCII.GetBytes("OO"), cancellationToken).ConfigureAwait(false);
                    return new ZmodemTransferResult(received);

                case ZmodemHeaderType.ZABORT:
                case ZmodemHeaderType.ZCAN:
                    throw new ZmodemTransferCanceledException("Remote cancelled the ZMODEM transfer.");

                default:
                    await SendReceiveInitAsync(cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
    }

    public async Task<ZmodemTransferResult> SendAsync(
        IReadOnlyList<string> filePaths,
        CancellationToken cancellationToken)
    {
        Trace($"TX session start files={filePaths.Count}");
        var files = filePaths
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .ToArray();
        if (files.Length == 0)
            return new ZmodemTransferResult([]);

        var receiver = await WaitForReceiverInitAsync(cancellationToken).ConfigureAwait(false);
        _peerSupportsCrc32 = (receiver.Zf0 & ZmodemConstants.CANFC32) != 0;
        Trace($"TX peer init zf0=0x{receiver.Zf0:x2} crc32={_peerSupportsCrc32}");
        if (!_peerSupportsCrc32)
            throw new NotSupportedException("The remote ZMODEM receiver did not advertise CRC32 support.");

        var sent = new List<string>();
        foreach (var file in files)
        {
            if (await SendFileAsync(file, cancellationToken).ConfigureAwait(false))
                sent.Add(file);
        }

        await FinishSendAsync(cancellationToken).ConfigureAwait(false);
        return new ZmodemTransferResult(sent);
    }

    public Task CancelAsync(CancellationToken cancellationToken) =>
        WriteRawAsync(ZmodemConstants.CancelSequence, cancellationToken);

    public Task BeginReceiveAsync(CancellationToken cancellationToken) =>
        SendReceiveInitAsync(cancellationToken);

    private async Task<string?> ReceiveFileAsync(string destinationFolder, CancellationToken cancellationToken)
    {
        var packet = await ReadDataSubpacketAsync(cancellationToken).ConfigureAwait(false);
        var info = ZmodemFileInfo.Parse(packet.Data);
        Trace($"RX file metadata name=\"{info.FileName}\" size={info.Size?.ToString() ?? "unknown"}");
        if (string.IsNullOrWhiteSpace(info.FileName))
        {
            await SendHeaderHexAsync(ZmodemHeaderType.ZSKIP, 0, cancellationToken).ConfigureAwait(false);
            return null;
        }

        var destination = GetUniqueDestinationPath(destinationFolder, info.FileName);
        Trace($"RX file destination=\"{destination}\"");
        await SendHeaderHexAsync(ZmodemHeaderType.ZRPOS, 0, cancellationToken).ConfigureAwait(false);

        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        long offset = 0;

        while (true)
        {
            var header = await ReadHeaderAsync(HeaderTimeout, cancellationToken).ConfigureAwait(false);
            switch (header.Type)
            {
                case ZmodemHeaderType.ZDATA:
                    if (header.Argument != offset)
                    {
                        await SendHeaderHexAsync(ZmodemHeaderType.ZRPOS, offset, cancellationToken).ConfigureAwait(false);
                        break;
                    }

                    await ReceiveDataBlocksAsync(output, offset, value => offset = value, cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case ZmodemHeaderType.ZEOF:
                    if (header.Argument != offset)
                    {
                        await SendHeaderHexAsync(ZmodemHeaderType.ZRPOS, offset, cancellationToken).ConfigureAwait(false);
                        break;
                    }

                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    if (info.ModifiedAt is not null)
                        TrySetLastWriteTime(destination, info.ModifiedAt.Value);
                    await SendReceiveInitAsync(cancellationToken).ConfigureAwait(false);
                    return destination;

                case ZmodemHeaderType.ZSKIP:
                    return null;

                case ZmodemHeaderType.ZFIN:
                    return destination;

                case ZmodemHeaderType.ZABORT:
                case ZmodemHeaderType.ZCAN:
                    throw new ZmodemTransferCanceledException("Remote cancelled the ZMODEM transfer.");

                default:
                    await SendHeaderHexAsync(ZmodemHeaderType.ZRPOS, offset, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
    }

    private async Task ReceiveDataBlocksAsync(
        Stream output,
        long initialOffset,
        Action<long> updateOffset,
        CancellationToken cancellationToken)
    {
        var offset = initialOffset;

        while (true)
        {
            var packet = await ReadDataSubpacketAsync(cancellationToken).ConfigureAwait(false);
            if (packet.Data.Length > 0)
            {
                await output.WriteAsync(packet.Data, cancellationToken).ConfigureAwait(false);
                offset += packet.Data.Length;
                updateOffset(offset);
            }

            switch (packet.End)
            {
                case ZmodemFrameEnd.ZCRCG:
                    continue;
                case ZmodemFrameEnd.ZCRCQ:
                    await SendHeaderHexAsync(ZmodemHeaderType.ZACK, offset, cancellationToken).ConfigureAwait(false);
                    continue;
                case ZmodemFrameEnd.ZCRCW:
                    await SendHeaderHexAsync(ZmodemHeaderType.ZACK, offset, cancellationToken).ConfigureAwait(false);
                    return;
                case ZmodemFrameEnd.ZCRCE:
                    return;
                default:
                    throw new InvalidDataException("Unsupported ZMODEM data frame terminator.");
            }
        }
    }

    private async Task<ZmodemHeader> WaitForReceiverInitAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var header = await ReadHeaderAsync(HeaderTimeout, cancellationToken).ConfigureAwait(false);
            switch (header.Type)
            {
                case ZmodemHeaderType.ZRINIT:
                    return header;
                case ZmodemHeaderType.ZCHALLENGE:
                    await SendHeaderHexAsync(ZmodemHeaderType.ZACK, header.Argument, cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case ZmodemHeaderType.ZABORT:
                case ZmodemHeaderType.ZCAN:
                    throw new ZmodemTransferCanceledException("Remote cancelled the ZMODEM transfer.");
            }
        }
    }

    private async Task<bool> SendFileAsync(string filePath, CancellationToken cancellationToken)
    {
        var info = new FileInfo(filePath);
        var metadata = ZmodemFileInfo.Build(Path.GetFileName(filePath), info.Length, info.LastWriteTimeUtc);

        while (true)
        {
            await SendHeaderBinary32Async(
                    ZmodemHeaderType.ZFILE,
                    HeaderBytesForFlags(zf0: ZmodemConstants.ZCBIN),
                    cancellationToken)
                .ConfigureAwait(false);
            await SendDataSubpacketAsync(metadata, ZmodemFrameEnd.ZCRCW, cancellationToken).ConfigureAwait(false);

            var response = await ReadHeaderAsync(HeaderTimeout, cancellationToken).ConfigureAwait(false);
            switch (response.Type)
            {
                case ZmodemHeaderType.ZRPOS:
                    await SendFileDataAsync(filePath, response.Argument, cancellationToken).ConfigureAwait(false);
                    return true;
                case ZmodemHeaderType.ZSKIP:
                    return false;
                case ZmodemHeaderType.ZRINIT:
                    continue;
                case ZmodemHeaderType.ZABORT:
                case ZmodemHeaderType.ZCAN:
                    throw new ZmodemTransferCanceledException("Remote cancelled the ZMODEM transfer.");
            }
        }
    }

    private async Task SendFileDataAsync(string filePath, long offset, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (offset > 0)
            input.Seek(offset, SeekOrigin.Begin);

        var buffer = new byte[8192];
        var position = offset;

        await SendHeaderBinary32Async(ZmodemHeaderType.ZDATA, position, cancellationToken).ConfigureAwait(false);

        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;

            position += read;
            var end = input.Position >= input.Length ? ZmodemFrameEnd.ZCRCE : ZmodemFrameEnd.ZCRCG;
            await SendDataSubpacketAsync(buffer.AsMemory(0, read), end, cancellationToken).ConfigureAwait(false);
        }

        await SendHeaderBinary32Async(ZmodemHeaderType.ZEOF, position, cancellationToken).ConfigureAwait(false);

        while (true)
        {
            var header = await ReadHeaderAsync(HeaderTimeout, cancellationToken).ConfigureAwait(false);
            switch (header.Type)
            {
                case ZmodemHeaderType.ZRINIT:
                    return;
                case ZmodemHeaderType.ZRPOS:
                    await SendFileDataAsync(filePath, header.Argument, cancellationToken).ConfigureAwait(false);
                    return;
                case ZmodemHeaderType.ZSKIP:
                    return;
                case ZmodemHeaderType.ZABORT:
                case ZmodemHeaderType.ZCAN:
                    throw new ZmodemTransferCanceledException("Remote cancelled the ZMODEM transfer.");
            }
        }
    }

    private async Task FinishSendAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await SendHeaderHexAsync(ZmodemHeaderType.ZFIN, 0, cancellationToken).ConfigureAwait(false);
            var header = await ReadHeaderAsync(HeaderTimeout, cancellationToken).ConfigureAwait(false);
            if (header.Type == ZmodemHeaderType.ZFIN)
            {
                await WriteRawAsync(Encoding.ASCII.GetBytes("OO"), cancellationToken).ConfigureAwait(false);
                return;
            }
        }
    }

    private Task SendReceiveInitAsync(CancellationToken cancellationToken) =>
        SendHeaderHexAsync(
            ZmodemHeaderType.ZRINIT,
            HeaderBytesForFlags(
                zf0: ZmodemConstants.CANFDX | ZmodemConstants.CANOVIO | ZmodemConstants.CANBRK | ZmodemConstants.CANFC32),
            cancellationToken);

    private async Task<ZmodemHeader> ReadHeaderAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var token = cancellationToken;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        token = timeoutCts.Token;

        try
        {
            Trace($"RX header wait timeout={timeout.TotalSeconds:0.#}s");
            var canCount = 0;
            while (true)
            {
                var b = await ReadRawByteAsync(token).ConfigureAwait(false);
                if (b == ZmodemConstants.CAN)
                {
                    if (++canCount >= 5)
                        throw new ZmodemTransferCanceledException("Remote sent ZMODEM cancel.");
                }
                else
                {
                    canCount = 0;
                }

                if (b != ZmodemConstants.ZPAD)
                    continue;

                do
                {
                    b = await ReadRawByteAsync(token).ConfigureAwait(false);
                } while (b == ZmodemConstants.ZPAD);

                if (b != ZmodemConstants.ZDLE)
                    continue;

                var format = await ReadRawByteAsync(token).ConfigureAwait(false);
                var header = format switch
                {
                    ZmodemConstants.ZHEX => await ReadHexHeaderAsync(token).ConfigureAwait(false),
                    ZmodemConstants.ZBIN => await ReadBinary16HeaderAsync(token).ConfigureAwait(false),
                    ZmodemConstants.ZBIN32 => await ReadBinary32HeaderAsync(token).ConfigureAwait(false),
                    _ => null,
                };

                if (header is null)
                    continue;

                var value = header.Value;
                _lastHeaderFormat = value.Format;
                Trace(
                    $"RX header type={value.Type} format={value.Format} arg={value.Argument} zf0=0x{value.Zf0:x2} zf1=0x{value.Zf1:x2} zf2=0x{value.Zf2:x2} zf3=0x{value.Zf3:x2}");
                return value;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Trace("RX header timeout");
            throw new TimeoutException("Timed out waiting for a ZMODEM header.");
        }
    }

    private async Task<ZmodemHeader?> ReadHexHeaderAsync(CancellationToken cancellationToken)
    {
        var bytes = new byte[7];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = await ReadHexByteAsync(cancellationToken).ConfigureAwait(false);

        var expected = ZmodemCrc.Crc16(bytes.AsSpan(0, 5));
        var actual = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(5, 2));
        if (expected != actual)
        {
            Trace($"RX hex header CRC mismatch expected=0x{expected:x4} actual=0x{actual:x4}");
            return null;
        }

        await ConsumeHexHeaderLineEndAsync(cancellationToken).ConfigureAwait(false);
        return ZmodemHeader.FromBytes(ZmodemHeaderFormat.Hex, bytes.AsSpan(0, 5));
    }

    private async Task<ZmodemHeader?> ReadBinary16HeaderAsync(CancellationToken cancellationToken)
    {
        var bytes = new byte[7];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = await ReadEscapedDataByteAsync(cancellationToken).ConfigureAwait(false);

        var expected = ZmodemCrc.Crc16(bytes.AsSpan(0, 5));
        var actual = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(5, 2));
        if (expected != actual)
        {
            Trace($"RX bin16 header CRC mismatch expected=0x{expected:x4} actual=0x{actual:x4}");
            return null;
        }

        return ZmodemHeader.FromBytes(ZmodemHeaderFormat.Bin16, bytes.AsSpan(0, 5));
    }

    private async Task<ZmodemHeader?> ReadBinary32HeaderAsync(CancellationToken cancellationToken)
    {
        var bytes = new byte[9];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = await ReadEscapedDataByteAsync(cancellationToken).ConfigureAwait(false);

        var expected = ZmodemCrc.Crc32(bytes.AsSpan(0, 5));
        var actual = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(5, 4));
        if (expected != actual)
        {
            Trace($"RX bin32 header CRC mismatch expected=0x{expected:x8} actual=0x{actual:x8}");
            return null;
        }

        return ZmodemHeader.FromBytes(ZmodemHeaderFormat.Bin32, bytes.AsSpan(0, 5));
    }

    private async Task ConsumeHexHeaderLineEndAsync(CancellationToken cancellationToken)
    {
        for (var i = 0; i < 4; i++)
        {
            var b = await TryReadRawByteAsync(TimeSpan.FromMilliseconds(20), cancellationToken).ConfigureAwait(false);
            if (b is null)
                return;
            if (b.Value is not (ZmodemConstants.CR or ZmodemConstants.CR_HIGH or ZmodemConstants.LF or ZmodemConstants.LF_HIGH or ZmodemConstants.XON))
            {
                PushBack(b.Value);
                return;
            }
        }
    }

    private async Task<byte> ReadHexByteAsync(CancellationToken cancellationToken)
    {
        var hi = FromHex(await ReadRawByteAsync(cancellationToken).ConfigureAwait(false));
        var lo = FromHex(await ReadRawByteAsync(cancellationToken).ConfigureAwait(false));
        if (hi < 0 || lo < 0)
            throw new InvalidDataException("Invalid ZMODEM hex header.");

        return (byte)((hi << 4) | lo);
    }

    private static int FromHex(byte b) =>
        b switch
        {
            >= (byte)'0' and <= (byte)'9' => b - (byte)'0',
            >= (byte)'a' and <= (byte)'f' => b - (byte)'a' + 10,
            >= (byte)'A' and <= (byte)'F' => b - (byte)'A' + 10,
            _ => -1,
        };

    private async Task<ZmodemDataSubpacket> ReadDataSubpacketAsync(CancellationToken cancellationToken)
    {
        var data = new List<byte>(8192);
        ZmodemFrameEnd end;

        while (true)
        {
            var item = await ReadEscapedDataOrFrameEndAsync(cancellationToken).ConfigureAwait(false);
            if (item.FrameEnd is not null)
            {
                end = item.FrameEnd.Value;
                break;
            }

            data.Add(item.Byte!.Value);
        }

        if (_lastHeaderFormat == ZmodemHeaderFormat.Bin32)
        {
            var crcBytes = new byte[4];
            for (var i = 0; i < crcBytes.Length; i++)
                crcBytes[i] = await ReadEscapedDataByteAsync(cancellationToken).ConfigureAwait(false);

            var expected = ZmodemCrc.Crc32(data, (byte)end);
            var actual = BinaryPrimitives.ReadUInt32LittleEndian(crcBytes);
            if (expected != actual)
            {
                Trace($"RX data CRC32 mismatch end={end} len={data.Count} expected=0x{expected:x8} actual=0x{actual:x8}");
                throw new InvalidDataException("ZMODEM CRC32 mismatch.");
            }
        }
        else
        {
            var crcBytes = new byte[2];
            for (var i = 0; i < crcBytes.Length; i++)
                crcBytes[i] = await ReadEscapedDataByteAsync(cancellationToken).ConfigureAwait(false);

            var expected = ZmodemCrc.Crc16(data, (byte)end);
            var actual = BinaryPrimitives.ReadUInt16BigEndian(crcBytes);
            if (expected != actual)
            {
                Trace($"RX data CRC16 mismatch end={end} len={data.Count} expected=0x{expected:x4} actual=0x{actual:x4}");
                throw new InvalidDataException("ZMODEM CRC16 mismatch.");
            }
        }

        Trace($"RX data end={end} len={data.Count} crcFormat={_lastHeaderFormat}");
        return new ZmodemDataSubpacket(data.ToArray(), end);
    }

    private async Task<byte> ReadEscapedDataByteAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var item = await ReadEscapedDataOrFrameEndAsync(cancellationToken).ConfigureAwait(false);
            if (item.Byte is not null)
                return item.Byte.Value;
        }
    }

    private async Task<ZmodemEscapedRead> ReadEscapedDataOrFrameEndAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var b = await ReadRawByteAsync(cancellationToken).ConfigureAwait(false);
            if (b is ZmodemConstants.XON or ZmodemConstants.XOFF or ZmodemConstants.XON_HIGH or ZmodemConstants.XOFF_HIGH)
                continue;

            if (b != ZmodemConstants.ZDLE)
                return ZmodemEscapedRead.Data(b);

            var escaped = await ReadRawByteAsync(cancellationToken).ConfigureAwait(false);
            if (escaped is (byte)ZmodemFrameEnd.ZCRCE
                or (byte)ZmodemFrameEnd.ZCRCG
                or (byte)ZmodemFrameEnd.ZCRCQ
                or (byte)ZmodemFrameEnd.ZCRCW)
            {
                return ZmodemEscapedRead.End((ZmodemFrameEnd)escaped);
            }

            if (escaped == ZmodemConstants.ZRUB0)
                return ZmodemEscapedRead.Data(0x7f);
            if (escaped == ZmodemConstants.ZRUB1)
                return ZmodemEscapedRead.Data(0xff);
            if ((escaped & 0x60) == 0x40)
                return ZmodemEscapedRead.Data((byte)(escaped ^ 0x40));

            return ZmodemEscapedRead.Data(escaped);
        }
    }

    private ValueTask<byte> ReadRawByteAsync(CancellationToken cancellationToken)
    {
        if (_pushback.Count > 0)
            return ValueTask.FromResult(_pushback.Dequeue());

        return _readByteAsync(cancellationToken);
    }

    private async Task<byte?> TryReadRawByteAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            return await ReadRawByteAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private void PushBack(byte b) => _pushback.Enqueue(b);

    private Task SendHeaderHexAsync(ZmodemHeaderType type, long argument, CancellationToken cancellationToken)
    {
        Span<byte> payload = stackalloc byte[4];
        FillPositionBytes(payload, argument);
        return SendHeaderHexAsync(type, payload, cancellationToken);
    }

    private Task SendHeaderHexAsync(
        ZmodemHeaderType type,
        ReadOnlySpan<byte> payload,
        CancellationToken cancellationToken)
    {
        Span<byte> header = stackalloc byte[5];
        FillHeaderBytes(header, type, payload);
        var crc = ZmodemCrc.Crc16(header);

        var output = new List<byte>(32) { ZmodemConstants.ZPAD, ZmodemConstants.ZPAD, ZmodemConstants.ZDLE, ZmodemConstants.ZHEX };
        AppendHex(output, header);
        AppendHex(output, (byte)(crc >> 8));
        AppendHex(output, (byte)crc);
        output.Add(ZmodemConstants.CR);
        output.Add(ZmodemConstants.LF_HIGH);
        if (type is not (ZmodemHeaderType.ZFIN or ZmodemHeaderType.ZACK))
            output.Add(ZmodemConstants.XON);
        Trace($"TX header type={type} format=Hex payload={FormatHex(payload)} crc=0x{crc:x4}");
        return WriteRawAsync(output.ToArray(), cancellationToken);
    }

    private Task SendHeaderBinary32Async(ZmodemHeaderType type, long argument, CancellationToken cancellationToken)
    {
        Span<byte> payload = stackalloc byte[4];
        FillPositionBytes(payload, argument);
        return SendHeaderBinary32Async(type, payload, cancellationToken);
    }

    private Task SendHeaderBinary32Async(
        ZmodemHeaderType type,
        ReadOnlySpan<byte> payload,
        CancellationToken cancellationToken)
    {
        Span<byte> header = stackalloc byte[5];
        FillHeaderBytes(header, type, payload);
        var crc = ZmodemCrc.Crc32(header);

        var output = new List<byte>(32) { ZmodemConstants.ZPAD, ZmodemConstants.ZDLE, ZmodemConstants.ZBIN32 };
        AppendEscaped(output, header);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(crcBytes, crc);
        AppendEscaped(output, crcBytes);
        Trace($"TX header type={type} format=Bin32 payload={FormatHex(payload)} crc=0x{crc:x8}");
        return WriteRawAsync(output.ToArray(), cancellationToken);
    }

    private Task SendDataSubpacketAsync(ReadOnlyMemory<byte> data, ZmodemFrameEnd end, CancellationToken cancellationToken)
    {
        var output = new List<byte>(data.Length + 16);
        AppendEscaped(output, data.Span);
        output.Add(ZmodemConstants.ZDLE);
        output.Add((byte)end);

        var crc = ZmodemCrc.Crc32(data.Span, (byte)end);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(crcBytes, crc);
        AppendEscaped(output, crcBytes);
        if (end == ZmodemFrameEnd.ZCRCW)
            output.Add(ZmodemConstants.XON);

        Trace($"TX data end={end} len={data.Length} crc32=0x{crc:x8}");
        return WriteRawAsync(output.ToArray(), cancellationToken);
    }

    private Task WriteRawAsync(byte[] bytes, CancellationToken cancellationToken) =>
        _writeAsync(bytes, cancellationToken);

    private void Trace(string message) => _trace?.Invoke(message);

    private static string FormatHex(ReadOnlySpan<byte> bytes) =>
        BitConverter.ToString(bytes.ToArray());

    private static void FillHeaderBytes(Span<byte> header, ZmodemHeaderType type, ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 4)
            throw new ArgumentException("ZMODEM header payload must be exactly 4 bytes.", nameof(payload));

        header[0] = (byte)type;
        payload.CopyTo(header[1..]);
    }

    private static void FillPositionBytes(Span<byte> payload, long argument)
    {
        if (payload.Length != 4)
            throw new ArgumentException("ZMODEM position payload must be exactly 4 bytes.", nameof(payload));

        var arg = unchecked((uint)argument);
        payload[0] = (byte)arg;
        payload[1] = (byte)(arg >> 8);
        payload[2] = (byte)(arg >> 16);
        payload[3] = (byte)(arg >> 24);
    }

    private static byte[] HeaderBytesForFlags(int zf0 = 0, int zf1 = 0, int zf2 = 0, int zf3 = 0) =>
    [
        (byte)zf3,
        (byte)zf2,
        (byte)zf1,
        (byte)zf0,
    ];

    private static void AppendHex(List<byte> output, ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
            AppendHex(output, b);
    }

    private static void AppendHex(List<byte> output, byte b)
    {
        const string digits = "0123456789abcdef";
        output.Add((byte)digits[b >> 4]);
        output.Add((byte)digits[b & 0x0f]);
    }

    private static void AppendEscaped(List<byte> output, ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
            AppendEscaped(output, b);
    }

    private static void AppendEscaped(List<byte> output, byte b)
    {
        if (ShouldEscape(b))
        {
            output.Add(ZmodemConstants.ZDLE);
            if (b == 0x7f)
                output.Add(ZmodemConstants.ZRUB0);
            else if (b == 0xff)
                output.Add(ZmodemConstants.ZRUB1);
            else
                output.Add((byte)(b ^ 0x40));
            return;
        }

        output.Add(b);
    }

    private static bool ShouldEscape(byte b) =>
        b == ZmodemConstants.ZDLE
        || b is ZmodemConstants.XON or ZmodemConstants.XOFF or ZmodemConstants.XON_HIGH or ZmodemConstants.XOFF_HIGH
        || b < 0x20
        || b == 0x7f
        || (b >= 0x80 && b < 0xa0)
        || b == 0xff;

    private static string GetUniqueDestinationPath(string folder, string remoteFileName)
    {
        var fileName = SanitizeRemoteFileName(remoteFileName);
        var candidate = Path.Combine(folder, fileName);
        if (!File.Exists(candidate))
            return candidate;

        var name = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var i = 1; ; i++)
        {
            candidate = Path.Combine(folder, $"{name} ({i}){extension}");
            if (!File.Exists(candidate))
                return candidate;
        }
    }

    private static string SanitizeRemoteFileName(string remoteFileName)
    {
        var normalized = remoteFileName.Replace('\\', '/');
        var fileName = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (string.IsNullOrWhiteSpace(fileName))
            fileName = "download";

        foreach (var c in Path.GetInvalidFileNameChars())
            fileName = fileName.Replace(c, '_');

        return fileName is "." or ".." ? "download" : fileName;
    }

    private static void TrySetLastWriteTime(string path, DateTimeOffset modifiedAt)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, modifiedAt.UtcDateTime);
        }
        catch
        {
            // Best effort only; the transfer has already completed.
        }
    }
}

internal enum ZmodemHeaderFormat
{
    Hex,
    Bin16,
    Bin32,
}

internal enum ZmodemHeaderType : byte
{
    ZRQINIT = 0,
    ZRINIT = 1,
    ZSINIT = 2,
    ZACK = 3,
    ZFILE = 4,
    ZSKIP = 5,
    ZNAK = 6,
    ZABORT = 7,
    ZFIN = 8,
    ZRPOS = 9,
    ZDATA = 10,
    ZEOF = 11,
    ZFERR = 12,
    ZCRC = 13,
    ZCHALLENGE = 14,
    ZCOMPL = 15,
    ZCAN = 16,
    ZFREECNT = 17,
    ZCOMMAND = 18,
    ZSTDERR = 19,
}

internal enum ZmodemFrameEnd : byte
{
    ZCRCE = (byte)'h',
    ZCRCG = (byte)'i',
    ZCRCQ = (byte)'j',
    ZCRCW = (byte)'k',
}

internal readonly record struct ZmodemHeader(
    ZmodemHeaderType Type,
    long Argument,
    ZmodemHeaderFormat Format,
    byte Zf0,
    byte Zf1,
    byte Zf2,
    byte Zf3)
{
    public static ZmodemHeader FromBytes(ZmodemHeaderFormat format, ReadOnlySpan<byte> bytes)
    {
        var arg = (uint)bytes[1]
                  | ((uint)bytes[2] << 8)
                  | ((uint)bytes[3] << 16)
                  | ((uint)bytes[4] << 24);
        return new ZmodemHeader(
            (ZmodemHeaderType)bytes[0],
            arg,
            format,
            bytes[4],
            bytes[3],
            bytes[2],
            bytes[1]);
    }
}

internal readonly record struct ZmodemDataSubpacket(byte[] Data, ZmodemFrameEnd End);

internal readonly record struct ZmodemEscapedRead(byte? Byte, ZmodemFrameEnd? FrameEnd)
{
    public static ZmodemEscapedRead Data(byte value) => new(value, null);

    public static ZmodemEscapedRead End(ZmodemFrameEnd value) => new(null, value);
}

internal sealed record ZmodemFileInfo(string FileName, long? Size, DateTimeOffset? ModifiedAt)
{
    public static ZmodemFileInfo Parse(byte[] data)
    {
        var firstNull = Array.IndexOf(data, (byte)0);
        if (firstNull < 0)
            return new ZmodemFileInfo(Encoding.UTF8.GetString(data).Trim(), null, null);

        var name = Encoding.UTF8.GetString(data, 0, firstNull).Trim();
        var metadataLength = Array.IndexOf(data, (byte)0, firstNull + 1) is var secondNull && secondNull >= 0
            ? secondNull - firstNull - 1
            : data.Length - firstNull - 1;
        var metadata = metadataLength > 0
            ? Encoding.ASCII.GetString(data, firstNull + 1, metadataLength)
            : "";
        var parts = metadata.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        long? size = parts.Length > 0 && long.TryParse(parts[0], out var parsedSize) ? parsedSize : null;
        DateTimeOffset? modified = null;
        if (parts.Length > 1 && long.TryParse(parts[1], out var unixSeconds))
            modified = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);

        return new ZmodemFileInfo(name, size, modified);
    }

    public static byte[] Build(string fileName, long size, DateTime modifiedUtc)
    {
        var unixSeconds = new DateTimeOffset(DateTime.SpecifyKind(modifiedUtc, DateTimeKind.Utc)).ToUnixTimeSeconds();
        var text = $"{fileName}\0{size} {unixSeconds} 100644 0 1 {size}\0";
        return Encoding.UTF8.GetBytes(text);
    }
}

internal static class ZmodemCrc
{
    private static readonly uint[] Crc32Table = BuildCrc32Table();

    public static ushort Crc16(ReadOnlySpan<byte> bytes)
    {
        var crc = 0;
        foreach (var b in bytes)
            crc = UpdateCrc16(crc, b);

        return (ushort)crc;
    }

    public static ushort Crc16(IReadOnlyList<byte> bytes, byte end)
    {
        var crc = 0;
        foreach (var b in bytes)
            crc = UpdateCrc16(crc, b);

        crc = UpdateCrc16(crc, end);
        return (ushort)crc;
    }

    public static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xffffffffu;
        foreach (var b in bytes)
            crc = UpdateCrc32(crc, b);

        return ~crc;
    }

    public static uint Crc32(ReadOnlySpan<byte> bytes, byte end)
    {
        var crc = 0xffffffffu;
        foreach (var b in bytes)
            crc = UpdateCrc32(crc, b);

        crc = UpdateCrc32(crc, end);
        return ~crc;
    }

    public static uint Crc32(IReadOnlyList<byte> bytes, byte end)
    {
        var crc = 0xffffffffu;
        foreach (var b in bytes)
            crc = UpdateCrc32(crc, b);

        crc = UpdateCrc32(crc, end);
        return ~crc;
    }

    private static int UpdateCrc16(int crc, byte b)
    {
        crc ^= b << 8;
        for (var i = 0; i < 8; i++)
            crc = (crc & 0x8000) != 0
                ? ((crc << 1) ^ 0x1021) & 0xffff
                : (crc << 1) & 0xffff;

        return crc;
    }

    private static uint UpdateCrc32(uint crc, byte b) =>
        Crc32Table[(crc ^ b) & 0xff] ^ (crc >> 8);

    private static uint[] BuildCrc32Table()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            var crc = i;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xedb88320u : crc >> 1;
            table[i] = crc;
        }

        return table;
    }
}

internal static class ZmodemConstants
{
    public const byte ZPAD = (byte)'*';
    public const byte ZDLE = 0x18;
    public const byte ZBIN = (byte)'A';
    public const byte ZHEX = (byte)'B';
    public const byte ZBIN32 = (byte)'C';
    public const byte ZRUB0 = (byte)'l';
    public const byte ZRUB1 = (byte)'m';
    public const byte XON = 0x11;
    public const byte XOFF = 0x13;
    public const byte XON_HIGH = 0x91;
    public const byte XOFF_HIGH = 0x93;
    public const byte CR = 0x0d;
    public const byte CR_HIGH = 0x8d;
    public const byte LF = 0x0a;
    public const byte LF_HIGH = 0x8a;
    public const byte CAN = 0x18;

    public const int CANFDX = 0x01;
    public const int CANOVIO = 0x02;
    public const int CANBRK = 0x04;
    public const int CANFC32 = 0x20;
    public const int ZCBIN = 0x01;

    public static readonly byte[] CancelSequence =
    [
        CAN, CAN, CAN, CAN, CAN,
        0x08, 0x08, 0x08, 0x08, 0x08,
    ];
}
