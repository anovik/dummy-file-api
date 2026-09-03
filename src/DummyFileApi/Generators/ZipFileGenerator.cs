namespace DummyFileApi.Generators;

public sealed class ZipFileGenerator : IFileGenerator
{
    private const string EntryName = "readme.txt";

    // Fixed-width binary headers mean the archive size is a closed-form
    // function of the single stored entry's content length — no padding
    // container and no digit-width iteration, unlike pdf/jpeg/png.
    private static readonly long Overhead = ZipWriter.OverheadFor(EntryName);

    public string TypeKey => "zip";
    public string MimeType => "application/zip";
    public string FileExtension => "zip";

    // One stored entry with zero-length content: a valid, openable archive.
    public long MinSizeBytes => Overhead;

    public async Task GenerateAsync(Stream output, long targetSizeBytes, int? seed, CancellationToken cancellationToken = default)
    {
        if (targetSizeBytes < MinSizeBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(targetSizeBytes));
        }

        var contentLength = targetSizeBytes - Overhead;
        // seed picks the filler phrase. Like png's palette, it changes the bytes
        // but never the size — content length is fixed by the target regardless.
        var phrase = FillerPhrases.AllBytes[SeedIndex.Wrap(seed, FillerPhrases.AllBytes.Length)];
        var crc = ZipWriter.RepeatingCrc32(phrase, contentLength);

        var writer = new ZipWriter(output);
        await writer.AddEntryAsync(
            EntryName,
            contentLength,
            crc,
            (stream, ct) => ZipWriter.WriteRepeatingAsync(stream, phrase, contentLength, ct),
            cancellationToken);
        await writer.FinishAsync(cancellationToken);
    }
}
