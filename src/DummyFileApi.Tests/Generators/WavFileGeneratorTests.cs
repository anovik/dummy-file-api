using System.Buffers.Binary;
using System.Text;
using DummyFileApi.Generators;

namespace DummyFileApi.Tests.Generators;

public class WavFileGeneratorTests
{
    private readonly WavFileGenerator _generator = new();

    private const int HeaderLength = 44;

    [Theory]
    [InlineData(HeaderLength)]      // MinSizeBytes: header, zero-length data chunk
    [InlineData(HeaderLength + 1)]  // one sample — odd data length, no RIFF pad byte
    [InlineData(1024)]
    [InlineData(65536)]             // spans the internal 64 KB streaming buffer
    [InlineData(65537)]
    [InlineData(512 * 1024)]
    [InlineData(2 * 1024 * 1024 + 7)]
    public async Task GenerateAsync_ProducesExactRequestedByteCount(long targetSizeBytes)
    {
        using var stream = new MemoryStream();

        await _generator.GenerateAsync(stream, targetSizeBytes, seed: null);

        Assert.Equal(targetSizeBytes, stream.Length);
    }

    [Theory]
    [InlineData(HeaderLength)]
    [InlineData(HeaderLength + 1)]
    [InlineData(4096)]
    [InlineData(2 * 1024 * 1024 + 7)]
    public async Task GenerateAsync_IsCanonicalPcmWaveWithDataLengthMatchingTheSolve(long targetSizeBytes)
    {
        using var stream = new MemoryStream();
        await _generator.GenerateAsync(stream, targetSizeBytes, seed: 3);

        var wav = ReadWav(stream.ToArray());

        Assert.Equal(1, wav.AudioFormat);      // PCM
        Assert.Equal(1, wav.Channels);
        Assert.Equal(8000u, wav.SampleRate);
        Assert.Equal(8, wav.BitsPerSample);
        Assert.Equal(targetSizeBytes - HeaderLength, wav.Data.Length);
    }

    [Fact]
    public async Task GenerateAsync_DefaultSeed_IsSilence()
    {
        using var stream = new MemoryStream();
        await _generator.GenerateAsync(stream, targetSizeBytes: 4096, seed: null);

        Assert.All(ReadWav(stream.ToArray()).Data, b => Assert.Equal(0x80, b));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public async Task GenerateAsync_EverySeed_ProducesExactSizeAndLowAmplitudeSamples(int seed)
    {
        using var stream = new MemoryStream();
        await _generator.GenerateAsync(stream, targetSizeBytes: 8192, seed: seed);

        Assert.Equal(8192, stream.Length);
        Assert.All(ReadWav(stream.ToArray()).Data, b => Assert.True(Math.Abs(b - 0x80) <= 40));
    }

    [Fact]
    public async Task GenerateAsync_NonSilentSeed_ProducesAudibleSamples()
    {
        using var stream = new MemoryStream();
        await _generator.GenerateAsync(stream, targetSizeBytes: 4096, seed: 1);

        Assert.Contains(ReadWav(stream.ToArray()).Data, b => b != 0x80);
    }

    [Fact]
    public async Task GenerateAsync_SeedChangesBytesButNotSize()
    {
        using var first = new MemoryStream();
        using var second = new MemoryStream();

        await _generator.GenerateAsync(first, targetSizeBytes: 8192, seed: 1);
        await _generator.GenerateAsync(second, targetSizeBytes: 8192, seed: 2);

        Assert.Equal(first.Length, second.Length);
        Assert.NotEqual(first.ToArray(), second.ToArray());
    }

    [Fact]
    public async Task GenerateAsync_WaveformTilesContinuouslyFromTheStart()
    {
        using var small = new MemoryStream();
        using var large = new MemoryStream();

        await _generator.GenerateAsync(small, targetSizeBytes: 4096, seed: 4);
        await _generator.GenerateAsync(large, targetSizeBytes: 8192, seed: 4);

        var smallData = ReadWav(small.ToArray()).Data;
        var largeData = ReadWav(large.ToArray()).Data;

        Assert.Equal(smallData, largeData[..smallData.Length]);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public async Task GenerateAsync_NegativeSeed_StillHitsExactSizeAndValidStructure(int seed)
    {
        using var stream = new MemoryStream();

        await _generator.GenerateAsync(stream, targetSizeBytes: 8192, seed: seed);

        Assert.Equal(8192, stream.Length);
        Assert.Equal(8192 - HeaderLength, ReadWav(stream.ToArray()).Data.Length);
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
    public void MinSizeBytes_IsTheCanonicalWaveHeader()
    {
        Assert.Equal(44, _generator.MinSizeBytes);
    }

    private readonly record struct Wav(
        ushort AudioFormat, ushort Channels, uint SampleRate, ushort BitsPerSample, byte[] Data);

    private static Wav ReadWav(byte[] bytes)
    {
        Assert.Equal("RIFF", Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal((uint)(bytes.Length - 8), BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(bytes, 8, 4));

        ushort audioFormat = 0, channels = 0, bitsPerSample = 0;
        uint sampleRate = 0;
        byte[]? data = null;

        var pos = 12;
        while (pos + 8 <= bytes.Length)
        {
            var id = Encoding.ASCII.GetString(bytes, pos, 4);
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(pos + 4));
            pos += 8;

            if (id == "fmt ")
            {
                audioFormat = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(pos));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(pos + 2));
                sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(pos + 4));
                bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(pos + 14));
            }
            else if (id == "data")
            {
                data = bytes[pos..(pos + size)];
            }

            pos += size + (size & 1); // skip the RIFF pad byte after an odd-length chunk
        }

        Assert.NotNull(data);
        return new Wav(audioFormat, channels, sampleRate, bitsPerSample, data);
    }
}
