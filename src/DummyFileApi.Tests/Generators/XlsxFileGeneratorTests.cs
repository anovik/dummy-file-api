using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Validation;
using DummyFileApi.Generators;

namespace DummyFileApi.Tests.Generators;

public class XlsxFileGeneratorTests
{
    private readonly XlsxFileGenerator _generator = new();

    public static IEnumerable<object[]> TargetSizes()
    {
        var min = new XlsxFileGenerator().MinSizeBytes;
        yield return [min]; // header row + one empty final row
        yield return [min + 1];
        yield return [4096L];
        yield return [65536L];
        yield return [min + 65536]; // rows span exactly one internal chunk
        yield return [min + 65537];
        yield return [2 * 1024 * 1024 + 7L];
    }

    [Theory]
    [MemberData(nameof(TargetSizes))]
    public async Task GenerateAsync_ProducesExactRequestedByteCount(long targetSizeBytes)
    {
        using var stream = new MemoryStream();

        await _generator.GenerateAsync(stream, targetSizeBytes, seed: null);

        Assert.Equal(targetSizeBytes, stream.Length);
    }

    [Theory]
    [MemberData(nameof(TargetSizes))]
    public async Task GenerateAsync_OpensAsASchemaValidSpreadsheet(long targetSizeBytes)
    {
        using var stream = new MemoryStream();
        await _generator.GenerateAsync(stream, targetSizeBytes, seed: 3);
        stream.Position = 0;

        using var doc = SpreadsheetDocument.Open(stream, isEditable: false);

        var errors = new OpenXmlValidator().Validate(doc).ToList();
        Assert.True(errors.Count == 0, string.Join("; ", errors.Select(e => e.Description)));

        var rows = RowsOf(doc);
        Assert.True(rows.Count >= 2); // header + at least the final row
        // Row indices are contiguous from 1.
        for (var i = 0; i < rows.Count; i++)
        {
            Assert.Equal((uint)(i + 1), rows[i].RowIndex!.Value);
        }
    }

    [Fact]
    public async Task GenerateAsync_AtMinSize_HasOnlyHeaderAndOneEmptyFinalRow()
    {
        using var stream = new MemoryStream();
        await _generator.GenerateAsync(stream, _generator.MinSizeBytes, seed: null);
        stream.Position = 0;

        using var doc = SpreadsheetDocument.Open(stream, isEditable: false);
        var rows = RowsOf(doc);

        Assert.Equal(2, rows.Count);
        var finalCells = rows[1].Elements<Cell>().ToList();
        Assert.Equal("A2", finalCells[0].CellReference!.Value);
        Assert.Equal("1", finalCells[0].CellValue!.Text);
        Assert.Equal(CellValues.InlineString, finalCells[1].DataType!.Value);
        Assert.Equal(string.Empty, finalCells[1].InlineString!.Text!.Text);
    }

    [Fact]
    public async Task GenerateAsync_SeededStartingId_CountsUpFromThatValue()
    {
        using var stream = new MemoryStream();
        await _generator.GenerateAsync(stream, targetSizeBytes: 8192, seed: 500);
        stream.Position = 0;

        using var doc = SpreadsheetDocument.Open(stream, isEditable: false);
        var rows = RowsOf(doc);

        // rows[0] is the header; the first data row carries the seeded Id.
        Assert.Equal("500", rows[1].Elements<Cell>().Single().CellValue!.Text);
        Assert.Equal("501", rows[2].Elements<Cell>().Single().CellValue!.Text);
    }

    [Fact]
    public async Task GenerateAsync_SeedChangesBytesButNotSize()
    {
        using var first = new MemoryStream();
        using var second = new MemoryStream();

        await _generator.GenerateAsync(first, targetSizeBytes: 8192, seed: null);
        await _generator.GenerateAsync(second, targetSizeBytes: 8192, seed: 42);

        Assert.Equal(first.Length, second.Length);
        Assert.NotEqual(first.ToArray(), second.ToArray());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(int.MinValue)]
    public async Task GenerateAsync_NonPositiveSeed_ProducesValidOutputStartingAtId1(int seed)
    {
        using var stream = new MemoryStream();

        await _generator.GenerateAsync(stream, targetSizeBytes: 8192, seed: seed);

        Assert.Equal(8192, stream.Length);
        stream.Position = 0;
        using var doc = SpreadsheetDocument.Open(stream, isEditable: false);
        Assert.Equal("1", RowsOf(doc)[1].Elements<Cell>().Single().CellValue!.Text);
    }

