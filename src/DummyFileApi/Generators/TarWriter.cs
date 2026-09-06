using System.Text;

namespace DummyFileApi.Generators;

/// <summary>
/// Writes a USTAR archive of a single regular-file entry straight to an output stream: a
/// 512-byte header, the content zero-padded to the next 512-byte boundary, then the two
/// 512-byte zero blocks that mark end-of-archive. Every field but the content itself is
/// fixed-width, so the total length is a closed-form function of the content length.
/// </summary>
public static class TarWriter
{
    public const int BlockSize = 512;
    public const long EndOfArchiveSize = 2 * BlockSize;

    private const int NameFieldSize = 100;

    /// <summary>Archive bytes for this entry name excluding content and its block padding: the header plus the end-of-archive marker.</summary>
    public static long OverheadFor(string name)
    {
        ValidateName(name);
        return BlockSize + EndOfArchiveSize;
    }

    /// <summary>Rounds a content length up to the next multiple of <see cref="BlockSize"/>.</summary>
    public static long PaddedLength(long contentLength) =>
        (contentLength + BlockSize - 1) / BlockSize * BlockSize;

    /// <summary>
    /// Writes the entry header and its content (via <paramref name="writeContent"/>, which must write
    /// exactly <paramref name="length"/> bytes), then zero-pads the content out to a full block.
    /// </summary>
    public static async Task WriteEntryAsync(
        Stream output,
        string name,
        long length,
        Func<Stream, CancellationToken, Task> writeContent,
        CancellationToken cancellationToken = default)
    {
        await output.WriteAsync(BuildHeader(name, length), cancellationToken);
        await writeContent(output, cancellationToken);

        var padding = PaddedLength(length) - length;
        if (padding > 0)
        {
            await output.WriteAsync(new byte[padding], cancellationToken);
        }
    }

    /// <summary>Writes the two zero blocks that mark the end of the archive.</summary>
    public static Task WriteEndOfArchiveAsync(Stream output, CancellationToken cancellationToken = default) =>
        output.WriteAsync(new byte[EndOfArchiveSize], cancellationToken).AsTask();

    private static void ValidateName(string name)
    {
        if (Encoding.ASCII.GetByteCount(name) >= NameFieldSize)
        {
            throw new ArgumentOutOfRangeException(nameof(name),
                "USTAR names of 99 bytes or more need the prefix field, which this writer does not implement.");
        }
    }

    private static byte[] BuildHeader(string name, long length)
    {
        ValidateName(name);

        var header = new byte[BlockSize];
        Encoding.ASCII.GetBytes(name).CopyTo(header, 0);
        WriteOctal(header.AsSpan(100, 8), 420);   // mode: 0644
        WriteOctal(header.AsSpan(108, 8), 0);     // uid
        WriteOctal(header.AsSpan(116, 8), 0);     // gid
        WriteOctal(header.AsSpan(124, 12), (ulong)length);
        WriteOctal(header.AsSpan(136, 12), 0);    // mtime — fixed epoch keeps output deterministic
        header.AsSpan(148, 8).Fill((byte)' ');    // chksum field reads as spaces while it's summed
        header[156] = (byte)'0';                  // typeflag: regular file
        "ustar\0"u8.CopyTo(header.AsSpan(257, 6));
        "00"u8.CopyTo(header.AsSpan(263, 2));

        var checksum = 0;
        foreach (var b in header)
        {
            checksum += b;
        }

        // chksum is the one field NUL-then-space terminated, not NUL-padded like the others.
        Encoding.ASCII.GetBytes(Convert.ToString(checksum, 8).PadLeft(6, '0')).CopyTo(header, 148);
        header[154] = 0;
        header[155] = (byte)' ';

        return header;
    }

    private static void WriteOctal(Span<byte> destination, ulong value)
    {
        var digitCount = destination.Length - 1;
        for (var i = digitCount - 1; i >= 0; i--)
        {
            destination[i] = (byte)('0' + (int)(value & 7));
            value >>= 3;
        }

        destination[digitCount] = 0;
    }
}
