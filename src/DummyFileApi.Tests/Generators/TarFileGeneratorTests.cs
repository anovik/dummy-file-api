using System.Formats.Tar;
using System.Text;
using DummyFileApi.Generators;

namespace DummyFileApi.Tests.Generators;

public class TarFileGeneratorTests
{
    private readonly TarFileGenerator _generator = new();

    [Theory]
    [InlineData(1536)] // MinSizeBytes: header + two end-of-archive blocks, zero-length content
    [InlineData(1537)] // one byte of sub-512 trailing padding
    [InlineData(2048)] // content fills exactly one 512-byte block
    [InlineData(4096)]
    [InlineData(1536 + 65536)] // content spans exactly one filler chunk
    [InlineData(1536 + 65536 + 1)]
    [InlineData(2 * 1024 * 1024 + 7)]
    public async Task GenerateAsync_ProducesExactRequestedByteCount(long targetSizeBytes)
    {
        using var stream = new MemoryStream();

        await _generator.GenerateAsync(stream, targetSizeBytes, seed: null);

        Assert.Equal(targetSizeBytes, stream.Length);
    }

    [Theory]
    [InlineData(1536)]
    [InlineData(1537)]
    [InlineData(4096)]
    [InlineData(2 * 1024 * 1024 + 7)]
    public async Task GenerateAsync_OpensAsTarWithOneRegularFileEntry(long targetSizeBytes)
    {
        using var stream = new MemoryStream();
        await _generator.GenerateAsync(stream, targetSizeBytes, seed: null);
        stream.Position = 0;

        using var reader = new TarReader(stream);
        var entry = reader.GetNextEntry();
        Assert.NotNull(entry);
        Assert.Equal(TarEntryType.RegularFile, entry.EntryType);
        Assert.Equal("readme.txt", entry.Name);

        // A zero-length entry (the minimum-size case) has no data stream to read.
        if (entry.Length > 0)
        {
            using var buffer = new MemoryStream();
            entry.DataStream!.CopyTo(buffer);
            Assert.Equal(entry.Length, buffer.Length);
        }

        Assert.Null(reader.GetNextEntry());
    }

    [Fact]
    public async Task GenerateAsync_EntryContentIsTheSeededFillerPhrase()
    {
        using var stream = new MemoryStream();
        await _generator.GenerateAsync(stream, targetSizeBytes: 4096, seed: 2);
        stream.Position = 0;

        using var reader = new TarReader(stream);
        var entry = reader.GetNextEntry();
        using var buffer = new MemoryStream();
        entry!.DataStream!.CopyTo(buffer);
        var content = Encoding.ASCII.GetString(buffer.ToArray());

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
        using var reader = new TarReader(stream);
        Assert.NotNull(reader.GetNextEntry());
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
    public async Task GenerateAsync_SubBlockRemainder_IsTrailingZeroPaddingPastEndOfArchive()
    {
        // 4097 is not a multiple of 512, so one byte of zero padding follows the two
        // end-of-archive blocks. TarReader must still read the single entry cleanly.
        using var stream = new MemoryStream();
        await _generator.GenerateAsync(stream, targetSizeBytes: 4097, seed: null);
        stream.Position = 0;

        using var reader = new TarReader(stream);
        var entry = reader.GetNextEntry();
        Assert.NotNull(entry);
        Assert.Null(reader.GetNextEntry());
    }

    [Fact]
    public async Task GenerateAsync_BelowMinSize_Throws()
    {
        using var stream = new MemoryStream();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _generator.GenerateAsync(stream, targetSizeBytes: 1535, seed: null));
    }

    [Fact]
    public void MinSizeBytes_IsHeaderPlusEndOfArchiveForOneEmptyEntry()
    {
        // header (512) + two end-of-archive zero blocks (2 * 512)
        Assert.Equal(1536, _generator.MinSizeBytes);
    }
}
