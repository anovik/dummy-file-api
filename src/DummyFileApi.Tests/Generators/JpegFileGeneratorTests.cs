using DummyFileApi.Generators;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace DummyFileApi.Tests.Generators;

public class JpegFileGeneratorTests
{
    private readonly JpegFileGenerator _generator = new();

    [Theory]
    [InlineData(205)] // MinSizeBytes: 16x16 template + empty COM padding segment
    [InlineData(206)]
    [InlineData(2048)]
    [InlineData(100 * 1024)]
    [InlineData(1024 * 1024)]
    [InlineData(2 * 1024 * 1024 + 7)]
    [InlineData(4 * 1024 * 1024)] // past the 4096-px canvas cap: padding chains multiple COM segments
    public async Task GenerateAsync_ProducesExactRequestedByteCount(long targetSizeBytes)
    {
        using var stream = new MemoryStream();

        await _generator.GenerateAsync(stream, targetSizeBytes, seed: null);

        Assert.Equal(targetSizeBytes, stream.Length);
    }

    [Theory]
    [InlineData(205, 16)]
    [InlineData(2048, 136)]
    [InlineData(1024 * 1024, 3304)]
    [InlineData(4 * 1024 * 1024, 4096)]
    public async Task GenerateAsync_OutputDecodesWithScaledDimensions(long targetSizeBytes, int expectedSide)
    {
        using var stream = new MemoryStream();
        await _generator.GenerateAsync(stream, targetSizeBytes, seed: null);
        stream.Position = 0;

        using var image = Image.Load<Rgb24>(stream);

        Assert.Equal(expectedSide, image.Width);
        Assert.Equal(expectedSide, image.Height);
    }

    [Fact]
    public async Task GenerateAsync_DrawsCheckerboardWithWhiteSquares()
    {
        using var stream = new MemoryStream();
        await _generator.GenerateAsync(stream, targetSizeBytes: 2048, seed: null);
        stream.Position = 0;

        using var image = Image.Load<Rgb24>(stream);

        // 8-pixel squares: one pixel per quadrant of the first 16x16 tile
        // covers both row phases. Colors allow YCbCr round-trip rounding.
        AssertNear(new Rgb24(0x64, 0x95, 0xED), image[0, 0]); // cornflower blue
        AssertNear(new Rgb24(0xFF, 0xFF, 0xFF), image[8, 0]);
        AssertNear(new Rgb24(0xFF, 0xFF, 0xFF), image[0, 8]);
        AssertNear(new Rgb24(0x64, 0x95, 0xED), image[8, 8]);
    }

    [Fact]
    public async Task GenerateAsync_SeedPicksColorWithoutChangingSize()
    {
        using var defaultStream = new MemoryStream();
        using var seededStream = new MemoryStream();

        await _generator.GenerateAsync(defaultStream, targetSizeBytes: 2048, seed: null);
        await _generator.GenerateAsync(seededStream, targetSizeBytes: 2048, seed: 1);

        Assert.Equal(defaultStream.Length, seededStream.Length);

        defaultStream.Position = 0;
        seededStream.Position = 0;
        using var defaultImage = Image.Load<Rgb24>(defaultStream);
        using var seededImage = Image.Load<Rgb24>(seededStream);

        Assert.NotEqual(defaultImage[0, 0], seededImage[0, 0]);
    }

    [Fact]
    public async Task GenerateAsync_EveryPaletteColorHitsExactSizeAndDecodes()
    {
        // Scan lengths differ per color, so each seed exercises its own
        // padding math — including at the shared minimum size.
        for (var seed = 0; seed < 8; seed++)
        {
            foreach (var size in new[] { _generator.MinSizeBytes, 2048L })
            {
                using var stream = new MemoryStream();
                await _generator.GenerateAsync(stream, size, seed);

                Assert.Equal(size, stream.Length);
                stream.Position = 0;
                using var image = Image.Load<Rgb24>(stream);
                Assert.Equal(image.Width, image.Height);
            }
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public async Task GenerateAsync_NegativeSeed_ProducesValidOutput(int seed)
    {
        using var stream = new MemoryStream();

        await _generator.GenerateAsync(stream, targetSizeBytes: 2048, seed: seed);

        Assert.Equal(2048, stream.Length);
        stream.Position = 0;
        using var image = Image.Load<Rgb24>(stream);
        Assert.Equal(image.Width, image.Height);
    }

    [Fact]
    public async Task GenerateAsync_PaddingLandsInCommentSegment()
    {
        using var stream = new MemoryStream();
        await _generator.GenerateAsync(stream, targetSizeBytes: 2048, seed: null);
        var bytes = stream.ToArray();

        // SOI (2) + APP0 (18), then the COM padding segment: FF FE, big-endian
        // length counting itself, then the filler text.
        Assert.Equal(0xFF, bytes[20]);
        Assert.Equal(0xFE, bytes[21]);

        var declaredLength = (bytes[22] << 8) | bytes[23];
        var payload = System.Text.Encoding.ASCII.GetString(bytes, 24, declaredLength - 2);
        Assert.StartsWith("the-quick-brown-fox", payload);
    }

    [Fact]
    public async Task GenerateAsync_IsDeterministic()
    {
        using var first = new MemoryStream();
        using var second = new MemoryStream();

        await _generator.GenerateAsync(first, targetSizeBytes: 5000, seed: 3);
        await _generator.GenerateAsync(second, targetSizeBytes: 5000, seed: 3);

        Assert.Equal(first.ToArray(), second.ToArray());
    }

    [Fact]
    public async Task GenerateAsync_BelowMinSize_Throws()
    {
        using var stream = new MemoryStream();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _generator.GenerateAsync(stream, targetSizeBytes: 204, seed: null));
    }

    [Fact]
    public void MinSizeBytes_IsSmallestCanvasPlusEmptyCommentSegment()
    {
        // SOI + APP0 (20) + DQT (69) + SOF0 (19) + DHT (33 + 22) + SOS (14)
        // + 16x16 entropy-coded scan (22) + EOI (2) + empty COM (4)
        Assert.Equal(205, _generator.MinSizeBytes);
    }

    private static void AssertNear(Rgb24 expected, Rgb24 actual)
    {
        Assert.InRange(actual.R, expected.R - 3, expected.R + 3);
        Assert.InRange(actual.G, expected.G - 3, expected.G + 3);
        Assert.InRange(actual.B, expected.B - 3, expected.B + 3);
    }
}
