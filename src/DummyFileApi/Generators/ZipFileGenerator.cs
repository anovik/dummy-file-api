namespace DummyFileApi.Generators;

public sealed class ZipFileGenerator : IFileGenerator
{
    private const string EntryName = "readme.txt";

    // Fixed-width binary headers mean the archive size is a closed-form
    // function of the single stored entry's content length — no padding
    // container and no digit-width iteration, unlike pdf/jpeg/png.
    private static readonly long Overhead = ZipWriter.OverheadFor(EntryName);

    // seed picks the filler phrase. Like png's palette, it changes the bytes
    // but never the size — content length is fixed by the target regardless.
    private static readonly byte[][] Phrases =
    [
        "The quick brown fox jumps over the lazy dog. "u8.ToArray(),
        "Pack my box with five dozen liquor jugs. "u8.ToArray(),
        "How vexingly quick daft zebras jump. "u8.ToArray(),
        "Sphinx of black quartz, judge my vow. "u8.ToArray(),
        "The five boxing wizards jump in quickly. "u8.ToArray(),
        "Jackdaws love my big sphinx of quartz. "u8.ToArray(),
        "Bright vixens jump; dozy fowl quack. "u8.ToArray(),
        "Quick zephyrs blow, vexing daft Jim. "u8.ToArray(),
    ];

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
        var phrase = Phrases[SeedIndex.Wrap(seed, Phrases.Length)];
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
