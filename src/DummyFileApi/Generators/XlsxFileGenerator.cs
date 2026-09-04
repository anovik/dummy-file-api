using System.IO.Hashing;
using System.Text;

namespace DummyFileApi.Generators;

/// <summary>
/// An .xlsx is an OOXML package — a ZIP of XML parts. Every part is fixed except
/// the rows in <c>xl/worksheets/sheet1.xml</c>: numeric rows counting up from a
/// seed value, then a final row whose inline-string cell is sized to land on the
/// exact target, the way <see cref="CsvFileGenerator"/> pads its last field.
/// </summary>
public sealed class XlsxFileGenerator : IFileGenerator
{
    private const string Prolog = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n";

    // Last row's padding cell: plain ASCII with no XML-significant characters, so
    // byte length equals character length and the text needs no escaping.
    private const string Padding = "the-quick-brown-fox-jumps-over-the-lazy-dog-";

    private const string ContentTypesXml = Prolog +
        "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
        "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
        "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
        "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
        "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
        "<Override PartName=\"/docProps/core.xml\" ContentType=\"application/vnd.openxmlformats-package.core-properties+xml\"/>" +
        "<Override PartName=\"/docProps/app.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.extended-properties+xml\"/>" +
        "</Types>";

    private const string PackageRelsXml = Prolog +
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
        "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties\" Target=\"docProps/core.xml\"/>" +
        "<Relationship Id=\"rId3\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/extended-properties\" Target=\"docProps/app.xml\"/>" +
        "</Relationships>";

    private const string WorkbookXml = Prolog +
        "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
        "<sheets><sheet name=\"Sheet1\" sheetId=\"1\" r:id=\"rId1\"/></sheets>" +
        "</workbook>";

    private const string WorkbookRelsXml = Prolog +
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
        "</Relationships>";

    private const string CorePropsXml = Prolog +
        "<cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\">" +
        "<dc:title>Dummy Spreadsheet</dc:title><dc:creator>dummy-file-api</dc:creator>" +
        "</cp:coreProperties>";

    private const string AppPropsXml = Prolog +
        "<Properties xmlns=\"http://schemas.openxmlformats.org/officeDocument/2006/extended-properties\">" +
        "<Application>dummy-file-api</Application>" +
        "</Properties>";

    private const string SheetPrefix = Prolog +
        "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>";

    private const string SheetSuffix = "</sheetData></worksheet>";

    private const string HeaderRow =
        "<row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>Id</t></is></c>" +
        "<c r=\"B1\" t=\"inlineStr\"><is><t>Value</t></is></c></row>";

    private const string SheetPartName = "xl/worksheets/sheet1.xml";

    private static readonly (string Name, byte[] Content)[] FixedParts =
    [
        ("[Content_Types].xml", Encoding.ASCII.GetBytes(ContentTypesXml)),
        ("_rels/.rels", Encoding.ASCII.GetBytes(PackageRelsXml)),
        ("xl/_rels/workbook.xml.rels", Encoding.ASCII.GetBytes(WorkbookRelsXml)),
        ("xl/workbook.xml", Encoding.ASCII.GetBytes(WorkbookXml)),
        ("docProps/core.xml", Encoding.ASCII.GetBytes(CorePropsXml)),
        ("docProps/app.xml", Encoding.ASCII.GetBytes(AppPropsXml)),
    ];

    private static readonly string[] PartNames =
        [.. FixedParts.Select(p => p.Name), SheetPartName];

    // The whole archive minus the sheet's row region (numeric rows + final row):
    // ZIP framing, the fixed parts, and the sheet's own prefix/header/suffix.
    private static readonly long FixedOverhead =
        ZipWriter.OverheadFor(PartNames)
        + FixedParts.Sum(p => (long)p.Content.Length)
        + SheetPrefix.Length + HeaderRow.Length + SheetSuffix.Length;

    public string TypeKey => "xlsx";
    public string MimeType => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    public string FileExtension => "xlsx";

    // Fixed overhead plus a single final row (Id 1, empty padding cell): header
    // row + one minimal data row, the smallest sheet that still reads as a table.
    public long MinSizeBytes => FixedOverhead + FinalRow(2, 1, string.Empty).Length;

    public async Task GenerateAsync(Stream output, long targetSizeBytes, int? seed, CancellationToken cancellationToken = default)
    {
        if (targetSizeBytes < MinSizeBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(targetSizeBytes));
        }

        // seed picks the starting Id, rows counting up from there (like csv).
        var start = seed is int s && s >= 1 ? (long)s : 1L;

        // ZipWriter needs the entry's CRC in the local header ahead of the data,
        // so the sheet is generated once to hash it, then again to stream it.
        var crc = new Crc32();
        long sheetLength = 0;
        foreach (var chunk in SheetChunks(targetSizeBytes, start))
        {
            var bytes = Encoding.ASCII.GetBytes(chunk);
            crc.Append(bytes);
            sheetLength += bytes.Length;
        }

        var writer = new ZipWriter(output);
        foreach (var (name, content) in FixedParts)
        {
            await writer.AddEntryAsync(name, content, cancellationToken);
        }

        await writer.AddEntryAsync(
            SheetPartName,
            sheetLength,
            crc.GetCurrentHashAsUInt32(),
            async (stream, ct) =>
            {
                foreach (var chunk in SheetChunks(targetSizeBytes, start))
                {
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(chunk), ct);
                }
            },
            cancellationToken);

        await writer.FinishAsync(cancellationToken);
    }

    // Deterministic in (targetSizeBytes, start): full numeric rows while one more
    // still leaves room for a minimal final row, then the final row's inline
    // string is sized to consume the exact remainder.
    private static IEnumerable<string> SheetChunks(long targetSizeBytes, long start)
    {
        yield return SheetPrefix;
        yield return HeaderRow;

        var remaining = targetSizeBytes - FixedOverhead;
        long row = 2;
        var value = start;

        // A large seed's digit width may not fit a near-minimum size; fall back
        // to Id 1 so any size at or above the minimum still works.
        if (remaining < FinalRow(row, value, string.Empty).Length)
        {
            value = 1;
        }

        while (true)
        {
            var numericRow = NumericRow(row, value);
            var nextFinalMin = FinalRow(row + 1, value + 1, string.Empty).Length;

            if (remaining >= numericRow.Length + nextFinalMin)
            {
                yield return numericRow;
                remaining -= numericRow.Length;
                row++;
                value++;
                continue;
            }

            var padLength = (int)(remaining - FinalRow(row, value, string.Empty).Length);
            yield return FinalRow(row, value, MakePadding(padLength));
            break;
        }

        yield return SheetSuffix;
    }

    private static string NumericRow(long row, long value) =>
        $"<row r=\"{row}\"><c r=\"A{row}\"><v>{value}</v></c></row>";

    private static string FinalRow(long row, long value, string padding) =>
        $"<row r=\"{row}\"><c r=\"A{row}\"><v>{value}</v></c>" +
        $"<c r=\"B{row}\" t=\"inlineStr\"><is><t>{padding}</t></is></c></row>";

    private static string MakePadding(int length)
    {
        if (length == 0)
        {
            return string.Empty;
        }

        return string.Create(length, 0, static (span, _) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = Padding[i % Padding.Length];
            }
        });
    }
}
