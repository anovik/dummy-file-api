using System.IO.Compression;
using System.Text;
using DummyFileApi.Generators;

namespace DummyFileApi.Tests.Generators;

public class ZipFileGeneratorTests
{
    private readonly ZipFileGenerator _generator = new();

    [Theory]
    [InlineData(118)] // MinSizeBytes: one stored entry with zero-length content
    [InlineData(119)]
    [InlineData(1024)]
    [InlineData(65536)]
    [InlineData(65654)] // 118 + 65536: content spans exactly one filler chunk
    [InlineData(65655)]
    [InlineData(2 * 1024 * 1024 + 7)]
    public async Task GenerateAsync_ProducesExactRequestedByteCount(long targetSizeBytes)
    {
        using var stream = new MemoryStream();

        await _generator.GenerateAsync(stream, targetSizeBytes, seed: null);

        Assert.Equal(targetSizeBytes, stream.Length);
    }

    [Theory]
    [InlineData(118)]
    [InlineData(119)]
    [InlineData(4096)]
    [InlineData(2 * 1024 * 1024 + 7)]
    public async Task GenerateAsync_OpensAsZipWithOneStoredEntry(long targetSizeBytes)
    {
        using var stream = new MemoryStream();
        await _generator.GenerateAsync(stream, targetSizeBytes, seed: null);
        stream.Position = 0;

        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var entry = Assert.Single(archive.Entries);

        Assert.Equal("readme.txt", entry.FullName);
        Assert.Equal(targetSizeBytes - _generator.MinSizeBytes, entry.Length);
        Assert.Equal(entry.Length, entry.CompressedLength); // STORE: no compression

        // Reading to the end validates the entry's CRC-32 against the header.
        using var entryStream = entry.Open();
        using var reader = new StreamReader(entryStream, Encoding.ASCII);
        var content = reader.ReadToEnd();
        Assert.Equal(entry.Length, content.Length);
    }

    [Fact]
    public async Task GenerateAsync_EntryContentIsTheSeededFillerPhrase()
    {
        using var stream = new MemoryStream();
        await _generator.GenerateAsync(stream, targetSizeBytes: 4096, seed: 2);
        stream.Position = 0;

        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        using var reader = new StreamReader(archive.Entries[0].Open(), Encoding.ASCII);
        var content = reader.ReadToEnd();

        Assert.StartsWith("How vexingly quick daft zebras jump. ", content);
    }

    [Fact]
    public async Task GenerateAsync_SeedPicksPhraseWithoutChangingSize()
    {
        using var first = new MemoryStream();
        using var second = new MemoryStream();

        await _generator.GenerateAsync(first, targetSizeBytes: 4096, seed: null);
        await _generator.GenerateAsync(second, targetSizeBytes: 4096, seed: 1);

        Assert.Equal(first.Length, second.Length);
        Assert.NotEqual(first.ToArray(), second.ToArray());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public async Task GenerateAsync_NegativeSeed_ProducesValidOutput(int seed)
    {
        using var stream = new MemoryStream();

        await _generator.GenerateAsync(stream, targetSizeBytes: 4096, seed: seed);

        Assert.Equal(4096, stream.Length);
        stream.Position = 0;
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        Assert.Single(archive.Entries);
    }

    [Fact]
    public async Task GenerateAsync_IsDeterministic()
    {
        using var first = new MemoryStream();
        using var second = new MemoryStream();

        await _generator.GenerateAsync(first, targetSizeBytes: 50_000, seed: 3);
        await _generator.GenerateAsync(second, targetSizeBytes: 50_000, seed: 3);

        Assert.Equal(first.ToArray(), second.ToArray());
    }

    [Fact]
    public async Task GenerateAsync_BelowMinSize_Throws()
    {
        using var stream = new MemoryStream();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _generator.GenerateAsync(stream, targetSizeBytes: 117, seed: null));
    }

    [Fact]
    public void MinSizeBytes_IsArchiveOverheadForOneEmptyEntry()
    {
        // EOCD (22) + local header (30) + central header (46) + 2 * "readme.txt" (10)
        Assert.Equal(118, _generator.MinSizeBytes);
    }
}
