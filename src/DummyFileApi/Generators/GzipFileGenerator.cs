using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;

namespace DummyFileApi.Generators;

public sealed class GzipFileGenerator : IFileGenerator
{
    private const int MaxChunkSize = 64 * 1024;

    // Stored in the header's FNAME field so decompressing yields a named file,
    // matching the readme.txt entry zip/tar carry.
    private const string OriginalName = "readme.txt";

    // 10-byte fixed member header + NUL-terminated FNAME. MTIME left zero so
    // output is deterministic.
    private static readonly byte[] Header = BuildHeader();

    // Header (including FNAME) + trailer (CRC-32 + ISIZE); the deflate stream in
    // between is stored blocks whose only variable is the payload length.
    private static readonly long FixedOverhead = Header.Length + 8;

    public string TypeKey => "gzip";
    public string MimeType => "application/gzip";
    public string FileExtension => "gz";

    // Header + one empty final stored block + trailer.
    public long MinSizeBytes => FixedOverhead + StoredDeflate.BlockHeaderSize;

    public async Task GenerateAsync(Stream output, long targetSizeBytes, int? seed, CancellationToken cancellationToken = default)
    {
        if (targetSizeBytes < MinSizeBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(targetSizeBytes));
        }

        // total = FixedOverhead + BlockHeaderSize * blocks + payload. A stored block
        // holds at most MaxBlockSize bytes, and crossing into a new block would leave
        // a 6-byte-wide range of totals unreachable — so raise the block count past
        // the natural minimum when needed, letting empty trailing blocks absorb the
        // gap. Fewest blocks that hold a non-negative payload:
        var budget = targetSizeBytes - FixedOverhead;
        var blocks = Math.Max(1, (budget + StoredDeflate.MaxBlockSize + StoredDeflate.BlockHeaderSize - 1)
            / (StoredDeflate.MaxBlockSize + StoredDeflate.BlockHeaderSize));
        var payloadLength = budget - StoredDeflate.BlockHeaderSize * blocks;

        // seed picks the filler phrase, like zip/tar — it changes the bytes, not the size.
        var phrase = FillerPhrases.AllBytes[SeedIndex.Wrap(seed, FillerPhrases.AllBytes.Length)];

        await output.WriteAsync(Header, cancellationToken);

        var crc = new Crc32();
        var buffer = new byte[(int)Math.Min(MaxChunkSize, Math.Max(payloadLength, 1))];
        var blockHeader = new byte[StoredDeflate.BlockHeaderSize];
        var patternIndex = 0;
        var remaining = payloadLength;

        for (var block = 0; block < blocks; block++)
        {
            // remaining <= (blocks - block) * MaxBlockSize by construction, so the
            // greedy take here always leaves the later blocks able to hold the rest.
            var blockLength = (int)Math.Min(StoredDeflate.MaxBlockSize, remaining);
            StoredDeflate.WriteBlockHeader(blockHeader, blockLength, isFinal: block == blocks - 1);
            await output.WriteAsync(blockHeader, cancellationToken);

            var blockRemaining = blockLength;
            while (blockRemaining > 0)
            {
                var count = Math.Min(buffer.Length, blockRemaining);
                RepeatingFiller.Fill(buffer.AsSpan(0, count), phrase, ref patternIndex);
                crc.Append(buffer.AsSpan(0, count));
                await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                blockRemaining -= count;
            }

            remaining -= blockLength;
        }

        var trailer = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(trailer, crc.GetCurrentHashAsUInt32());
        BinaryPrimitives.WriteUInt32LittleEndian(trailer.AsSpan(4), (uint)payloadLength);
        await output.WriteAsync(trailer, cancellationToken);
    }

    private static byte[] BuildHeader()
    {
        var name = Encoding.Latin1.GetBytes(OriginalName);
        var header = new byte[10 + name.Length + 1];
        header[0] = 0x1F;
        header[1] = 0x8B;
        header[2] = 0x08; // CM: deflate
        header[3] = 0x08; // FLG: FNAME present
        // bytes 4..7 MTIME, byte 8 XFL: all zero
        header[9] = 0xFF; // OS: unknown
        name.CopyTo(header, 10);
        header[^1] = 0x00; // FNAME terminator
        return header;
    }
}
