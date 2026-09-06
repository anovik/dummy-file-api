using System.Buffers.Binary;

namespace DummyFileApi.Generators;

/// <summary>
/// Framing for DEFLATE stored (uncompressed, BTYPE 00) blocks. Used where the
/// output must be byte-stable and its length known up front — something the
/// BCL's <c>DeflateStream</c> doesn't guarantee. Shared by
/// <see cref="PngFileGenerator"/> (inside a zlib wrapper) and
/// <see cref="GzipFileGenerator"/> (raw).
/// </summary>
internal static class StoredDeflate
{
    /// <summary>Maximum payload bytes in one stored block — the 16-bit LEN field.</summary>
    public const int MaxBlockSize = ushort.MaxValue;

    /// <summary>Bytes a stored block adds around its payload: BFINAL/BTYPE byte, LEN, NLEN.</summary>
    public const int BlockHeaderSize = 5;

    /// <summary>Stored blocks a payload of this length occupies — at least one, for the final (possibly empty) block.</summary>
    public static long BlockCount(long payloadLength) =>
        payloadLength <= 0 ? 1 : (payloadLength + MaxBlockSize - 1) / MaxBlockSize;

    /// <summary>Writes the 5-byte stored-block header: BFINAL/BTYPE, then LEN and its one's-complement.</summary>
    public static void WriteBlockHeader(Span<byte> destination, int blockLength, bool isFinal)
    {
        destination[0] = isFinal ? (byte)1 : (byte)0; // BFINAL bit set on the last block, BTYPE 00
        BinaryPrimitives.WriteUInt16LittleEndian(destination[1..], (ushort)blockLength);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[3..], (ushort)~blockLength);
    }
}
