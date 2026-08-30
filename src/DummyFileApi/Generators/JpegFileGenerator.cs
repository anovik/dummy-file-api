using System.Buffers.Binary;
using System.Collections.Concurrent;

namespace DummyFileApi.Generators;

public sealed class JpegFileGenerator : IFileGenerator
{
    private const int MaxChunkSize = 64 * 1024;

    // A COM segment's 16-bit length field counts itself, so one segment holds
    // at most 65533 payload bytes; larger padding chains segments back to back.
    private const int SegmentOverhead = 4;
    private const int MaxSegmentPayload = ushort.MaxValue - 2;

    // Checker squares coincide with the 8x8 DCT blocks, so every block is a
    // uniform color with an exactly measurable scan (DC diff + EOB only).
    private const int BlockSize = 8;

    // The canvas is the largest square of whole blocks whose measured size
    // fits the byte budget, capped to keep decoded memory bounded.
    private const int MinBlocksPerSide = 2;
    private const int MaxBlocksPerSide = 512;

    private const int DcCodeLength = 5;
    private const int AcEobCodeLength = 2;

    private const string PaddingText = "the-quick-brown-fox-jumps-over-the-lazy-dog-";

    // Seed picks the colored squares, stored as JFIF YCbCr; the alternate
    // squares are white (JPEG has no transparency).
    private static readonly byte[][] Palette =
    [
        [144, 180, 96],  // cornflower blue (#6495ED)
        [122, 90, 207],  // red-orange (#E94F37)
        [146, 109, 56],  // green (#2ECC71)
        [166, 44, 183],  // orange (#F39C12)
        [119, 163, 153], // purple (#9B59B6)
        [136, 139, 50],  // teal (#1ABC9C)
        [130, 134, 200], // pink (#E74C8C)
        [69, 142, 116],  // slate (#34495E)
    ];

    private static readonly byte[] White = [255, 128, 128];

    private static readonly byte[] Prefix =
    [
        0xFF, 0xD8, // SOI
        0xFF, 0xE0, 0x00, 0x10, // APP0, JFIF
        (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0x00,
        0x01, 0x01, // version 1.1
        0x00, 0x00, 0x01, 0x00, 0x01, // aspect-ratio density 1:1
        0x00, 0x00, // no thumbnail
    ];

    private static readonly byte[] Eoi = [0xFF, 0xD9];

    // Scan lengths are deterministic per canvas and color, so measuring pays
    // only once per combination; keyed on the packed RGB value for value equality.
    private static readonly ConcurrentDictionary<(int BlocksPerSide, int ColorKey), long> ScanLengthCache = new();

    // Everything except the scan and the COM padding has a fixed length: the
    // prefix, the table/frame/scan-header segments, and the trailing EOI.
    private static readonly int FixedOverhead =
        Prefix.Length + BuildHeaderSegments(MinBlocksPerSide * BlockSize).Length + Eoi.Length;

    private static readonly long MinSize = ComputeMinSize();

    public string TypeKey => "jpeg";
    public string MimeType => "image/jpeg";
    public string FileExtension => "jpg";

    // Smallest canvas with one empty COM segment, for the color with the
    // longest scan; the segment payload then grows one byte at a time.
    public long MinSizeBytes => MinSize;

    public async Task GenerateAsync(Stream output, long targetSizeBytes, int? seed, CancellationToken cancellationToken = default)
    {
        if (targetSizeBytes < MinSizeBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(targetSizeBytes));
        }

        var color = Palette[SeedIndex.Wrap(seed, Palette.Length)];
        var (blocksPerSide, scanLength) = LargestCanvasFitting(targetSizeBytes, color);
        var padding = targetSizeBytes - FixedOverhead - scanLength;

        await output.WriteAsync(Prefix, cancellationToken);
        await WriteComSegmentsAsync(output, padding, cancellationToken);
        await output.WriteAsync(BuildHeaderSegments(blocksPerSide * BlockSize), cancellationToken);
        await WriteScanDataAsync(output, blocksPerSide, color, cancellationToken);
        await output.WriteAsync(Eoi, cancellationToken);
    }

