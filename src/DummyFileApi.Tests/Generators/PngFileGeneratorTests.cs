using DummyFileApi.Generators;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace DummyFileApi.Tests.Generators;

public class PngFileGeneratorTests
{
    private readonly PngFileGenerator _generator = new();

    [Theory]
    [InlineData(1128)] // MinSizeBytes: 16x16 canvas + empty tEXt padding
    [InlineData(1129)]
    [InlineData(2048)]
    [InlineData(65756)] // one byte below the smallest two-deflate-block layout
    [InlineData(65757)] // smallest canvas needing two stored deflate blocks (side 128)
    [InlineData(1024 * 1024)]
    [InlineData(2 * 1024 * 1024 + 7)]
    public async Task GenerateAsync_ProducesExactRequestedByteCount(long targetSizeBytes)
    {
        using var stream = new MemoryStream();

        await _generator.GenerateAsync(stream, targetSizeBytes, seed: null);

        Assert.Equal(targetSizeBytes, stream.Length);
    }

    [Theory]
    [InlineData(1128, 16)]
    [InlineData(1129, 16)]
    [InlineData(65757, 128)]
    [InlineData(1024 * 1024, 511)]
    public async Task GenerateAsync_OutputDecodesWithScaledDimensions(long targetSizeBytes, int expectedSide)
    {
        using var stream = new MemoryStream();
        await _generator.GenerateAsync(stream, targetSizeBytes, seed: null);
        stream.Position = 0;

        using var image = Image.Load<Rgba32>(stream);

        Assert.Equal(expectedSide, image.Width);
        Assert.Equal(expectedSide, image.Height);
    }

    [Fact]
    public async Task GenerateAsync_DrawsCheckerboardWithTransparentSquares()
    {
        using var stream = new MemoryStream();
        await _generator.GenerateAsync(stream, targetSizeBytes: 2048, seed: null);
        stream.Position = 0;

        using var image = Image.Load<Rgba32>(stream);

        var colored = new Rgba32(0x64, 0x95, 0xED, 0xFF);
        var transparent = new Rgba32(0, 0, 0, 0);

        // 8-pixel squares: sampling one pixel per quadrant of the first
        // 16x16 tile covers both row phases of the pattern.
        Assert.Equal(colored, image[0, 0]);
        Assert.Equal(transparent, image[8, 0]);
        Assert.Equal(transparent, image[0, 8]);
        Assert.Equal(colored, image[8, 8]);
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
        using var defaultImage = Image.Load<Rgba32>(defaultStream);
        using var seededImage = Image.Load<Rgba32>(seededStream);

        Assert.NotEqual(defaultImage[0, 0], seededImage[0, 0]);
        Assert.Equal(0xFF, seededImage[0, 0].A);
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
        using var image = Image.Load<Rgba32>(stream);
        Assert.Equal(0xFF, image[0, 0].A);
    }

    [Fact]
    public async Task GenerateAsync_PaddingLandsInTextChunk()
    {
        using var stream = new MemoryStream();
        await _generator.GenerateAsync(stream, targetSizeBytes: 2048, seed: null);
        stream.Position = 0;

        using var image = Image.Load(stream);

        var text = Assert.Single(image.Metadata.GetPngMetadata().TextData);
        Assert.Equal("Comment", text.Keyword);
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
            () => _generator.GenerateAsync(stream, targetSizeBytes: 1127, seed: null));
    }

    [Fact]
    public void MinSizeBytes_IsSmallestCanvasPlusEmptyPaddingChunk()
    {
        // signature (8) + IHDR (25) + IDAT (12 + 2 + 5 + 16*(1+16*4) + 4)
        // + empty tEXt (12 + "Comment" + NUL) + IEND (12)
        Assert.Equal(1128, _generator.MinSizeBytes);
    }
}
