using System.Globalization;
using System.Text;
using CsvHelper;
using DummyFileApi.Generators;

namespace DummyFileApi.Tests.Generators;

public class CsvFileGeneratorTests
{
    private readonly CsvFileGenerator _generator = new();

    [Theory]
    [InlineData(26)] // MinSizeBytes: header + minimal row
    [InlineData(27)]
    [InlineData(100)]
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

    [Theory]
    [InlineData(26)]
    [InlineData(27)]
    [InlineData(1024)]
    [InlineData(65537)]
    public async Task GenerateAsync_OutputParsesAsCsvWithConsistentColumns(long targetSizeBytes)
    {
        using var stream = new MemoryStream();
        await _generator.GenerateAsync(stream, targetSizeBytes, seed: null);
        stream.Position = 0;

        using var reader = new StreamReader(stream, Encoding.ASCII);
        using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);

        Assert.True(csv.Read());
        Assert.True(csv.ReadHeader());
        Assert.Equal(new[] { "Id", "Name", "Value" }, csv.HeaderRecord);

        var rowCount = 0;
        while (csv.Read())
        {
            rowCount++;
            Assert.Equal(3, csv.Parser.Count);
            Assert.Equal(rowCount.ToString(CultureInfo.InvariantCulture), csv.GetField(0));
            Assert.Equal($"Item-{rowCount}", csv.GetField(1));
        }

        Assert.True(rowCount >= 1);
    }

    [Fact]
    public async Task GenerateAsync_UsesCrlfLineEndingsOnly()
    {
        using var stream = new MemoryStream();

        await _generator.GenerateAsync(stream, targetSizeBytes: 1024, seed: null);

        var text = Encoding.ASCII.GetString(stream.ToArray());
        Assert.DoesNotContain("\r\r", text);
        Assert.Equal(text.Count(c => c == '\n'), text.Split("\r\n").Length - 1);
        Assert.EndsWith("\r\n", text);
    }

    [Fact]
    public async Task GenerateAsync_IsDeterministic()
    {
        using var first = new MemoryStream();
        using var second = new MemoryStream();

        await _generator.GenerateAsync(first, targetSizeBytes: 5000, seed: null);
        await _generator.GenerateAsync(second, targetSizeBytes: 5000, seed: null);

        Assert.Equal(first.ToArray(), second.ToArray());
    }

    [Fact]
    public async Task GenerateAsync_BelowMinSize_Throws()
    {
        using var stream = new MemoryStream();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _generator.GenerateAsync(stream, targetSizeBytes: 25, seed: null));
    }

    [Fact]
    public void MinSizeBytes_IsHeaderPlusMinimalRow()
    {
        // "Id,Name,Value\r\n" (15) + "1,Item-1,\r\n" (11)
        Assert.Equal(26, _generator.MinSizeBytes);
    }
}
