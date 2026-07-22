using System.Text;

namespace DummyFileApi.Generators;

public sealed class TxtFileGenerator : IFileGenerator
{
    private const int MaxChunkSize = 64 * 1024;
    private static readonly byte[] Filler = Encoding.ASCII.GetBytes(
        "The quick brown fox jumps over the lazy dog. ");

    public string TypeKey => "txt";
    public string MimeType => "text/plain";
    public string FileExtension => "txt";

    // An empty text file is valid; 1 is a basic sanity floor, not a format requirement.
    public long MinSizeBytes => 1;

    public async Task GenerateAsync(Stream output, long targetSizeBytes, int? seed, CancellationToken cancellationToken = default)
    {
        if (targetSizeBytes < MinSizeBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(targetSizeBytes));
        }

        var chunk = new byte[Math.Min(MaxChunkSize, targetSizeBytes)];
        for (var i = 0; i < chunk.Length; i++)
        {
            chunk[i] = Filler[i % Filler.Length];
        }

        var remaining = targetSizeBytes;
        while (remaining > 0)
        {
            var count = (int)Math.Min(chunk.Length, remaining);
            await output.WriteAsync(chunk.AsMemory(0, count), cancellationToken);
            remaining -= count;
        }
    }
}
