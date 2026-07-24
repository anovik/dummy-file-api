using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;

namespace DummyFileApi.Generators;

public sealed class PngFileGenerator : IFileGenerator
{
    private const int MaxChunkSize = 64 * 1024;

    // The canvas is the largest square whose template fits the byte budget,
    // capped to keep decoded memory bounded; tEXt padding absorbs the rest.
    private const int MinSide = 16;
    private const int MaxSide = 4096;

    // Checkerboard of colored and fully transparent squares. Pixel content is
    // free to vary because stored (uncompressed) deflate blocks make the IDAT
    // length depend on dimensions only, never on pixel values.
    private const int SquareSize = 8;
    private const int BytesPerPixel = 4; // RGBA

    // tEXt payload: registered keyword, then Latin-1 text with no NUL bytes.
    private const string PaddingKeyword = "Comment";
    private const string Padding = "the-quick-brown-fox-jumps-over-the-lazy-dog-";

    // Seed picks the opaque square color; transparent squares are always zero.
    private static readonly byte[][] Palette =
    [
        [0x64, 0x95, 0xED],
        [0xE9, 0x4F, 0x37],
        [0x2E, 0xCC, 0x71],
        [0xF3, 0x9C, 0x12],
        [0x9B, 0x59, 0xB6],
        [0x1A, 0xBC, 0x9C],
        [0xE7, 0x4C, 0x8C],
        [0x34, 0x49, 0x5E],
    ];

    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] IendChunk = BuildIendChunk();

    private const int SignatureLength = 8;
    private const int IhdrChunkLength = 25;
    private const int IendChunkLength = 12;

    public string TypeKey => "png";
    public string MimeType => "image/png";
    public string FileExtension => "png";

    // Smallest canvas + tEXt chunk with zero-length text + IEND. The padding
    // text grows one byte at a time, so every size >= this is reachable.
    public long MinSizeBytes => TotalSizeFor(MinSide);

    public async Task GenerateAsync(Stream output, long targetSizeBytes, int? seed, CancellationToken cancellationToken = default)
    {
        if (targetSizeBytes < MinSizeBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(targetSizeBytes));
        }

        var side = LargestSideFitting(targetSizeBytes);
        var paddingLength = targetSizeBytes - TotalSizeFor(side);
        var color = Palette[seed is int s ? ((s % Palette.Length) + Palette.Length) % Palette.Length : 0];

        await output.WriteAsync(Signature, cancellationToken);
        await output.WriteAsync(BuildIhdrChunk(side), cancellationToken);
        await WriteIdatAsync(output, side, color, cancellationToken);
        await WriteTextChunkAsync(output, paddingLength, cancellationToken);
        await output.WriteAsync(IendChunk, cancellationToken);
    }

    // Whole-file size for a side-length canvas with an empty tEXt chunk.
    private static long TotalSizeFor(int side)
    {
        var raw = RawImageLength(side);
        var blocks = (raw + ushort.MaxValue - 1) / ushort.MaxValue;
        var zlib = 2 + 5 * blocks + raw + 4;
        var emptyText = 4 + 4 + PaddingKeyword.Length + 1 + 4;
        return SignatureLength + IhdrChunkLength + (12 + zlib) + emptyText + IendChunkLength;
    }

    private static long RawImageLength(int side) => (long)side * (1 + (long)side * BytesPerPixel);

    private static int LargestSideFitting(long targetSizeBytes)
    {
        var lo = MinSide;
        var hi = MaxSide;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (TotalSizeFor(mid) <= targetSizeBytes)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return lo;
    }

    private static async Task WriteIdatAsync(Stream output, int side, byte[] color, CancellationToken cancellationToken)
    {
        var rawLength = RawImageLength(side);
        var blockCount = (rawLength + ushort.MaxValue - 1) / ushort.MaxValue;
        var zlibLength = 2 + 5 * blockCount + rawLength + 4;

        var header = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)zlibLength);
        Encoding.ASCII.GetBytes("IDAT", header.AsSpan(4));
        await output.WriteAsync(header, cancellationToken);

        // The chunk CRC covers the type and data fields, not the length.
        var crc = new Crc32();
        crc.Append(header.AsSpan(4));

        var zlibHeader = new byte[] { 0x78, 0x01 }; // deflate, 32K window, valid check bits
        crc.Append(zlibHeader);
        await output.WriteAsync(zlibHeader, cancellationToken);

        // Checker rows flip every SquareSize scanlines, so two cached row
        // variants cover the whole image.
        var rowLength = 1 + side * BytesPerPixel;
        var evenRow = BuildScanline(side, color, startWithColor: true);
        var oddRow = BuildScanline(side, color, startWithColor: false);

        // Hand-rolled stored (uncompressed) deflate blocks: the BCL's
        // DeflateStream doesn't guarantee byte-stable output across runtime
        // versions, and the exact-size guarantee needs a fixed IDAT length.
        var adler = new Adler32();
        var block = new byte[ushort.MaxValue];
        var blockHeader = new byte[5];
        long rawOffset = 0;
        while (rawOffset < rawLength)
        {
            var blockLength = (int)Math.Min(ushort.MaxValue, rawLength - rawOffset);
            var filled = 0;
            while (filled < blockLength)
            {
                var absolute = rawOffset + filled;
                var rowIndex = (int)(absolute / rowLength);
                var withinRow = (int)(absolute % rowLength);
                var source = rowIndex / SquareSize % 2 == 0 ? evenRow : oddRow;
                var count = Math.Min(rowLength - withinRow, blockLength - filled);
                source.AsSpan(withinRow, count).CopyTo(block.AsSpan(filled));
                filled += count;
            }

            blockHeader[0] = rawOffset + blockLength == rawLength ? (byte)1 : (byte)0; // BFINAL + BTYPE 00
            BinaryPrimitives.WriteUInt16LittleEndian(blockHeader.AsSpan(1), (ushort)blockLength);
            BinaryPrimitives.WriteUInt16LittleEndian(blockHeader.AsSpan(3), (ushort)~blockLength);
            crc.Append(blockHeader);
            await output.WriteAsync(blockHeader, cancellationToken);

            adler.Append(block.AsSpan(0, blockLength));
            crc.Append(block.AsSpan(0, blockLength));
            await output.WriteAsync(block.AsMemory(0, blockLength), cancellationToken);

            rawOffset += blockLength;
        }

        var trailer = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(trailer, adler.Value);
        crc.Append(trailer);
        await output.WriteAsync(trailer, cancellationToken);

        var crcBytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc.GetCurrentHashAsUInt32());
        await output.WriteAsync(crcBytes, cancellationToken);
    }

    private static byte[] BuildScanline(int side, byte[] color, bool startWithColor)
    {
        var row = new byte[1 + side * BytesPerPixel]; // filter byte 0 (None), then pixels
        for (var x = 0; x < side; x++)
        {
            if (x / SquareSize % 2 == 0 != startWithColor)
            {
                continue; // transparent square: all-zero RGBA
            }

            var pos = 1 + x * BytesPerPixel;
            row[pos] = color[0];
            row[pos + 1] = color[1];
            row[pos + 2] = color[2];
            row[pos + 3] = 0xFF;
        }

        return row;
    }

    private static async Task WriteTextChunkAsync(Stream output, long paddingLength, CancellationToken cancellationToken)
    {
        // Chunk layout: length, type, keyword + NUL + text, CRC. PNG chunk
        // lengths are 32-bit, so one chunk covers any allowed target size.
        var header = new byte[4 + 4 + PaddingKeyword.Length + 1];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)(PaddingKeyword.Length + 1 + paddingLength));
        Encoding.ASCII.GetBytes("tEXt", header.AsSpan(4, 4));
        Encoding.ASCII.GetBytes(PaddingKeyword, header.AsSpan(8));

        var crc = new Crc32();
        crc.Append(header.AsSpan(4));

        await output.WriteAsync(header, cancellationToken);

        if (paddingLength > 0)
        {
            var chunk = new byte[(int)Math.Min(MaxChunkSize, paddingLength)];
            for (var i = 0; i < chunk.Length; i++)
            {
                chunk[i] = (byte)Padding[i % Padding.Length];
            }

            var remaining = paddingLength;
            while (remaining > 0)
            {
                var count = (int)Math.Min(chunk.Length, remaining);
                crc.Append(chunk.AsSpan(0, count));
                await output.WriteAsync(chunk.AsMemory(0, count), cancellationToken);
                remaining -= count;
            }
        }

        var crcBytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc.GetCurrentHashAsUInt32());
        await output.WriteAsync(crcBytes, cancellationToken);
    }

    private static byte[] BuildIhdrChunk(int side)
    {
        using var stream = new MemoryStream();
        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)side);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr[4..], (uint)side);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 6;  // color type: truecolor with alpha
        ihdr[10] = 0; // compression: deflate
        ihdr[11] = 0; // filter method
        ihdr[12] = 0; // interlace: none
        WriteChunk(stream, "IHDR", ihdr);
        return stream.ToArray();
    }

    private static byte[] BuildIendChunk()
    {
        using var stream = new MemoryStream();
        WriteChunk(stream, "IEND", []);
        return stream.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, (uint)data.Length);
        stream.Write(buffer);

        Span<byte> typeBytes = stackalloc byte[4];
        Encoding.ASCII.GetBytes(type, typeBytes);
        stream.Write(typeBytes);
        stream.Write(data);

        var crc = new Crc32();
        crc.Append(typeBytes);
        crc.Append(data);
        BinaryPrimitives.WriteUInt32BigEndian(buffer, crc.GetCurrentHashAsUInt32());
        stream.Write(buffer);
    }

    private sealed class Adler32
    {
        private const uint Modulus = 65521;

        // Largest run whose worst case keeps s2 below uint overflow.
        private const int MaxRun = 5552;

        private uint _s1 = 1;
        private uint _s2;

        public uint Value => (_s2 << 16) | _s1;

        public void Append(ReadOnlySpan<byte> data)
        {
            var i = 0;
            while (i < data.Length)
            {
                var end = i + Math.Min(MaxRun, data.Length - i);
                for (; i < end; i++)
                {
                    _s1 += data[i];
                    _s2 += _s1;
                }

                _s1 %= Modulus;
                _s2 %= Modulus;
            }
        }
    }
}
