using System.Globalization;
using System.Text;

namespace DummyFileApi.Generators;

public sealed class PdfFileGenerator : IFileGenerator
{
    private const int MaxChunkSize = 64 * 1024;

    private const int PageWidth = 612;
    private const int PageHeight = 792;
    private const int Margin = 72;
    private const int TitleY = 740;
    private const int CaptionY = 716;
    private const int GridTopY = 690;
    private const int OtherPageGridTop = PageHeight - Margin;
    private const int GridOriginX = Margin;
    private const int SquareSize = 48;
    private const int GridTextGap = 20;
    private const int FillerFontSize = 9;
    private const int FillerLeading = 14;

    // Every page carries the checkerboard, so bigger requests grow the image
    // per page instead of just adding more plain-text pages — keeps the page
    // count reasonable for a test document.
    private const int MaxTotalPages = 40;

    // (0, 0) drops the checkerboard for requests under 1 KB, where even the
    // smallest image would inflate the minimum achievable size.
    //
    // Rows are fixed at GridRows for every non-empty tier, keeping the image
    // at roughly half the page's available height; bigger requests grow the
    // image by adding columns instead of shrinking the text area.
    private const int GridRows = 6;

    private static readonly (int Cols, int Rows)[] GridTiers =
        [(0, 0), (3, GridRows), (5, GridRows), (7, GridRows), (9, GridRows)];

    // Resets fill color to black before text: the checkerboard leaves its
    // last square's color active, and text drawn afterward would otherwise
    // silently inherit it — including invisible white-on-white.
    private const string BlackFill = "0.000 0.000 0.000 rg\n";

    // One repeated filler line of fixed byte length.
    private const string FullLineText =
        "(The quick brown fox jumps over the lazy dog while dummy-file-api streams this PDF.) Tj\nT*\n";

    private static readonly byte[] Header = Encoding.ASCII.GetBytes("%PDF-1.4\n");

