namespace DummyFileApi.Generators;

public sealed class TarFileGenerator : IFileGenerator
{
    private const string EntryName = "readme.txt";

    // Header + the two end-of-archive zero blocks; content is the only variable part.
    private static readonly long FixedOverhead = TarWriter.OverheadFor(EntryName);

    public string TypeKey => "tar";
    public string MimeType => "application/x-tar";
    public string FileExtension => "tar";

    // Header + zero-length content (no data block needed) + the two end blocks.
    public long MinSizeBytes => FixedOverhead;

    public async Task GenerateAsync(Stream output, long targetSizeBytes, int? seed, CancellationToken cancellationToken = default)
    {
        if (targetSizeBytes < MinSizeBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(targetSizeBytes));
        }

        // A "clean" tar (header + block-padded content + two end blocks) is always a
        // multiple of 512 bytes. Content is sized so the clean portion lands exactly on the
        // largest such multiple at or below the target; any sub-512 remainder is appended
        // as trailing zero bytes, which readers treat as harmless padding past the
        // end-of-archive marker.
        var alignedTotal = targetSizeBytes / TarWriter.BlockSize * TarWriter.BlockSize;
        var contentLength = alignedTotal - FixedOverhead;
        var trailingPadding = targetSizeBytes - alignedTotal;

        // seed picks the filler phrase, like zip/docx — it changes the bytes, not the size.
        var phrase = FillerPhrases.AllBytes[SeedIndex.Wrap(seed, FillerPhrases.AllBytes.Length)];

        await TarWriter.WriteEntryAsync(
            output,
            EntryName,
            contentLength,
            (stream, ct) => RepeatingFiller.WriteAsync(stream, phrase, contentLength, ct),
            cancellationToken);
        await TarWriter.WriteEndOfArchiveAsync(output, cancellationToken);

        if (trailingPadding > 0)
        {
            await output.WriteAsync(new byte[trailingPadding], cancellationToken);
        }
    }
}
