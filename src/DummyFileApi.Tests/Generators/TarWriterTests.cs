using System.Formats.Tar;
using DummyFileApi.Generators;
using TarWriter = DummyFileApi.Generators.TarWriter;

namespace DummyFileApi.Tests.Generators;

public class TarWriterTests
{
    [Fact]
    public async Task WriteEntryAsync_RoundTripsThroughTarReader()
    {
        var content = "hello tar"u8.ToArray();

        using var stream = new MemoryStream();
        await TarWriter.WriteEntryAsync(stream, "readme.txt", content.Length,
            (s, ct) => s.WriteAsync(content, ct).AsTask());
        await TarWriter.WriteEndOfArchiveAsync(stream);
        stream.Position = 0;

        using var reader = new TarReader(stream);
        var entry = reader.GetNextEntry();
        Assert.NotNull(entry);
        Assert.Equal("readme.txt", entry.Name);
        Assert.Equal(content.Length, entry.Length);

        using var buffer = new MemoryStream();
        entry.DataStream!.CopyTo(buffer);
        Assert.Equal(content, buffer.ToArray());

        Assert.Null(reader.GetNextEntry());
    }

    [Fact]
    public async Task WriteEntryAsync_PadsContentToTheNextBlockBoundary()
    {
        using var stream = new MemoryStream();
        await TarWriter.WriteEntryAsync(stream, "a", 10, (s, ct) => s.WriteAsync(new byte[10], ct).AsTask());

        Assert.Equal(TarWriter.BlockSize + TarWriter.BlockSize, stream.Length); // header + one padded block
    }

    [Fact]
    public async Task OverheadFor_MatchesAnEmptyEntrysActualLength()
    {
        using var stream = new MemoryStream();
        await TarWriter.WriteEntryAsync(stream, "readme.txt", 0, (_, _) => Task.CompletedTask);
        await TarWriter.WriteEndOfArchiveAsync(stream);

        Assert.Equal(TarWriter.OverheadFor("readme.txt"), stream.Length);
    }

    [Fact]
    public void OverheadFor_NameTooLongForTheNameField_Throws()
    {
        var longName = new string('a', 100);

        Assert.Throws<ArgumentOutOfRangeException>(() => TarWriter.OverheadFor(longName));
    }

    [Fact]
    public async Task WriteEntryAsync_ChecksumIsValidPerUstarSpec()
    {
        // TarReader validates the header checksum while reading; a wrong one throws.
        using var stream = new MemoryStream();
        await TarWriter.WriteEntryAsync(stream, "readme.txt", 5, (s, ct) => s.WriteAsync(new byte[5], ct).AsTask());
        await TarWriter.WriteEndOfArchiveAsync(stream);
        stream.Position = 0;

        using var reader = new TarReader(stream);
        var entry = reader.GetNextEntry();
        Assert.NotNull(entry);
    }
}
