namespace DummyFileApi.Generators;

/// <summary>
/// Streams (or fills a buffer with) a byte pattern repeated indefinitely from its start.
/// Shared by every generator that pads an entry with deterministic filler content —
/// <see cref="ZipWriter"/> (zip/docx) and <see cref="TarWriter"/> (tar).
/// </summary>
internal static class RepeatingFiller
{
    private const int MaxChunkSize = 64 * 1024;

    /// <summary>Streams <paramref name="length"/> bytes of <paramref name="pattern"/> repeated from its start.</summary>
    public static async Task WriteAsync(Stream output, byte[] pattern, long length, CancellationToken cancellationToken)
    {
        var chunk = new byte[(int)Math.Min(MaxChunkSize, Math.Max(length, 1))];
        var patternIndex = 0;
        var remaining = length;
        while (remaining > 0)
        {
            var count = (int)Math.Min(chunk.Length, remaining);
            Fill(chunk.AsSpan(0, count), pattern, ref patternIndex);
            await output.WriteAsync(chunk.AsMemory(0, count), cancellationToken);
            remaining -= count;
        }
    }

    /// <summary>Fills <paramref name="destination"/> with <paramref name="pattern"/> repeated, continuing from <paramref name="patternIndex"/>.</summary>
    public static void Fill(Span<byte> destination, ReadOnlySpan<byte> pattern, ref int patternIndex)
    {
        for (var i = 0; i < destination.Length; i++)
        {
            destination[i] = pattern[patternIndex];
            if (++patternIndex == pattern.Length)
            {
                patternIndex = 0;
            }
        }
    }
}
