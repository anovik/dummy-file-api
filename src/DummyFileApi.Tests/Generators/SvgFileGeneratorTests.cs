using System.Text;
using System.Xml.Linq;
using DummyFileApi.Generators;

namespace DummyFileApi.Tests.Generators;

public class SvgFileGeneratorTests
{
    private readonly SvgFileGenerator _generator = new();

    public static IEnumerable<object[]> TargetSizes()
    {
        var min = new SvgFileGenerator().MinSizeBytes;
        yield return [min]; // minimal canvas, empty <desc>
        yield return [min + 1];
        yield return [1024L];
        yield return [65536L]; // exactly one internal chunk boundary
        yield return [65537L];
        yield return [512 * 1024L];
        yield return [2 * 1024 * 1024 + 7L];
    }

    [Theory]
    [MemberData(nameof(TargetSizes))]
    public async Task GenerateAsync_ProducesExactRequestedByteCount(long targetSizeBytes)
    {
        using var stream = new MemoryStream();

        await _generator.GenerateAsync(stream, targetSizeBytes, seed: null);

        Assert.Equal(targetSizeBytes, stream.Length);
    }

    [Theory]
    [MemberData(nameof(TargetSizes))]
    public async Task GenerateAsync_OutputIsWellFormedXmlRootedAtSvg(long targetSizeBytes)
    {
        using var stream = new MemoryStream();
        await _generator.GenerateAsync(stream, targetSizeBytes, seed: 3);
        stream.Position = 0;

        var doc = XDocument.Load(stream);

        Assert.Equal("svg", doc.Root!.Name.LocalName);
        Assert.Equal("http://www.w3.org/2000/svg", doc.Root.Name.NamespaceName);
        Assert.Contains(doc.Root.Elements(), e => e.Name.LocalName == "rect");
    }

    [Fact]
    public async Task GenerateAsync_LargerRequest_DrawsMoreRects()
    {
        var small = await RectCount(_generator.MinSizeBytes + 200);
        var large = await RectCount(200_000);

        Assert.True(large > small, $"expected more rects at 200 KB ({large}) than near min ({small})");
    }

    [Fact]
    public async Task GenerateAsync_FinalDescAbsorbsTheRemainder()
    {
        var target = _generator.MinSizeBytes + 40;
        using var stream = new MemoryStream();
        await _generator.GenerateAsync(stream, target, seed: null);

        Assert.Equal(target, stream.Length);

        stream.Position = 0;
        var desc = XDocument.Load(stream).Root!.Elements()
            .First(e => e.Name.LocalName == "desc").Value;

        Assert.StartsWith("the-quick-bro", desc);
        Assert.DoesNotContain("<", desc);
    }

    [Fact]
    public async Task GenerateAsync_DefaultSeed_UsesFirstPaletteColor()
    {
        using var stream = new MemoryStream();
        await _generator.GenerateAsync(stream, targetSizeBytes: 4096, seed: null);

        Assert.Contains("#6495ED", Encoding.ASCII.GetString(stream.ToArray()));
    }

    [Fact]
    public async Task GenerateAsync_SeedPicksPaletteColor()
    {
        using var stream = new MemoryStream();
        await _generator.GenerateAsync(stream, targetSizeBytes: 4096, seed: 1);

        Assert.Contains("#E94F37", Encoding.ASCII.GetString(stream.ToArray()));
    }

    [Fact]
    public async Task GenerateAsync_SeedChangesBytesButNotSize()
    {
        using var first = new MemoryStream();
        using var second = new MemoryStream();

        await _generator.GenerateAsync(first, targetSizeBytes: 8192, seed: null);
        await _generator.GenerateAsync(second, targetSizeBytes: 8192, seed: 2);

        Assert.Equal(first.Length, second.Length);
        Assert.NotEqual(first.ToArray(), second.ToArray());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public async Task GenerateAsync_NegativeSeed_StillHitsExactSizeAndValidXml(int seed)
    {
        using var stream = new MemoryStream();

        await _generator.GenerateAsync(stream, targetSizeBytes: 8192, seed: seed);

        Assert.Equal(8192, stream.Length);
        stream.Position = 0;
        Assert.Equal("svg", XDocument.Load(stream).Root!.Name.LocalName);
    }

    [Fact]
    public async Task GenerateAsync_IsDeterministic()
    {
        using var first = new MemoryStream();
        using var second = new MemoryStream();

        await _generator.GenerateAsync(first, targetSizeBytes: 50_000, seed: 5);
        await _generator.GenerateAsync(second, targetSizeBytes: 50_000, seed: 5);

        Assert.Equal(first.ToArray(), second.ToArray());
    }

    [Fact]
    public async Task GenerateAsync_BelowMinSize_Throws()
    {
        using var stream = new MemoryStream();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _generator.GenerateAsync(stream, _generator.MinSizeBytes - 1, seed: null));
    }

    [Fact]
    public async Task GenerateAsync_AtMinSize_HasAnEmptyDesc()
    {
        using var stream = new MemoryStream();
        await _generator.GenerateAsync(stream, _generator.MinSizeBytes, seed: null);
        stream.Position = 0;

        var desc = XDocument.Load(stream).Root!.Elements()
            .First(e => e.Name.LocalName == "desc");

        Assert.Equal(string.Empty, desc.Value);
    }

    [Fact]
    public void MinSizeBytes_IsTheMinimalScaffold()
    {
        Assert.Equal(258, _generator.MinSizeBytes);
    }

    private async Task<int> RectCount(long targetSizeBytes)
    {
        using var stream = new MemoryStream();
        await _generator.GenerateAsync(stream, targetSizeBytes, seed: null);
        stream.Position = 0;
        return XDocument.Load(stream).Root!.Elements().Count(e => e.Name.LocalName == "rect");
    }
}
