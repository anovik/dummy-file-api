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
    [InlineData("docx")]
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

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 20)]
    [InlineData(50, 100)]
    [InlineData(1_000_000, 100)]
    public void TryValidatePagination_ValidParams_ReturnsTrue(int page, int pageSize)
    {
        var result = RequestValidation.TryValidatePagination(page, pageSize, out var error);

        Assert.True(result);
        Assert.Null(error);
    }

    [Theory]
    [InlineData(0, 20, "page")]
    [InlineData(-1, 20, "page")]
    [InlineData(1_000_001, 20, "page")]
    [InlineData(int.MaxValue, 100, "page")]
    [InlineData(1, 0, "pageSize")]
    [InlineData(1, 101, "pageSize")]
    public void TryValidatePagination_InvalidParams_ReturnsFalseWithError(int page, int pageSize, string expectedInError)
    {
        var result = RequestValidation.TryValidatePagination(page, pageSize, out var error);

        Assert.False(result);
        Assert.NotNull(error);
        Assert.Contains(expectedInError, error);
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
