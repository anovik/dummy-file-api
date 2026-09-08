using System.Buffers.Binary;

namespace DummyFileApi.Generators;

/// <summary>
/// A canonical RIFF/WAVE file: the 44-byte header (PCM <c>fmt </c> chunk, mono
/// 8 kHz 8-bit) followed by a <c>data</c> chunk of raw unsigned PCM samples.
/// Block align is 1, so the sample count — and with it the file size — can be any
/// byte value: <c>data</c> length is <c>target - 44</c> in one step, no padding
/// chunk. Seed picks the sample waveform (silence or a fixed low-amplitude
/// shape); it changes the audio, not the size.
/// </summary>
public sealed class WavFileGenerator : IFileGenerator
{
    private const int SampleRate = 8000;
    private const int BitsPerSample = 8;
    private const int Channels = 1;
    private const int BlockAlign = Channels * BitsPerSample / 8;
    private const int ByteRate = SampleRate * BlockAlign;

    // "RIFF" + size + "WAVE" (12) | "fmt " + size + 16-byte body (24) | "data" + size (8).
    private const int HeaderLength = 12 + 24 + 8;

    private static readonly byte[][] Waveforms = BuildWaveforms();

    public string TypeKey => "wav";
    public string MimeType => "audio/wav";
    public string FileExtension => "wav";

    // The header with a zero-length data chunk is a valid (silent) WAVE file.
    public long MinSizeBytes => HeaderLength;

    public async Task GenerateAsync(Stream output, long targetSizeBytes, int? seed, CancellationToken cancellationToken = default)
    {
        if (targetSizeBytes < MinSizeBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(targetSizeBytes));
        }

        var dataLength = targetSizeBytes - HeaderLength;

        // data is the final chunk, so an odd length needs no RIFF pad byte — readers
        // take the sample count from the chunk's own size field.
        await output.WriteAsync(BuildHeader(dataLength), cancellationToken);

        var waveform = Waveforms[SeedIndex.Wrap(seed, Waveforms.Length)];
        await RepeatingFiller.WriteAsync(output, waveform, dataLength, cancellationToken);
    }

    private static byte[] BuildHeader(long dataLength)
    {
        var header = new byte[HeaderLength];

        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), (uint)(HeaderLength - 8 + dataLength));
        "WAVE"u8.CopyTo(header.AsSpan(8));

        "fmt "u8.CopyTo(header.AsSpan(12));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), 16); // PCM fmt body size
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(20), 1);  // AudioFormat: PCM
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(22), Channels);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28), ByteRate);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(32), BlockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(34), BitsPerSample);

        "data"u8.CopyTo(header.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40), (uint)dataLength);

        return header;
    }

    // Fixed one-period sample tables, tiled across the data chunk. All stay within
    // ±40 of the 0x80 midpoint so the result is a quiet tone rather than a screech.
    private static byte[][] BuildWaveforms()
    {
        const int mid = 0x80;

        var silence = new byte[] { mid };

        var square = new byte[64];
        for (var i = 0; i < square.Length; i++)
        {
            square[i] = (byte)(i < 32 ? mid - 24 : mid + 24);
        }

        var sawtooth = new byte[64];
        for (var i = 0; i < sawtooth.Length; i++)
        {
            sawtooth[i] = (byte)(mid - 32 + i);
        }

        var triangle = new byte[64];
        for (var i = 0; i < triangle.Length; i++)
        {
            triangle[i] = (byte)(mid - 16 + (i < 32 ? i : 64 - i));
        }

        var staircase = new byte[64];
        for (var i = 0; i < staircase.Length; i++)
        {
            staircase[i] = (byte)(mid - 28 + i / 8 * 8);
        }

        var pulse = new byte[128];
        Array.Fill(pulse, (byte)mid);
        pulse[0] = mid + 40;
        pulse[1] = mid + 20;
        pulse[2] = mid - 20;
        pulse[3] = mid - 40;

        var tone = new byte[16];
        for (var i = 0; i < tone.Length; i++)
        {
            tone[i] = (byte)Math.Round(mid + 32 * Math.Sin(2 * Math.PI * i / tone.Length));
        }

        var stepped = new byte[37];
        for (var i = 0; i < stepped.Length; i++)
        {
            stepped[i] = (byte)(mid - 20 + (i * 37 + 11) % 41);
        }

        return [silence, square, sawtooth, triangle, staircase, pulse, tone, stepped];
    }
}
