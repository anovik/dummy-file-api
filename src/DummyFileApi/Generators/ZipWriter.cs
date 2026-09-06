using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;

namespace DummyFileApi.Generators;

/// <summary>
/// Streams a ZIP archive of STORE-method (uncompressed) entries straight to an
/// output stream. Local headers and entry data are written as each entry is
/// added; only the small per-entry central-directory metadata is held in
/// memory until <see cref="FinishAsync"/>. Shared by the zip, docx, and xlsx
/// generators — docx/xlsx are OOXML, i.e. a ZIP of fixed XML parts.
/// </summary>
public sealed class ZipWriter(Stream output)
{
    public const int LocalHeaderBaseSize = 30;
    public const int CentralHeaderBaseSize = 46;
    public const int EndOfCentralDirectorySize = 22;

    // Fixed 1980-01-01 timestamp keeps output byte-for-byte deterministic.
    private const ushort DosTime = 0;
    private const ushort DosDate = (0 << 9) | (1 << 5) | 1;
    private const ushort VersionNeeded = 20; // 2.0 — the floor for STORE
    private const int MaxChunkSize = 64 * 1024;

    private readonly List<Entry> _entries = [];
    private long _offset;
    private bool _finished;

    /// <summary>Total non-content bytes a stored archive of these entry names occupies.</summary>
    public static long OverheadFor(params ReadOnlySpan<string> entryNames)
    {
        long total = EndOfCentralDirectorySize;
        foreach (var name in entryNames)
        {
            total += LocalHeaderBaseSize + CentralHeaderBaseSize + 2L * Encoding.UTF8.GetByteCount(name);
        }

        return total;
    }

    /// <summary>Adds an entry whose full content is already in memory.</summary>
    public Task AddEntryAsync(string name, byte[] content, CancellationToken cancellationToken = default)
        => AddEntryAsync(name, content.Length, Crc32.HashToUInt32(content),
            (stream, ct) => stream.WriteAsync(content, ct).AsTask(), cancellationToken);

    /// <summary>
    /// Adds an entry whose content is streamed by <paramref name="writeContent"/>, which
    /// must write exactly <paramref name="length"/> bytes. The caller supplies
    /// <paramref name="crc32"/> for those bytes (a counting pre-pass) since the local
    /// header carries the CRC ahead of the data.
    /// </summary>
    public async Task AddEntryAsync(
        string name,
        long length,
        uint crc32,
        Func<Stream, CancellationToken, Task> writeContent,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_finished, this);

        var nameBytes = Encoding.UTF8.GetBytes(name);

