using System.Text;
using DummyFileApi.Generators;
using UglyToad.PdfPig;

namespace DummyFileApi.Tests.Generators;

public class PdfFileGeneratorTests
{
    private readonly PdfFileGenerator _generator = new();

    [Theory]
    [InlineData(740)] // MinSizeBytes: text-only template, zero-length padding stream
    [InlineData(741)]
    [InlineData(2048)] // smallest checkerboard tier
    [InlineData(20 * 1024)]
    [InlineData(100 * 1024)]
    [InlineData(250_000)] // page count is already capped at this size
    [InlineData(1188684)] // padding value still fits its natural 6 digits
    [InlineData(1188685)] // padding value needs a 7th digit: startxref stays valid via a leading zero
    [InlineData(1024 * 1024)]
    [InlineData(2 * 1024 * 1024 + 7)]
    [InlineData(100_000_000)]
    public async Task GenerateAsync_ProducesExactRequestedByteCount(long targetSizeBytes)
    {
        using var stream = new MemoryStream();

        await _generator.GenerateAsync(stream, targetSizeBytes, seed: null);

        Assert.Equal(targetSizeBytes, stream.Length);
    }

    // Every size across the 1 KB and 20 KB grid-tier switches and the windows
    // where the xref offset or the padding /Length gains a digit: the page
    // planner and the padding solve must agree on what fits everywhere.
    [Fact]
    public async Task GenerateAsync_SweepAcrossTierAndDigitBoundaries_AlwaysExact()
    {
        var targets = Enumerable.Range(740, 700)
            .Concat(Enumerable.Range(9_950, 100))
            .Concat(Enumerable.Range(20_430, 100))
            .Concat(Enumerable.Range(99_950, 100));

        foreach (var target in targets)
        {
            using var stream = new MemoryStream();
            await _generator.GenerateAsync(stream, target, seed: null);
            Assert.Equal(target, stream.Length);
        }
    }

    [Theory]
    [InlineData(740)]
    [InlineData(2048)]
    [InlineData(100 * 1024)]
    [InlineData(1024 * 1024)]
    [InlineData(100_000_000)]
    public async Task GenerateAsync_OutputOpensWithCorrectPageDimensions(long targetSizeBytes)
    {
        using var stream = new MemoryStream();
        await _generator.GenerateAsync(stream, targetSizeBytes, seed: null);
        stream.Position = 0;

        using var document = PdfDocument.Open(stream);

        Assert.True(document.NumberOfPages >= 1);
        foreach (var page in document.GetPages())
        {
            Assert.Equal(612, page.Width);
            Assert.Equal(792, page.Height);
        }
    }

    [Fact]
    public async Task GenerateAsync_BelowOneKilobyte_OmitsCheckerboard()
    {
        using var stream = new MemoryStream();
        await _generator.GenerateAsync(stream, _generator.MinSizeBytes, seed: null);
        var text = Encoding.ASCII.GetString(stream.ToArray());

        Assert.DoesNotContain(" re f\n", text);
        Assert.Contains("(Dummy PDF File) Tj", text);
    }

    [Fact]
    public async Task GenerateAsync_LargerRequests_GrowTheCheckerboard()
    {
        int CountSquares(byte[] bytes) => Encoding.ASCII.GetString(bytes).Split(" re f\n").Length - 1;

        using var small = new MemoryStream();
        await _generator.GenerateAsync(small, 2048, seed: null);

        using var medium = new MemoryStream();
        await _generator.GenerateAsync(medium, 100 * 1024, seed: null);

        using var large = new MemoryStream();
        await _generator.GenerateAsync(large, 1024 * 1024, seed: null);

        var smallSquares = CountSquares(small.ToArray());
        var mediumSquares = CountSquares(medium.ToArray());
        var largeSquares = CountSquares(large.ToArray());

        Assert.True(smallSquares > 0);
        Assert.True(mediumSquares > smallSquares);
        Assert.True(largeSquares > mediumSquares);
    }

    [Fact]
    public async Task GenerateAsync_HugeRequest_CapsPageCountInsteadOfGrowingForever()
    {
        using var stream = new MemoryStream();
        await _generator.GenerateAsync(stream, 100_000_000, seed: null);
        stream.Position = 0;

        using var document = PdfDocument.Open(stream);

        // The page cap (40) keeps a "test document" a reasonable size to open;
        // everything beyond what fits in that many pages becomes invisible padding.
        Assert.Equal(40, document.NumberOfPages);
    }

    [Fact]
    public async Task GenerateAsync_FillerTextResetsFillColorToBlack()
    {
        // The checkerboard leaves its last square's color as the active fill
        // color; without an explicit reset, text drawn afterward (including on
        // pages with no title/caption) would silently inherit it.
        using var stream = new MemoryStream();
        await _generator.GenerateAsync(stream, 1024 * 1024, seed: 3);
        var text = Encoding.ASCII.GetString(stream.ToArray());

        var searchFrom = 0;
        while (true)
        {
            var btIndex = text.IndexOf("BT\n", searchFrom, StringComparison.Ordinal);
            if (btIndex < 0)
            {
                break;
            }

            var rgIndex = text.LastIndexOf(" rg\n", btIndex, StringComparison.Ordinal);
            Assert.True(rgIndex >= 0);
            var rgLineStart = text.LastIndexOf('\n', rgIndex - 1) + 1;
            Assert.Equal("0.000 0.000 0.000", text[rgLineStart..rgIndex]);

            searchFrom = btIndex + 3;
        }
    }

    [Fact]
    public async Task GenerateAsync_PaddingObjectIsUnreferenced()
    {
        using var stream = new MemoryStream();
        await _generator.GenerateAsync(stream, targetSizeBytes: 2048, seed: null);
        var text = Encoding.ASCII.GetString(stream.ToArray());

        // At this size the document is a single page: Catalog=1, Pages=2, Font=3,
        // Page=4, Contents=5, and the padding stream is object 6 — referenced by
        // nothing, unlike Contents which the Page object points to.
        Assert.Contains("6 0 obj", text);
        Assert.DoesNotContain("/Contents 6 0 R", text);
        Assert.Contains("/Contents 5 0 R", text);
    }

    [Fact]
    public async Task GenerateAsync_SeedPicksColorWithoutChangingSize()
    {
        using var defaultStream = new MemoryStream();
        using var seededStream = new MemoryStream();

        await _generator.GenerateAsync(defaultStream, targetSizeBytes: 100_000, seed: null);
        await _generator.GenerateAsync(seededStream, targetSizeBytes: 100_000, seed: 1);

        Assert.Equal(defaultStream.Length, seededStream.Length);
        Assert.NotEqual(defaultStream.ToArray(), seededStream.ToArray());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public async Task GenerateAsync_NegativeSeed_ProducesValidOutput(int seed)
    {
        using var stream = new MemoryStream();

        await _generator.GenerateAsync(stream, targetSizeBytes: 100_000, seed: seed);

        Assert.Equal(100_000, stream.Length);
        stream.Position = 0;
        using var document = PdfDocument.Open(stream);
        Assert.True(document.NumberOfPages >= 1);
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
            () => _generator.GenerateAsync(stream, targetSizeBytes: _generator.MinSizeBytes - 1, seed: null));
    }

    [Fact]
    public void MinSizeBytes_IsTextOnlyTemplateWithZeroLengthPaddingStream()
    {
        // header (9) + Catalog/Pages/Font objects (49+57+70=176) + Page +
        // Contents for the one page, title/caption text only, no checkerboard
        // (126+177=303) + zero-length padding stream/xref/trailer (252)
        Assert.Equal(740, _generator.MinSizeBytes);
    }
}
