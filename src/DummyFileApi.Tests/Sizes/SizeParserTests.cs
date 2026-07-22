using DummyFileApi.Sizes;

namespace DummyFileApi.Tests.Sizes;

public class SizeParserTests
{
    [Theory]
    [InlineData("1B", 1)]
    [InlineData("100B", 100)]
    [InlineData("1KB", 1024)]
    [InlineData("1KiB", 1024)]
    [InlineData("1kb", 1024)]
    [InlineData("1MB", 1024 * 1024)]
    [InlineData("1MiB", 1024 * 1024)]
    [InlineData("2.5MB", 2621440)]
    [InlineData("  1KB  ", 1024)]
    public void TryParse_ValidInput_ReturnsExpectedBytes(string input, long expectedBytes)
    {
        var result = SizeParser.TryParse(input, out var bytes);

        Assert.True(result);
        Assert.Equal(expectedBytes, bytes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("notasize")]
    [InlineData("KB")]
    [InlineData("-1KB")]
    [InlineData("0KB")]
    [InlineData("1GB")]
    [InlineData("1.5.5MB")]
    public void TryParse_InvalidInput_ReturnsFalse(string? input)
    {
        var result = SizeParser.TryParse(input, out var bytes);

        Assert.False(result);
        Assert.Equal(0, bytes);
    }

    [Fact]
    public void TryParse_UsesInvariantCulture_RegardlessOfDecimalSeparator()
    {
        // Guards against a server locale where ',' is the decimal separator
        // silently misparsing "2.5MB" or accepting "2,5MB".
        Assert.True(SizeParser.TryParse("2.5MB", out var bytes));
        Assert.Equal(2621440, bytes);

        Assert.False(SizeParser.TryParse("2,5MB", out _));
    }
}
