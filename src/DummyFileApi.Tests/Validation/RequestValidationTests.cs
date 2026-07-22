using DummyFileApi.Generators;
using DummyFileApi.Validation;

namespace DummyFileApi.Tests.Validation;

public class RequestValidationTests
{
    [Theory]
    [InlineData("txt")]
    [InlineData("TXT")]
    [InlineData(" txt ")]
    public void TryValidateType_KnownType_ReturnsTrue(string type)
    {
        var result = RequestValidation.TryValidateType(type, out var key, out var error);

        Assert.True(result);
        Assert.Equal("txt", key);
        Assert.Null(error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("doc")]
    [InlineData("pdf")]
    public void TryValidateType_UnknownType_ReturnsFalseWithError(string? type)
    {
        var result = RequestValidation.TryValidateType(type, out _, out var error);

        Assert.False(result);
        Assert.NotNull(error);
        Assert.Contains("txt", error);
    }

    [Fact]
    public void TryValidateSize_ValidSize_ReturnsTrue()
    {
        var result = RequestValidation.TryValidateSize("1KB", out var bytes, out var error);

        Assert.True(result);
        Assert.Equal(1024, bytes);
        Assert.Null(error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("notasize")]
    public void TryValidateSize_InvalidSize_ReturnsFalseWithError(string? size)
    {
        var result = RequestValidation.TryValidateSize(size, out var bytes, out var error);

        Assert.False(result);
        Assert.Equal(0, bytes);
        Assert.NotNull(error);
    }

    private static readonly TxtFileGenerator Generator = new();

    [Fact]
    public void TryValidateBounds_WithinBounds_ReturnsTrue()
    {
        var result = RequestValidation.TryValidateBounds(1024, Generator, maxSizeBytes: 100_000_000, out var error);

        Assert.True(result);
        Assert.Null(error);
    }

    [Fact]
    public void TryValidateBounds_BelowMin_ReturnsFalseWithError()
    {
        var result = RequestValidation.TryValidateBounds(0, Generator, maxSizeBytes: 100_000_000, out var error);

        Assert.False(result);
        Assert.Contains("at least", error);
    }

    [Fact]
    public void TryValidateBounds_AboveMax_ReturnsFalseWithError()
    {
        var result = RequestValidation.TryValidateBounds(200_000_000, Generator, maxSizeBytes: 100_000_000, out var error);

        Assert.False(result);
        Assert.Contains("not exceed", error);
    }
}
