using System.IO.Compression;
using System.Text;
using DummyFileApi.Generators;

namespace DummyFileApi.Tests.Generators;

public class ZipWriterTests
{
    [Fact]
    public async Task InMemoryEntries_RoundTripThroughZipArchive()
    {
        var parts = new (string Name, byte[] Content)[]
        {
            ("[Content_Types].xml", "<Types/>"u8.ToArray()),
            ("word/document.xml", "<document>body</document>"u8.ToArray()),
            ("docProps/core.xml", Array.Empty<byte>()),
        };

        using var stream = new MemoryStream();
        var writer = new ZipWriter(stream);
        foreach (var (name, content) in parts)
        {
            await writer.AddEntryAsync(name, content);
        }

        await writer.FinishAsync();
        stream.Position = 0;

        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        Assert.Equal(parts.Select(p => p.Name), archive.Entries.Select(e => e.FullName));

        foreach (var (name, content) in parts)
        {
            using var entryStream = archive.GetEntry(name)!.Open();
            using var buffer = new MemoryStream();
            entryStream.CopyTo(buffer);
            Assert.Equal(content, buffer.ToArray());
        }
    }

    [Fact]
    public async Task OverheadFor_MatchesAnEmptyArchivesActualLength()
    {
        string[] names = ["a.xml", "nested/b.xml", "c"];

        using var stream = new MemoryStream();
        var writer = new ZipWriter(stream);
        foreach (var name in names)
        {
            await writer.AddEntryAsync(name, Array.Empty<byte>());
        }

        await writer.FinishAsync();

        Assert.Equal(ZipWriter.OverheadFor(names), stream.Length);
    }

    [Fact]
    public async Task RepeatingCrc32_MatchesTheBytesWriteRepeatingProduces()
    {
        var pattern = "abcdefghij"u8.ToArray();
        const long length = 65536 + 55; // spans an internal chunk boundary

        using var written = new MemoryStream();
        await ZipWriter.WriteRepeatingAsync(written, pattern, length, CancellationToken.None);

        var expected = System.IO.Hashing.Crc32.HashToUInt32(written.ToArray());
        Assert.Equal(expected, ZipWriter.RepeatingCrc32(pattern, length));
    }

    [Fact]
    public async Task AddEntryAsync_EntryPastFourGiB_ThrowsInsteadOfTruncating()
    {
        using var stream = new MemoryStream();
        var writer = new ZipWriter(stream);

        // Guard fires before writeContent runs, so nothing is streamed.
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => writer.AddEntryAsync("big", 5_000_000_000L, crc32: 0, (_, _) => Task.CompletedTask));
    }

    [Fact]
    public async Task FinishAsync_CalledTwice_Throws()
    {
        using var stream = new MemoryStream();
        var writer = new ZipWriter(stream);
        await writer.AddEntryAsync("a", "x"u8.ToArray());
        await writer.FinishAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => writer.FinishAsync());
    }
}