    private static readonly byte[] CatalogObject = Encoding.ASCII.GetBytes(
        "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

    // The standard Helvetica font, one of the 14 base fonts every PDF viewer
    // must support without embedding — keeps the generator free of font-file
    // handling.
    private static readonly byte[] FontObject = Encoding.ASCII.GetBytes(
        "3 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

    // Same palette as png/jpeg (seed picks the checkerboard color) for a
    // consistent look across generated formats. Each channel always renders
    // as "0.000"-"1.000" (5 chars), so a page's content length never depends
    // on which color is picked.
    private static readonly byte[][] Palette =
    [
        [0x64, 0x95, 0xED],
        [0xE9, 0x4F, 0x37],
        [0x2E, 0xCC, 0x71],
        [0xF3, 0x9C, 0x12],
        [0x9B, 0x59, 0xB6],
        [0x1A, 0xBC, 0x9C],
        [0xE7, 0x4C, 0x8C],
        [0x34, 0x49, 0x5E],
    ];

    private static readonly byte[] White = [0xFF, 0xFF, 0xFF];

    // Object 6..(2p+4) alternate Page/Contents per page; the padding stream
    // (2p+4) is placed last so growing/shrinking it never shifts any other
    // object's offset — only the xref/trailer that follow. It's a legal,
    // well-formed indirect object that nothing else references.
    private const string PaddingObjectMid = " >>\nstream\n";
    private static readonly byte[] PaddingObjectTail = Encoding.ASCII.GetBytes("\nendstream\nendobj\n");
    private const string FreeEntry = "0000000000 65535 f \n";
    private const string TrailerTail = "\n%%EOF\n";

    private const string PaddingText = "the-quick-brown-fox-jumps-over-the-lazy-dog-";

    private static readonly long MinSize = ComputeMinSize();

    public string TypeKey => "pdf";
    public string MimeType => "application/pdf";
    public string FileExtension => "pdf";

    // Smallest template: the smallest grid tier (no image), a single page
    // with no filler lines, and a zero-length padding stream.
    public long MinSizeBytes => MinSize;

    public async Task GenerateAsync(Stream output, long targetSizeBytes, int? seed, CancellationToken cancellationToken = default)
    {
        if (targetSizeBytes < MinSizeBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(targetSizeBytes));
        }

        var color = Palette[SeedIndex.Wrap(seed, Palette.Length)];
        var tier = SelectGridTier(targetSizeBytes);
        var (pageCount, linesPerPage) = PlanPages(targetSizeBytes, tier);

        // First pass: measure each object's length to fix its offset and the
        // padding needed, keeping only the offsets — the second pass below
        // rebuilds each object's text rather than caching it, so the file
        // streams out without holding its full contents in memory.
        var objectOffsets = new Dictionary<int, long>();
        var cursor = (long)Header.Length;

        objectOffsets[1] = cursor;
        cursor += CatalogObject.Length;
        objectOffsets[2] = cursor;
        cursor += BuildPagesObjectText(pageCount).Length;
        objectOffsets[3] = cursor;
        cursor += FontObject.Length;

        for (var i = 1; i <= pageCount; i++)
        {
            objectOffsets[PageObjNum(i)] = cursor;
            cursor += BuildPageObjectText(i).Length;
            objectOffsets[ContentsObjNum(i)] = cursor;
            cursor += BuildContentsObjectText(i, BuildPageBody(i, tier, color, linesPerPage[i - 1])).Length;
        }

        var totalObjects = TotalObjectCount(pageCount);
        var paddingObjNum = PaddingObjNum(pageCount);
        // The planner only accepts layouts this solve succeeds for, so failing
        // here is a bug — and it fails before a single byte is written.
        if (!TrySolvePadding(targetSizeBytes, cursor, paddingObjNum, totalObjects, out var solution))
        {
            throw new InvalidOperationException("PDF layout does not fit the requested size.");
        }

        var (paddingLength, lengthWidth, offsetWidth) = solution;

        await output.WriteAsync(Header, cancellationToken);
        await output.WriteAsync(CatalogObject, cancellationToken);
        await WriteAsciiAsync(output, BuildPagesObjectText(pageCount), cancellationToken);
        await output.WriteAsync(FontObject, cancellationToken);

        for (var i = 1; i <= pageCount; i++)
        {
            await WriteAsciiAsync(output, BuildPageObjectText(i), cancellationToken);
            var body = BuildPageBody(i, tier, color, linesPerPage[i - 1]);
            await WriteAsciiAsync(output, BuildContentsObjectText(i, body), cancellationToken);
        }

        objectOffsets[paddingObjNum] = cursor;
        var paddedLength = paddingLength.ToString(CultureInfo.InvariantCulture).PadLeft(lengthWidth, '0');
        var paddingHead = Encoding.ASCII.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"{paddingObjNum} 0 obj\n<< /Length {paddedLength} >>\nstream\n"));
        await output.WriteAsync(paddingHead, cancellationToken);
        await WritePaddingAsync(output, paddingLength, cancellationToken);
        await output.WriteAsync(PaddingObjectTail, cancellationToken);

        var xrefOffset = cursor + paddingHead.Length + paddingLength + PaddingObjectTail.Length;

        await WriteAsciiAsync(output, string.Create(CultureInfo.InvariantCulture, $"xref\n0 {totalObjects + 1}\n"), cancellationToken);
        await WriteAsciiAsync(output, FreeEntry, cancellationToken);
        for (var num = 1; num <= totalObjects; num++)
        {
            await WriteAsciiAsync(output, XrefEntry(objectOffsets[num]), cancellationToken);
        }

