using System.IO.Compression;
using System.Text;
using DummyFileApi.Generators;

namespace DummyFileApi.Tests.Generators;

public class GzipFileGeneratorTests
{
    private readonly GzipFileGenerator _generator = new();

    // 10-byte fixed header + "readme.txt\0" FNAME (11) + one empty final stored
    // block (5) + 8-byte trailer.
    private const int MinSize = 34;

    [Theory]
    [InlineData(MinSize)]      // MinSizeBytes: header + one empty final stored block + trailer
    [InlineData(MinSize + 1)]  // one payload byte
    [InlineData(1024)]
    [InlineData(65536)]        // spans the internal 64 KB streaming buffer
    [InlineData(65569)]        // largest total a single stored block can produce
    [InlineData(65570)]        // first total that needs a second block — the boundary the block count steps over
    [InlineData(65574)]
    [InlineData(200_000)]      // several stored blocks
    [InlineData(2 * 1024 * 1024 + 7)]
    public async Task GenerateAsync_ProducesExactRequestedByteCount(long targetSizeBytes)
    {
        using var stream = new MemoryStream();

        await _generator.GenerateAsync(stream, targetSizeBytes, seed: null);

        Assert.Equal(targetSizeBytes, stream.Length);
    }

    [Fact]
    public async Task GenerateAsync_EverySizeAcrossTheBlockBoundaryIsExact()
    {
        // The 6-byte total jump at each 65535-byte block boundary would leave a
        // band of sizes unreachable if the block count weren't raised to fill it.
        for (var target = 65_560L; target <= 65_585L; target++)
        {
            using var stream = new MemoryStream();
            await _generator.GenerateAsync(stream, target, seed: null);
            Assert.Equal(target, stream.Length);
        }
    }

    [Theory]
    [InlineData(MinSize)]
    [InlineData(1024)]
    [InlineData(65570)]
    [InlineData(200_000)]
    public async Task GenerateAsync_DecompressesToRepeatedSeededFiller(long targetSizeBytes)
    {
        using var stream = new MemoryStream();
        await _generator.GenerateAsync(stream, targetSizeBytes, seed: 2);
        stream.Position = 0;

        var payload = Decompress(stream);

        // GZipStream throws on a bad CRC-32 or ISIZE, so reaching here is the structural check.
        var phrase = "How vexingly quick daft zebras jump. ";
        var expected = Encoding.ASCII.GetString(Tile(phrase, payload.Length));
        Assert.Equal(expected, Encoding.ASCII.GetString(payload));
    }

    [Fact]
    public async Task GenerateAsync_HeaderCarriesReadmeTxtAsTheOriginalFilename()
    {
        using var stream = new MemoryStream();
        await _generator.GenerateAsync(stream, targetSizeBytes: 1024, seed: null);
        var bytes = stream.ToArray();

        Assert.Equal(0x08, bytes[3] & 0x08); // FLG.FNAME set
        var nul = Array.IndexOf(bytes, (byte)0, 10);
        Assert.Equal("readme.txt", Encoding.Latin1.GetString(bytes, 10, nul - 10));
    }

    [Fact]
    public async Task GenerateAsync_MultiBlockPayload_RoundTripsIntact()
    {
        using var stream = new MemoryStream();
        await _generator.GenerateAsync(stream, targetSizeBytes: 180_000, seed: null);
        stream.Position = 0;

        var payload = Decompress(stream);

        Assert.True(payload.Length > 65535); // more than one stored block's worth
        Assert.Equal(Tile(FillerPhrasesFirst, payload.Length), payload);
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
        Assert.Equal(4096 - MinSize, Decompress(stream).Length); // 4 KB fits one stored block; payload = target - overhead
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
            () => _generator.GenerateAsync(stream, targetSizeBytes: MinSize - 1, seed: null));
    }

    [Fact]
    public void MinSizeBytes_IsHeaderPlusEmptyBlockPlusTrailer()
    {
        // 10-byte header + "readme.txt\0" (11) + 5-byte empty final stored block + 8-byte trailer
        Assert.Equal(MinSize, _generator.MinSizeBytes);
    }

    private const string FillerPhrasesFirst = "The quick brown fox jumps over the lazy dog. ";

    private static byte[] Decompress(Stream gzip)
    {
        using var gz = new GZipStream(gzip, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gz.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] Tile(string phrase, int length)
    {
        var source = Encoding.ASCII.GetBytes(phrase);
        var result = new byte[length];
        for (var i = 0; i < length; i++)
        {
            result[i] = source[i % source.Length];
        }

        return result;
    }
}