    [Fact]
    public async Task GenerateAsync_LargeSeedNearMinSize_FallsBackToId1AndStaysExact()
    {
        var target = _generator.MinSizeBytes + 4;
        using var stream = new MemoryStream();

        await _generator.GenerateAsync(stream, target, seed: 999_999_999);

        Assert.Equal(target, stream.Length);
        stream.Position = 0;
        using var doc = SpreadsheetDocument.Open(stream, isEditable: false);
        Assert.Equal("1", RowsOf(doc)[^1].Elements<Cell>().First().CellValue!.Text);
    }

    [Fact]
    public async Task GenerateAsync_IsDeterministic()
    {
        using var first = new MemoryStream();
        using var second = new MemoryStream();

        await _generator.GenerateAsync(first, targetSizeBytes: 50_000, seed: 3);
        await _generator.GenerateAsync(second, targetSizeBytes: 50_000, seed: 3);

        Assert.Equal(first.ToArray(), second.ToArray());
    }

    [Fact]
    public async Task GenerateAsync_BelowMinSize_Throws()
    {
        using var stream = new MemoryStream();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _generator.GenerateAsync(stream, _generator.MinSizeBytes - 1, seed: null));
    }

    [Fact]
    public async Task GenerateAsync_ArchiveHoldsExactlyTheSevenOoxmlPartsAsStoredXml()
    {
        using var stream = new MemoryStream();
        await _generator.GenerateAsync(stream, targetSizeBytes: 16_384, seed: null);
        stream.Position = 0;

        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        Assert.Equal(
            [
                "[Content_Types].xml", "_rels/.rels", "xl/_rels/workbook.xml.rels",
                "xl/workbook.xml", "docProps/core.xml", "docProps/app.xml",
                "xl/worksheets/sheet1.xml",
            ],
            archive.Entries.Select(e => e.FullName));

        foreach (var entry in archive.Entries)
        {
            Assert.Equal(entry.Length, entry.CompressedLength); // STORE: no compression
            using var entryStream = entry.Open();
            Assert.NotNull(XDocument.Load(entryStream)); // every part is well-formed XML
        }
    }

    [Fact]
    public async Task GenerateAsync_AtDefaultMaxSize_WidensRowsToStayWithinExcelRowLimit()
    {
        const long target = 100L * 1024 * 1024;
        using var stream = new MemoryStream();
        await _generator.GenerateAsync(stream, target, seed: null);

        Assert.Equal(target, stream.Length);
        stream.Position = 0;
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        using var sheet = archive.GetEntry("xl/worksheets/sheet1.xml")!.Open();
        using var reader = XmlReader.Create(sheet);

        // Streamed rather than loaded: the sheet XML is ~100 MB.
        var rowCount = 0;
        long currentRow = 0;
        var headerCells = new List<string>();
        var firstDataCells = new List<string>();
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            if (reader.LocalName == "row")
            {
                rowCount++;
                currentRow = long.Parse(reader.GetAttribute("r")!);
                Assert.Equal(rowCount, currentRow);
            }
            else if (reader.LocalName == "c" && currentRow == 1)
            {
                headerCells.Add(reader.GetAttribute("r")!);
            }
            else if (reader.LocalName == "v" && currentRow == 2)
            {
                firstDataCells.Add(reader.ReadElementContentAsString());
            }
        }

        Assert.InRange(rowCount, 2, 1_048_576);

        // Id 1 in column A, then Id*2, Id*3, ... in the extra numeric columns,
        // then the "Value" column the header names but only the final row fills.
        Assert.True(firstDataCells.Count > 1);
        Assert.Equal(firstDataCells.Select((_, i) => (i + 1).ToString()), firstDataCells);
        Assert.Equal(firstDataCells.Count + 1, headerCells.Count);
        Assert.Equal(Enumerable.Range(0, headerCells.Count).Select(i => $"{(char)('A' + i)}1"), headerCells);
    }

    private static List<Row> RowsOf(SpreadsheetDocument doc)
    {
        var worksheet = doc.WorkbookPart!.WorksheetParts.Single().Worksheet;
        var sheetData = worksheet!.GetFirstChild<SheetData>();
        Assert.NotNull(sheetData);
        return sheetData.Elements<Row>().ToList();
    }
}
