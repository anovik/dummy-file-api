using DummyFileApi.Generators;

namespace DummyFileApi.Tests.Generators;

public class TxtFileGeneratorTests
{
    private readonly TxtFileGenerator _generator = new();

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(50)]
    [InlineData(1024)]
    [InlineData(65536)] // exactly one internal chunk boundary
    [InlineData(65537)] // one byte past a chunk boundary
    [InlineData(2 * 1024 * 1024 + 7)] // spans multiple chunks with a partial remainder
    public async Task GenerateAsync_ProducesExactRequestedByteCount(long targetSizeBytes)
    {
        using var stream = new MemoryStream();

        await _generator.GenerateAsync(stream, targetSizeBytes, seed: null);

        Assert.Equal(targetSizeBytes, stream.Length);
    }

    [Fact]
    public async Task GenerateAsync_OutputIsValidUtf8Text()
    {
        using var stream = new MemoryStream();

        await _generator.GenerateAsync(stream, targetSizeBytes: 500, seed: null);

        var text = System.Text.Encoding.UTF8.GetString(stream.ToArray());
        Assert.Equal(500, text.Length);
        Assert.Contains("quick brown fox", text);
    }

    [Fact]
    public async Task GenerateAsync_BelowMinSize_Throws()
    {
        using var stream = new MemoryStream();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _generator.GenerateAsync(stream, targetSizeBytes: 0, seed: null));
    }

    [Fact]
    public void MinSizeBytes_IsOne()
    {
        Assert.Equal(1, _generator.MinSizeBytes);
    }
}
