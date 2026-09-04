using System.Text;
using System.Text.Json;
using DummyFileApi.Generators;

namespace DummyFileApi.Tests.Generators;

public class JsonFileGeneratorTests
{
    private readonly JsonFileGenerator _generator = new();

    public static IEnumerable<object[]> TargetSizes()
    {
        var min = new JsonFileGenerator().MinSizeBytes;
        yield return [min]; // scaffold + one empty-value record
        yield return [min + 1];
        yield return [1024L];
        yield return [65536L]; // exactly one internal chunk boundary
        yield return [65537L];
        yield return [200_000L]; // enough records that count is several digits wide
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
    public async Task GenerateAsync_OutputParsesAsJsonWithMatchingCount(long targetSizeBytes)
    {
        using var stream = new MemoryStream();
        await _generator.GenerateAsync(stream, targetSizeBytes, seed: 7);
        stream.Position = 0;

        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;

        var records = root.GetProperty("records");
        Assert.Equal(JsonValueKind.Array, records.ValueKind);
        Assert.Equal(records.GetArrayLength(), root.GetProperty("meta").GetProperty("count").GetInt32());

        foreach (var record in records.EnumerateArray())
        {
            Assert.Equal(JsonValueKind.Number, record.GetProperty("id").ValueKind);
            Assert.Equal(JsonValueKind.String, record.GetProperty("value").ValueKind);
        }
    }

    [Fact]
    public async Task GenerateAsync_AtMinSize_HasOneEmptyValueRecord()
    {
        using var stream = new MemoryStream();
        await _generator.GenerateAsync(stream, _generator.MinSizeBytes, seed: null);
        stream.Position = 0;

        using var doc = JsonDocument.Parse(stream);
        var records = doc.RootElement.GetProperty("records");

        Assert.Equal(1, records.GetArrayLength());
        Assert.Equal(1, records[0].GetProperty("id").GetInt64());
        Assert.Equal(string.Empty, records[0].GetProperty("value").GetString());
    }

    [Fact]
    public async Task GenerateAsync_RecordIdsCountUpContiguouslyFromOne()
    {
        using var stream = new MemoryStream();
        await _generator.GenerateAsync(stream, targetSizeBytes: 20_000, seed: null);
        stream.Position = 0;

        using var doc = JsonDocument.Parse(stream);
        var ids = doc.RootElement.GetProperty("records").EnumerateArray()
            .Select(r => r.GetProperty("id").GetInt64())
            .ToList();

        Assert.True(ids.Count >= 2);
        Assert.Equal(Enumerable.Range(1, ids.Count).Select(i => (long)i), ids);
    }

    [Fact]
    public async Task GenerateAsync_SeededStartingId_CountsUpFromThatValue()
    {
        using var stream = new MemoryStream();
        await _generator.GenerateAsync(stream, targetSizeBytes: 20_000, seed: 500);
        stream.Position = 0;

        using var doc = JsonDocument.Parse(stream);
        var records = doc.RootElement.GetProperty("records");

        Assert.Equal(500, records[0].GetProperty("id").GetInt64());
        Assert.Equal(501, records[1].GetProperty("id").GetInt64());
    }

    [Fact]
    public async Task GenerateAsync_FinalRecordValueAbsorbsTheRemainder()
    {
        // A size a little above the minimum: one padded record whose value is
        // longer than "item-N" and drawn from the filler pattern.
        var target = _generator.MinSizeBytes + 40;
        using var stream = new MemoryStream();
        await _generator.GenerateAsync(stream, target, seed: null);

        Assert.Equal(target, stream.Length);

        stream.Position = 0;
        using var doc = JsonDocument.Parse(stream);
        var records = doc.RootElement.GetProperty("records");
        var lastValue = records[records.GetArrayLength() - 1].GetProperty("value").GetString();

        Assert.NotNull(lastValue);
        Assert.StartsWith("the-quick-bro", lastValue);
        Assert.DoesNotContain("\"", lastValue);
    }

    [Fact]
    public async Task GenerateAsync_SeedChangesBytesButNotSize()
    {
        using var first = new MemoryStream();
        using var second = new MemoryStream();

        await _generator.GenerateAsync(first, targetSizeBytes: 8192, seed: null);
        await _generator.GenerateAsync(second, targetSizeBytes: 8192, seed: 42);

        Assert.Equal(first.Length, second.Length);
        Assert.NotEqual(first.ToArray(), second.ToArray());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(int.MinValue)]
    public async Task GenerateAsync_NonPositiveSeed_StartsAtId1(int seed)
    {
        using var stream = new MemoryStream();

        await _generator.GenerateAsync(stream, targetSizeBytes: 8192, seed: seed);

        Assert.Equal(8192, stream.Length);
        stream.Position = 0;
        using var doc = JsonDocument.Parse(stream);
        Assert.Equal(1, doc.RootElement.GetProperty("records")[0].GetProperty("id").GetInt64());
    }

    [Fact]
    public async Task GenerateAsync_LargeSeedNearMinSize_FallsBackToId1AndStaysExact()
    {
        var target = _generator.MinSizeBytes + 4;
        using var stream = new MemoryStream();

        await _generator.GenerateAsync(stream, target, seed: 999_999_999);

        Assert.Equal(target, stream.Length);
        stream.Position = 0;
        using var doc = JsonDocument.Parse(stream);
        var records = doc.RootElement.GetProperty("records");
        Assert.Equal(1, records[records.GetArrayLength() - 1].GetProperty("id").GetInt64());
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
            () => _generator.GenerateAsync(stream, _generator.MinSizeBytes - 1, seed: null));
    }

    [Fact]
    public void MinSizeBytes_IsScaffoldPlusOneEmptyRecord()
    {
        // {"meta":{"count":1},"records":[{"id":1,"value":""}]}
        Assert.Equal(52, _generator.MinSizeBytes);
    }

    [Fact]
    public async Task GenerateAsync_MinSizeOutput_IsTheExpectedLiteral()
    {
        using var stream = new MemoryStream();
        await _generator.GenerateAsync(stream, _generator.MinSizeBytes, seed: null);

        Assert.Equal(
            "{\"meta\":{\"count\":1},\"records\":[{\"id\":1,\"value\":\"\"}]}",
            Encoding.ASCII.GetString(stream.ToArray()));
    }
}
