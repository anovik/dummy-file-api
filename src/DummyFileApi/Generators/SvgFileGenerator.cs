using System.Text;

namespace DummyFileApi.Generators;

/// <summary>
/// A minimal SVG document: a scaled checkerboard of <c>&lt;rect&gt;</c>s (seed
/// picks the fill color) followed by a <c>&lt;desc&gt;</c> whose safe-ASCII text
/// is sized to land on the exact byte count — the last-field trick
/// <see cref="CsvFileGenerator"/> uses, with the fixed SVG scaffold as the
/// constant. No padding container and no digit-width iteration.
/// </summary>
public sealed class SvgFileGenerator : IFileGenerator
{
    private const int MaxChunkSize = 64 * 1024;

    private const int CellPx = 16;
    private const int MinSide = 2;

    // The checkerboard is capped so its markup stays small; the image formats
    // (png/jpeg/pdf) already cover "an image that scales to a real size".
    private const int MaxSide = 32;

    private const string XmlDecl = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n";
    private const string DescOpen = "<desc>";
    private const string Suffix = "</desc></svg>";

    // Filler for <desc>: letters and hyphens only, so byte length equals char
    // length and nothing needs XML-escaping.
    private const string Padding = "the-quick-brown-fox-jumps-over-the-lazy-dog-";

    // Seed picks the checkerboard fill; the alternate squares are left blank.
    // The same eight colors png/pdf use, written as CSS hex.
    private static readonly string[] Palette =
    [
        "#6495ED", "#E94F37", "#2ECC71", "#F39C12",
        "#9B59B6", "#1ABC9C", "#E74C8C", "#34495E",
    ];

    // Smallest canvas with an empty <desc>. The desc text grows one byte at a
    // time, so every size at or above this is reachable.
    private static readonly long MinSize =
        BuildScaffold(MinSide, Palette[0]).Length + DescOpen.Length + Suffix.Length;

    public string TypeKey => "svg";
    public string MimeType => "image/svg+xml";
    public string FileExtension => "svg";

    public long MinSizeBytes => MinSize;

    public async Task GenerateAsync(Stream output, long targetSizeBytes, int? seed, CancellationToken cancellationToken = default)
    {
        if (targetSizeBytes < MinSizeBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(targetSizeBytes));
        }

        var color = Palette[SeedIndex.Wrap(seed, Palette.Length)];

        // Largest checkerboard whose scaffold still fits, so a bigger request
        // maps to a visibly bigger image; <desc> absorbs the remainder. Total
        // size is monotonic in the side length, so a linear widen suffices.
        var scaffold = BuildScaffold(MinSide, color);
        for (var side = MinSide + 1; side <= MaxSide; side++)
        {
            var candidate = BuildScaffold(side, color);
            if (candidate.Length + DescOpen.Length + Suffix.Length > targetSizeBytes)
            {
                break;
            }

            scaffold = candidate;
        }

        var descLength = targetSizeBytes - scaffold.Length - DescOpen.Length - Suffix.Length;

        await output.WriteAsync(Encoding.ASCII.GetBytes(scaffold + DescOpen), cancellationToken);
        await WriteFillerAsync(output, descLength, cancellationToken);
        await output.WriteAsync(Encoding.ASCII.GetBytes(Suffix), cancellationToken);
    }

    private static async Task WriteFillerAsync(Stream output, long length, CancellationToken cancellationToken)
    {
        if (length <= 0)
        {
            return;
        }

        var buffer = new byte[(int)Math.Min(MaxChunkSize, length)];
        var patternOffset = 0;
        var remaining = length;
        while (remaining > 0)
        {
            var count = (int)Math.Min(buffer.Length, remaining);
            for (var i = 0; i < count; i++)
            {
                buffer[i] = (byte)Padding[(patternOffset + i) % Padding.Length];
            }

            patternOffset = (patternOffset + count) % Padding.Length;
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            remaining -= count;
        }
    }

    private static string BuildScaffold(int side, string color)
    {
        var px = side * CellPx;
        var sb = new StringBuilder(XmlDecl);
        sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"").Append(px)
          .Append("\" height=\"").Append(px)
          .Append("\" viewBox=\"0 0 ").Append(px).Append(' ').Append(px).Append("\">\n");

        for (var row = 0; row < side; row++)
        {
            for (var col = 0; col < side; col++)
            {
                if ((row + col) % 2 != 0)
                {
                    continue; // blank square
                }

                sb.Append("<rect x=\"").Append(col * CellPx)
                  .Append("\" y=\"").Append(row * CellPx)
                  .Append("\" width=\"").Append(CellPx)
                  .Append("\" height=\"").Append(CellPx)
                  .Append("\" fill=\"").Append(color).Append("\"/>");
            }
        }

        return sb.ToString();
    }
}