        // Every size/offset field written below is 32-bit, and the entry count
        // is 16-bit. Refuse rather than silently truncate into a corrupt
        // archive — ZIP64 is out of scope (MaxSizeBytes is 100 MB by default).
        if (length < 0 ||
            (ulong)_offset + (ulong)LocalHeaderBaseSize + (ulong)nameBytes.Length + (ulong)length > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(length),
                "Entry would push the archive past 4 GiB; this writer does not implement ZIP64.");
        }

        if (_entries.Count >= ushort.MaxValue)
        {
            throw new InvalidOperationException(
                "Archive exceeds 65,535 entries; this writer does not implement ZIP64.");
        }

        var header = new byte[LocalHeaderBaseSize + nameBytes.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0x04034b50);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), VersionNeeded);
        // flags (6), method (8) left zero — no flags, STORE.
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(10), DosTime);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(12), DosDate);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(14), crc32);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(18), (uint)length); // compressed
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(22), (uint)length); // uncompressed
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(26), (ushort)nameBytes.Length);
        // extra length (28) left zero.
        nameBytes.CopyTo(header.AsSpan(LocalHeaderBaseSize));

        await output.WriteAsync(header, cancellationToken);
        _entries.Add(new Entry(nameBytes, crc32, length, _offset));
        _offset += header.Length;

        await writeContent(output, cancellationToken);
        _offset += length;
    }

    /// <summary>Writes the central directory and end-of-central-directory record.</summary>
    public async Task FinishAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        _finished = true;

        var centralDirStart = _offset;
        foreach (var entry in _entries)
        {
            var record = new byte[CentralHeaderBaseSize + entry.Name.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(record, 0x02014b50);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), VersionNeeded); // version made by
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(6), VersionNeeded);
            // flags (8), method (10) zero.
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(12), DosTime);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(14), DosDate);
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(16), entry.Crc32);
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(20), (uint)entry.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(24), (uint)entry.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(28), (ushort)entry.Name.Length);
            // extra (30), comment (32), disk (34), internal attrs (36), external attrs (38) zero.
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(42), (uint)entry.LocalHeaderOffset);
            entry.Name.CopyTo(record.AsSpan(CentralHeaderBaseSize));

            await output.WriteAsync(record, cancellationToken);
            _offset += record.Length;
        }

        var eocd = new byte[EndOfCentralDirectorySize];
        BinaryPrimitives.WriteUInt32LittleEndian(eocd, 0x06054b50);
        // disk numbers (4, 6) zero.
        BinaryPrimitives.WriteUInt16LittleEndian(eocd.AsSpan(8), (ushort)_entries.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(eocd.AsSpan(10), (ushort)_entries.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(eocd.AsSpan(12), (uint)(_offset - centralDirStart));
        BinaryPrimitives.WriteUInt32LittleEndian(eocd.AsSpan(16), (uint)centralDirStart);
        // comment length (20) zero.
        await output.WriteAsync(eocd, cancellationToken);
        _offset += eocd.Length;
    }

    /// <summary>
    /// CRC-32 of <paramref name="length"/> bytes of <paramref name="pattern"/> repeated
    /// from its start — the pre-pass companion to <see cref="WriteRepeatingAsync"/>, which
    /// must stream the identical bytes.
    /// </summary>
    public static uint RepeatingCrc32(byte[] pattern, long length)
        => FramedRepeatingCrc32(default, pattern, length, default);

    /// <summary>
    /// CRC-32 of <paramref name="prefix"/>, then <paramref name="length"/> bytes of
    /// <paramref name="pattern"/> repeated from its start, then <paramref name="suffix"/> —
    /// the pre-pass companion to <see cref="WriteFramedRepeatingAsync"/>, for an entry
    /// whose fixed text wraps a repeating filler body.
    /// </summary>
    public static uint FramedRepeatingCrc32(
        ReadOnlySpan<byte> prefix, byte[] pattern, long length, ReadOnlySpan<byte> suffix)
    {
        var crc = new Crc32();
        crc.Append(prefix);

        var chunk = new byte[(int)Math.Min(MaxChunkSize, Math.Max(length, 1))];
        var patternIndex = 0;
        var remaining = length;
        while (remaining > 0)
        {
            var count = (int)Math.Min(chunk.Length, remaining);
            RepeatingFiller.Fill(chunk.AsSpan(0, count), pattern, ref patternIndex);
            crc.Append(chunk.AsSpan(0, count));
            remaining -= count;
        }

        crc.Append(suffix);
        return crc.GetCurrentHashAsUInt32();
    }

    /// <summary>Streams <paramref name="length"/> bytes of <paramref name="pattern"/> repeated from its start.</summary>
    public static Task WriteRepeatingAsync(Stream output, byte[] pattern, long length, CancellationToken cancellationToken)
        => WriteFramedRepeatingAsync(output, default, pattern, length, default, cancellationToken);

    /// <summary>
    /// Streams <paramref name="prefix"/>, then <paramref name="length"/> bytes of
    /// <paramref name="pattern"/> repeated from its start, then <paramref name="suffix"/>.
    /// </summary>
    public static async Task WriteFramedRepeatingAsync(
        Stream output, ReadOnlyMemory<byte> prefix, byte[] pattern, long length,
        ReadOnlyMemory<byte> suffix, CancellationToken cancellationToken)
    {
        if (!prefix.IsEmpty)
        {
            await output.WriteAsync(prefix, cancellationToken);
        }

        await RepeatingFiller.WriteAsync(output, pattern, length, cancellationToken);

        if (!suffix.IsEmpty)
        {
            await output.WriteAsync(suffix, cancellationToken);
        }
    }

    private readonly record struct Entry(byte[] Name, uint Crc32, long Length, long LocalHeaderOffset);
}