        var paddedOffset = xrefOffset.ToString(CultureInfo.InvariantCulture).PadLeft(offsetWidth, '0');
        await WriteAsciiAsync(
            output,
            string.Create(CultureInfo.InvariantCulture, $"trailer\n<< /Size {totalObjects + 1} /Root 1 0 R >>\nstartxref\n{paddedOffset}\n%%EOF\n"),
            cancellationToken);
    }

    // A size threshold alone can pick a tier whose own minimum overhead
    // exceeds the request, since row/column counts shift that minimum
    // independently of the thresholds — so the chosen tier is verified
    // against its real minimum, stepping down until one actually fits.
    private static (int Cols, int Rows) SelectGridTier(long targetSizeBytes)
    {
        if (targetSizeBytes < 1024)
        {
            return GridTiers[0];
        }

        var desired = targetSizeBytes switch
        {
            < 20 * 1024 => 1,
            < 100 * 1024 => 2,
            < 400 * 1024 => 3,
            _ => 4,
        };

        for (var i = Math.Min(desired, GridTiers.Length - 1); i > 0; i--)
        {
            if (TierFits(i, targetSizeBytes))
            {
                return GridTiers[i];
            }
        }

        return GridTiers[0];
    }

    // Whether the tier's smallest layout (one page, no filler lines) fits.
    private static bool TierFits(int tierIndex, long targetSizeBytes)
    {
        var contentEnd = ComputeContentSectionLength(1, GridTiers[tierIndex], [0]);
        return TrySolvePadding(targetSizeBytes, contentEnd, PaddingObjNum(1), TotalObjectCount(1), out _);
    }

    // Grows the page count using each page's own precomputed cost, then
    // binary searches just the one page left partially filled — avoiding a
    // full document rebuild per candidate page count.
    private static (int PageCount, int[] LinesPerPage) PlanPages(long targetSizeBytes, (int Cols, int Rows) tier)
    {
        var color = Palette[0]; // content length is invariant across colors
        var capacities = new int[MaxTotalPages + 1];
        var sumFullPageCost = new long[MaxTotalPages + 1];
        var kidsJoinedLength = new long[MaxTotalPages + 1];

        for (var i = 1; i <= MaxTotalPages; i++)
        {
            capacities[i] = Capacity(FillerTopY(tier, includeTitleCaption: i == 1));
            sumFullPageCost[i] = sumFullPageCost[i - 1] + PartialPageCost(i, tier, color, capacities[i]);
            kidsJoinedLength[i] = kidsJoinedLength[i - 1] + (i > 1 ? 1 : 0) + KidsEntryText(i).Length;
        }

        long ContentEnd(int finalPageCount, long lastPageCost) =>
            FixedPreambleLength + PagesObjectFixedLength(finalPageCount, kidsJoinedLength[finalPageCount]) + lastPageCost;

        bool Fits(long contentEnd, int pageCount) =>
            TrySolvePadding(targetSizeBytes, contentEnd, PaddingObjNum(pageCount), TotalObjectCount(pageCount), out _);

        var pFull = 0;
        for (var p = 1; p <= MaxTotalPages; p++)
        {
            if (Fits(ContentEnd(p, sumFullPageCost[p]), p))
            {
                pFull = p;
            }
            else
            {
                break;
            }
        }

        if (pFull == MaxTotalPages)
        {
            var lines = new int[MaxTotalPages];
            for (var i = 1; i <= MaxTotalPages; i++)
            {
                lines[i - 1] = capacities[i];
            }

            return (MaxTotalPages, lines);
        }

        var candidatePage = pFull + 1;
        var prefixCost = sumFullPageCost[pFull];
        var capacity = capacities[candidatePage];

        bool PartialFits(int lines) =>
            Fits(ContentEnd(candidatePage, prefixCost + PartialPageCost(candidatePage, tier, color, lines)), candidatePage);

        var partialLines = -1;
        if (PartialFits(0))
        {
            var lo = 0;
            var hi = capacity;
            while (lo < hi)
            {
                var mid = (lo + hi + 1) / 2;
                if (PartialFits(mid))
                {
                    lo = mid;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            partialLines = lo;
        }

        if (partialLines > 0 || pFull == 0)
        {
            var lines = new int[candidatePage];
            for (var i = 1; i < candidatePage; i++)
            {
                lines[i - 1] = capacities[i];
            }

            lines[candidatePage - 1] = Math.Max(partialLines, 0);
            return (candidatePage, lines);
        }

        var fullLines = new int[pFull];
        for (var i = 1; i <= pFull; i++)
        {
            fullLines[i - 1] = capacities[i];
        }

        return (pFull, fullLines);
    }

    // A page's own Page + Contents object bytes — independent of how many
    // other pages exist, since object numbers are a function of the page
    // index alone (PageObjNum/ContentsObjNum), not the total page count.
    private static long PartialPageCost(int i, (int Cols, int Rows) tier, byte[] color, int lines) =>
        BuildPageObjectText(i).Length + BuildContentsObjectText(i, BuildPageBody(i, tier, color, lines)).Length;

    private static string BuildPageBody(int i, (int Cols, int Rows) tier, byte[] color, int lines)
    {
        var includeTitleCaption = i == 1;
        return BuildPageGraphicText(tier, color, includeTitleCaption)
            + BuildFillerBlock(FillerTopY(tier, includeTitleCaption), lines);
    }

    // Total bytes before the padding object, for a given page layout. Only
    // called with pageCount 1 (MinSizeBytes, tier-affordability checks), so
    // rebuilding full object text here is fine — PlanPages uses the cheaper
    // per-page cost model below since it checks many candidate page counts.
    private static long ComputeContentSectionLength(int pageCount, (int Cols, int Rows) tier, IReadOnlyList<int> linesPerPage)
    {
        long total = CatalogObject.Length + BuildPagesObjectText(pageCount).Length + FontObject.Length;
        for (var i = 1; i <= pageCount; i++)
        {
            total += BuildPageObjectText(i).Length;
            total += BuildContentsObjectText(i, BuildPageBody(i, tier, Palette[0], linesPerPage[i - 1])).Length;
        }

        return Header.Length + total;
    }

    private static int GridTop(bool includeTitleCaption) => includeTitleCaption ? GridTopY : OtherPageGridTop;

    private static int FillerTopY((int Cols, int Rows) tier, bool includeTitleCaption)
    {
        var gridTop = GridTop(includeTitleCaption);
        return tier.Rows == 0 ? gridTop - GridTextGap : gridTop - tier.Rows * SquareSize - GridTextGap;
    }

    private static int Capacity(int topY) => Math.Max(0, (topY - Margin) / FillerLeading + 1);

    private static int PageObjNum(int i) => 4 + 2 * (i - 1);

    private static int ContentsObjNum(int i) => 5 + 2 * (i - 1);

    private static int PaddingObjNum(int pageCount) => 2 * pageCount + 4;

    private static int TotalObjectCount(int pageCount) => 2 * pageCount + 4;

    private const string PagesObjectPrefix = "2 0 obj\n<< /Type /Pages /Kids [";
    private const string PagesObjectMid = "] /Count ";
    private const string PagesObjectSuffix = " >>\nendobj\n";

    private static long FixedPreambleLength => Header.Length + CatalogObject.Length + FontObject.Length;

    // The Pages object's length given the already-accumulated Kids array
    // text length — used during planning so the Kids array (and each page
    // reference within it) is never rebuilt from scratch per candidate. Must
    // stay shaped like BuildPagesObjectText below.
    private static long PagesObjectFixedLength(int pageCount, long kidsJoinedLength) =>
        PagesObjectPrefix.Length + kidsJoinedLength + PagesObjectMid.Length + DigitCount(pageCount) + PagesObjectSuffix.Length;

    private static string KidsEntryText(int i) => string.Create(CultureInfo.InvariantCulture, $"{PageObjNum(i)} 0 R");

    private static string BuildPagesObjectText(int pageCount)
    {
        var kids = string.Join(' ', Enumerable.Range(1, pageCount).Select(KidsEntryText));
        return string.Create(CultureInfo.InvariantCulture, $"{PagesObjectPrefix}{kids}{PagesObjectMid}{pageCount}{PagesObjectSuffix}");
    }

    private static string BuildPageObjectText(int i) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{PageObjNum(i)} 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {PageWidth} {PageHeight}] "
            + $"/Resources << /Font << /F1 3 0 R >> >> /Contents {ContentsObjNum(i)} 0 R >>\nendobj\n");

    private static string BuildContentsObjectText(int i, string body) =>
        string.Create(CultureInfo.InvariantCulture, $"{ContentsObjNum(i)} 0 obj\n<< /Length {body.Length} >>\nstream\n{body}\nendstream\nendobj\n");

    // Title/caption (page 1 only) and a checkerboard grid of filled
    // rectangles drawn with plain PDF graphics operators — no image XObject
    // or embedded font needed, so every color's content is exactly the same
    // byte length. A (0,0) tier omits the grid entirely.
    private static string BuildPageGraphicText((int Cols, int Rows) tier, byte[] color, bool includeTitleCaption)
    {
        var sb = new StringBuilder();
        if (includeTitleCaption)
        {
            sb.Append(BlackFill);
            sb.Append("BT\n/F1 24 Tf\n").Append(AppendInt(Margin)).Append(' ').Append(AppendInt(TitleY)).Append(" Td\n(Dummy PDF File) Tj\nET\n");
            sb.Append("BT\n/F1 12 Tf\n").Append(AppendInt(Margin)).Append(' ').Append(AppendInt(CaptionY))
              .Append(" Td\n(Generated by dummy-file-api) Tj\nET\n");
        }

        if (tier.Cols > 0 && tier.Rows > 0)
        {
            var gridBottom = GridTop(includeTitleCaption) - tier.Rows * SquareSize;
            for (var row = 0; row < tier.Rows; row++)
            {
                for (var col = 0; col < tier.Cols; col++)
                {
                    var square = (row + col) % 2 == 0 ? color : White;
                    sb.Append(FormatComponent(square[0])).Append(' ')
                      .Append(FormatComponent(square[1])).Append(' ')
                      .Append(FormatComponent(square[2])).Append(" rg\n");

                    var x = GridOriginX + col * SquareSize;
                    var y = gridBottom + row * SquareSize;
                    sb.Append(AppendInt(x)).Append(' ').Append(AppendInt(y)).Append(' ')
                      .Append(AppendInt(SquareSize)).Append(' ').Append(AppendInt(SquareSize)).Append(" re f\n");
                }
            }
        }

        return sb.ToString();
    }

    private static string BuildFillerBlock(int topY, int lineCount)
    {
        if (lineCount == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.Append(BlackFill);
        sb.Append("BT\n/F1 ").Append(AppendInt(FillerFontSize)).Append(" Tf\n")
          .Append(AppendInt(Margin)).Append(' ').Append(AppendInt(topY)).Append(" Td\n")
          .Append(AppendInt(FillerLeading)).Append(" TL\n");

        for (var i = 0; i < lineCount; i++)
        {
            sb.Append(FullLineText);
        }

        sb.Append("ET\n");
        return sb.ToString();
    }

    private static string FormatComponent(byte value) =>
        (value / 255.0).ToString("0.000", CultureInfo.InvariantCulture);

    // Invariant formatting keeps content-stream numbers plain ASCII whatever
    // the server's locale.
    private static string AppendInt(int value) => value.ToString(CultureInfo.InvariantCulture);

    // Finds a (padding length, /Length digit width, startxref digit width)
    // combination that hits the target exactly. Both widths start minimal and
    // only grow when the padding length or xref offset needs more digits than
    // currently allotted — which happens right at a digit-count boundary
    // (e.g. padding 9999 -> 10000), where the extra leading zero absorbs the
    // byte a minimal-width representation would otherwise skip over. Widths
    // only grow, so the padding only shrinks: once it goes negative, the
    // content section is too long for the target.
    private static bool TrySolvePadding(
        long targetSizeBytes, long contentEnd, int paddingObjNum, int totalObjects,
        out (long PaddingLength, int LengthWidth, int OffsetWidth) solution)
    {
        solution = default;
        var lengthWidth = 1;
        var offsetWidth = 1;
        for (var attempt = 0; attempt < 64; attempt++)
        {
            var overhead = TotalLength(contentEnd, 0, lengthWidth, offsetWidth, paddingObjNum, totalObjects);
            var paddingLength = targetSizeBytes - overhead;
            if (paddingLength < 0)
            {
                return false;
            }

            var neededLengthWidth = DigitCount(paddingLength);
            if (neededLengthWidth > lengthWidth)
            {
                lengthWidth = neededLengthWidth;
                continue;
            }

            var xrefOffset = contentEnd + PaddingObjectLength(paddingLength, lengthWidth, paddingObjNum);
            var neededOffsetWidth = DigitCount(xrefOffset);
            if (neededOffsetWidth > offsetWidth)
            {
                offsetWidth = neededOffsetWidth;
                continue;
            }

            solution = (paddingLength, lengthWidth, offsetWidth);
            return true;
        }

        throw new InvalidOperationException("PDF padding calculation did not converge.");
    }

    private static long ComputeMinSize()
    {
        var tier = GridTiers[0];
        var linesPerPage = new[] { 0 };
        var contentEnd = ComputeContentSectionLength(1, tier, linesPerPage);
        const int lengthWidth = 1; // digit width of a 0-byte padding stream
        var paddingObjNum = PaddingObjNum(1);
        var offsetWidth = DigitCount(contentEnd + PaddingObjectLength(0, lengthWidth, paddingObjNum));
        return TotalLength(contentEnd, 0, lengthWidth, offsetWidth, paddingObjNum, TotalObjectCount(1));
    }

    private static long TotalLength(
        long contentEnd, long paddingLength, int lengthWidth, int offsetWidth, int paddingObjNum, int totalObjects)
    {
        var xrefSectionLength = $"xref\n0 {totalObjects + 1}\n".Length + FreeEntry.Length + totalObjects * 20;
        var trailerHeadLength = $"trailer\n<< /Size {totalObjects + 1} /Root 1 0 R >>\nstartxref\n".Length;
        return contentEnd + PaddingObjectLength(paddingLength, lengthWidth, paddingObjNum)
            + xrefSectionLength + trailerHeadLength + offsetWidth + TrailerTail.Length;
    }

    private static long PaddingObjectLength(long paddingLength, int lengthWidth, int paddingObjNum)
    {
        var head = $"{paddingObjNum} 0 obj\n<< /Length ";
        return head.Length + lengthWidth + PaddingObjectMid.Length + paddingLength + PaddingObjectTail.Length;
    }

    private static int DigitCount(long value) => value.ToString(CultureInfo.InvariantCulture).Length;

    private static string XrefEntry(long offset) =>
        string.Create(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");

    private static async Task WriteAsciiAsync(Stream output, string text, CancellationToken cancellationToken) =>
        await output.WriteAsync(Encoding.ASCII.GetBytes(text), cancellationToken);

    private static async Task WritePaddingAsync(Stream output, long length, CancellationToken cancellationToken)
    {
        if (length == 0)
        {
            return;
        }

        var chunk = new byte[(int)Math.Min(MaxChunkSize, length)];
        for (var i = 0; i < chunk.Length; i++)
        {
            chunk[i] = (byte)PaddingText[i % PaddingText.Length];
        }

        var remaining = length;
        while (remaining > 0)
        {
            var count = (int)Math.Min(chunk.Length, remaining);
            await output.WriteAsync(chunk.AsMemory(0, count), cancellationToken);
            remaining -= count;
        }
    }
}