    private static (int BlocksPerSide, long ScanLength) LargestCanvasFitting(long targetSizeBytes, byte[] color)
    {
        // Every extra block row adds far more scan bytes than byte stuffing
        // can jitter, so the measured size grows with the canvas and binary
        // search applies.
        var lo = MinBlocksPerSide;
        var loScan = MeasureScanLength(lo, color);
        var hi = MaxBlocksPerSide;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            var scan = MeasureScanLength(mid, color);
            if (FixedOverhead + scan + SegmentOverhead <= targetSizeBytes)
            {
                lo = mid;
                loScan = scan;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return (lo, loScan);
    }

    private static long MeasureScanLength(int blocksPerSide, byte[] color)
    {
        var colorKey = (color[0] << 16) | (color[1] << 8) | color[2];
        return ScanLengthCache.GetOrAdd((blocksPerSide, colorKey), _ =>
            WriteScanDataAsync(Stream.Null, blocksPerSide, color, CancellationToken.None)
                .GetAwaiter().GetResult());
    }

    private static long ComputeMinSize()
    {
        long maxScan = 0;
        foreach (var color in Palette)
        {
            maxScan = Math.Max(maxScan, MeasureScanLength(MinBlocksPerSide, color));
        }

        return FixedOverhead + maxScan + SegmentOverhead;
    }

    private static async Task WriteComSegmentsAsync(Stream output, long remaining, CancellationToken cancellationToken)
    {
        var header = new byte[] { 0xFF, 0xFE, 0, 0 }; // COM
        byte[]? filler = null;

        while (remaining > 0)
        {
            // A full segment can leave a 1-3 byte remainder that no segment
            // can express, so cap the payload to keep at least one further
            // segment header's worth of bytes available.
            var payload = remaining <= SegmentOverhead + MaxSegmentPayload
                ? (int)(remaining - SegmentOverhead)
                : (int)Math.Min(MaxSegmentPayload, remaining - 2 * SegmentOverhead);

            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2), (ushort)(payload + 2));
            await output.WriteAsync(header, cancellationToken);

            var left = payload;
            while (left > 0)
            {
                filler ??= BuildFiller();
                var count = Math.Min(filler.Length, left);
                await output.WriteAsync(filler.AsMemory(0, count), cancellationToken);
                left -= count;
            }

            remaining -= SegmentOverhead + payload;
        }
    }

    private static byte[] BuildFiller()
    {
        var filler = new byte[MaxChunkSize];
        for (var i = 0; i < filler.Length; i++)
        {
            filler[i] = (byte)PaddingText[i % PaddingText.Length];
        }

        return filler;
    }

    private static byte[] BuildHeaderSegments(int side)
    {
        using var stream = new MemoryStream();
        WriteQuantizationTable(stream);
        WriteFrameHeader(stream, side);
        WriteHuffmanTables(stream);
        WriteScanHeader(stream);
        return stream.ToArray();
    }

    private static void WriteQuantizationTable(Stream stream)
    {
        // Table 0, all ones: quantization becomes a no-op, so the checker
        // colors survive the round trip essentially unchanged.
        var payload = new byte[1 + 64];
        payload[0] = 0x00; // 8-bit precision, table id 0
        Array.Fill(payload, (byte)1, 1, 64);
        WriteSegment(stream, 0xDB, payload);
    }

    private static void WriteFrameHeader(Stream stream, int side)
    {
        // SOF0: baseline DCT, 8-bit samples, three components with no
        // subsampling, all using quantization table 0.
        WriteSegment(stream, 0xC0,
        [
            8,
            (byte)(side >> 8), (byte)side,
            (byte)(side >> 8), (byte)side,
            3,
            1, 0x11, 0,
            2, 0x11, 0,
            3, 0x11, 0,
        ]);
    }

    private static void WriteHuffmanTables(Stream stream)
    {
        // DC table 0, shared by all components: the twelve magnitude
        // categories as canonical five-bit codes 0..11.
        var dc = new byte[1 + 16 + 12];
        dc[0] = 0x00;
        dc[DcCodeLength] = 12;
        for (var i = 0; i < 12; i++)
        {
            dc[17 + i] = (byte)i;
        }

        WriteSegment(stream, 0xC4, dc);

        // AC table 0: a single two-bit end-of-block code — every AC
        // coefficient of a uniform block is zero.
        var ac = new byte[1 + 16 + 1];
        ac[0] = 0x10;
        ac[AcEobCodeLength] = 1;
        ac[17] = 0x00;
        WriteSegment(stream, 0xC4, ac);
    }

    private static void WriteScanHeader(Stream stream)
    {
        // SOS: three interleaved components, DC and AC table 0, full spectral range.
        WriteSegment(stream, 0xDA,
        [
            3,
            1, 0x00,
            2, 0x00,
            3, 0x00,
            0, 63, 0,
        ]);
    }

    private static async Task<long> WriteScanDataAsync(Stream output, int blocksPerSide, byte[] color, CancellationToken cancellationToken)
    {
        // Each block's DC diff is measured against the previous block of the
        // same component; with quant 1, a uniform block's DC is 8*(value-128).
        var writer = new BitWriter(output);
        var previous = new int[3];
        for (var blockY = 0; blockY < blocksPerSide; blockY++)
        {
            for (var blockX = 0; blockX < blocksPerSide; blockX++)
            {
                var block = (blockX + blockY) % 2 == 0 ? color : White;
                for (var component = 0; component < 3; component++)
                {
                    var dc = 8 * (block[component] - 128);
                    WriteDcCoefficient(writer, dc - previous[component]);
                    previous[component] = dc;
                    writer.WriteBits(0, AcEobCodeLength);
                }

                if (writer.NeedsFlush)
                {
                    await writer.FlushAsync(cancellationToken);
                }
            }
        }

        writer.FlushWithOneBits();
        await writer.FlushAsync(cancellationToken);
        return writer.TotalBytes;
    }

    private static void WriteDcCoefficient(BitWriter writer, int diff)
    {
        // Huffman-coded magnitude category, then the value's bits; negative
        // values are encoded as diff + 2^size - 1 per the JPEG spec.
        var size = 32 - int.LeadingZeroCount(Math.Abs(diff));
        writer.WriteBits(size, DcCodeLength);
        if (size > 0)
        {
            writer.WriteBits(diff >= 0 ? diff : diff + (1 << size) - 1, size);
        }
    }

    private static void WriteSegment(Stream stream, byte marker, ReadOnlySpan<byte> payload)
    {
        stream.WriteByte(0xFF);
        stream.WriteByte(marker);
        stream.WriteByte((byte)((payload.Length + 2) >> 8));
        stream.WriteByte((byte)(payload.Length + 2));
        stream.Write(payload);
    }

    private sealed class BitWriter(Stream output)
    {
        private readonly byte[] _buffer = new byte[MaxChunkSize];
        private int _length;
        private int _bits;
        private int _bitCount;

        public long TotalBytes { get; private set; }

        // One MCU emits at most 16 bytes including stuffing; flush before the
        // buffer can run out of that headroom.
        public bool NeedsFlush => _length >= _buffer.Length - 16;

        public void WriteBits(int value, int count)
        {
            for (var i = count - 1; i >= 0; i--)
            {
                _bits = (_bits << 1) | ((value >> i) & 1);
                if (++_bitCount < 8)
                {
                    continue;
                }

                _buffer[_length++] = (byte)_bits;
                TotalBytes++;
                if (_bits == 0xFF)
                {
                    _buffer[_length++] = 0x00; // byte stuffing
                    TotalBytes++;
                }

                _bits = 0;
                _bitCount = 0;
            }
        }

        // Entropy-coded data is padded to a byte boundary with 1 bits.
        public void FlushWithOneBits()
        {
            while (_bitCount != 0)
            {
                WriteBits(1, 1);
            }
        }

        public async Task FlushAsync(CancellationToken cancellationToken)
        {
            await output.WriteAsync(_buffer.AsMemory(0, _length), cancellationToken);
            _length = 0;
        }
    }
}
