using System.Globalization;
using System.IO.Hashing;
using System.Text;

namespace DummyFileApi.Generators;

/// <summary>
/// An .xlsx is an OOXML package — a ZIP of XML parts. Every part is fixed except
/// the rows in <c>xl/worksheets/sheet1.xml</c>: numeric rows counting up from a
/// seed value, then a final row whose inline-string cell is sized to land on the
/// exact target, the way <see cref="CsvFileGenerator"/> pads its last field.
/// Large targets spread each row across more numeric columns so the sheet stays
/// within Excel's row limit.
/// </summary>
public sealed class XlsxFileGenerator : IFileGenerator
{
    // Excel's hard limit; rows past it are dropped as corrupt on open.
    private const int MaxRows = 1_048_576;

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

    // The whole archive minus the sheet's header and data rows: ZIP framing,
    // the fixed parts, and the sheet's own prefix/suffix.
    private static readonly long PackageOverhead =
        ZipWriter.OverheadFor(PartNames)
        + FixedParts.Sum(p => (long)p.Content.Length)
        + SheetPrefix.Length + SheetSuffix.Length;

    private static readonly SheetLayout Narrow = new(1);

    public string TypeKey => "xlsx";
    public string MimeType => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    public string FileExtension => "xlsx";

    // Header row + a single final row (Id 1, empty padding cell): the smallest
    // sheet that still reads as a table.
    public long MinSizeBytes => PackageOverhead + Narrow.HeaderRow().Length + Narrow.FinalRow(2, 1, string.Empty).Length;

    public async Task GenerateAsync(Stream output, long targetSizeBytes, int? seed, CancellationToken cancellationToken = default)
    {
        if (targetSizeBytes < MinSizeBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(targetSizeBytes));
        }

        // seed picks the starting Id, rows counting up from there (like csv).
        var start = seed is int s && s >= 1 ? (long)s : 1L;
        var layout = new SheetLayout(NumericColumnsFor(targetSizeBytes));

        // ZipWriter needs the entry's CRC in the local header ahead of the data,
        // so the sheet is generated once to hash it, then again to stream it.
        var crc = new Crc32();
        long sheetLength = 0;
        foreach (var chunk in SheetChunks(targetSizeBytes, start, layout))
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
                foreach (var chunk in SheetChunks(targetSizeBytes, start, layout))
                {
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(chunk), ct);
                }
            },
            cancellationToken);

        await writer.FinishAsync(cancellationToken);
    }

    // Fewest numeric columns that keep the sheet within MaxRows. No numeric row
    // is shorter than the first one at Id 1 (row numbers and values only grow),
    // so the byte budget divided by its length bounds the row count from above.
    private static int NumericColumnsFor(long targetSizeBytes)
    {
        var budget = targetSizeBytes - PackageOverhead;
        var columns = 1;
        while (budget / new SheetLayout(columns).NumericRow(2, 1).Length > MaxRows - 2) // header + final row
        {
            columns++;
        }

        return columns;
    }

    // Deterministic in (targetSizeBytes, start, layout): full numeric rows while
    // one more still leaves room for a minimal final row, then the final row's
    // inline string is sized to consume the exact remainder.
    private static IEnumerable<string> SheetChunks(long targetSizeBytes, long start, SheetLayout layout)
    {
        var header = layout.HeaderRow();
        yield return SheetPrefix;
        yield return header;

        var remaining = targetSizeBytes - PackageOverhead - header.Length;
        long row = 2;
        var value = start;

        // A large seed's digit width may not fit a near-minimum size; fall back
        // to Id 1 so any size at or above the minimum still works.
        if (remaining < layout.FinalRow(row, value, string.Empty).Length)
        {
            value = 1;
        }

        while (true)
        {
            var numericRow = layout.NumericRow(row, value);
            var nextFinalMin = layout.FinalRow(row + 1, value + 1, string.Empty).Length;

            if (remaining >= numericRow.Length + nextFinalMin)
            {
                yield return numericRow;
                remaining -= numericRow.Length;
                row++;
                value++;
                continue;
            }

            var padLength = (int)(remaining - layout.FinalRow(row, value, string.Empty).Length);
            yield return layout.FinalRow(row, value, MakePadding(padLength));
            break;
        }

        yield return SheetSuffix;
    }

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

    // Row shapes for a given width: numeric cells (Id, Id*2, Id*3, ...) in the
    // first columns, then one inline-string "Value" column only the final row fills.
    private sealed class SheetLayout
    {
        private readonly int _numericColumns;
        private readonly string[] _columnNames;

        public SheetLayout(int numericColumns)
        {
            _numericColumns = numericColumns;
            _columnNames = [.. Enumerable.Range(1, numericColumns + 1).Select(ColumnName)];
        }

        public string HeaderRow()
        {
            var sb = new StringBuilder("<row r=\"1\">");
            for (var c = 1; c <= _numericColumns + 1; c++)
            {
                var title = c == 1 ? "Id"
                    : c <= _numericColumns ? string.Create(CultureInfo.InvariantCulture, $"Id*{c}")
                    : "Value";
                sb.Append($"<c r=\"{_columnNames[c - 1]}1\" t=\"inlineStr\"><is><t>{title}</t></is></c>");
            }

            return sb.Append("</row>").ToString();
        }

        public string NumericRow(long row, long value) =>
            AppendNumericCells(StartRow(row), row, value).Append("</row>").ToString();

        public string FinalRow(long row, long value, string padding) =>
            AppendNumericCells(StartRow(row), row, value)
                .Append(CultureInfo.InvariantCulture, $"<c r=\"{_columnNames[_numericColumns]}{row}\" t=\"inlineStr\"><is><t>")
                .Append(padding)
                .Append("</t></is></c></row>")
                .ToString();

        private static StringBuilder StartRow(long row) =>
            new StringBuilder().Append(CultureInfo.InvariantCulture, $"<row r=\"{row}\">");

        private StringBuilder AppendNumericCells(StringBuilder sb, long row, long value)
        {
            for (var c = 1; c <= _numericColumns; c++)
            {
                sb.Append(CultureInfo.InvariantCulture, $"<c r=\"{_columnNames[c - 1]}{row}\"><v>{value * c}</v></c>");
            }

            return sb;
        }

        // 1 -> A, 26 -> Z, 27 -> AA.
        private static string ColumnName(int column)
        {
            var name = string.Empty;
            while (column > 0)
            {
                column--;
                name = (char)('A' + column % 26) + name;
                column /= 26;
            }

            return name;
        }
    }
}
