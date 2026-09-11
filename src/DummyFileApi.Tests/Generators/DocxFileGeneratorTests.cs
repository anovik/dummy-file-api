using System.IO.Compression;
using System.Xml.Linq;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using DocumentFormat.OpenXml.Wordprocessing;
using DummyFileApi.Generators;
using ImageSharpImage = SixLabors.ImageSharp.Image;

namespace DummyFileApi.Tests.Generators;

public class DocxFileGeneratorTests
{
    private const int MaxFillerText = 1024 * 1024;

    // Largest target whose filler text fits the cap; one byte more embeds an image.
    private static readonly long LastTextOnlySize = new DocxFileGenerator().MinSizeBytes + MaxFillerText;

    private readonly DocxFileGenerator _generator = new();

    public static IEnumerable<object[]> TargetSizes()
    {
        var min = new DocxFileGenerator().MinSizeBytes;
        yield return [min]; // zero-length filler paragraph
        yield return [min + 1];
        yield return [4096L];
        yield return [65536L];
        yield return [min + 65536]; // filler spans exactly one internal chunk
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
    public async Task GenerateAsync_OpensAsASchemaValidWordDocument(long targetSizeBytes)
    {
        using var stream = new MemoryStream();
        await _generator.GenerateAsync(stream, targetSizeBytes, seed: 3);
        stream.Position = 0;

        using var doc = WordprocessingDocument.Open(stream, isEditable: false);

        Assert.NotNull(doc.MainDocumentPart?.Document?.Body);

        var errors = new OpenXmlValidator().Validate(doc).ToList();
        Assert.True(errors.Count == 0, string.Join("; ", errors.Select(e => e.Description)));

        // Title paragraph + the one filler paragraph, plus an image paragraph when one is embedded.
        var imageCount = doc.MainDocumentPart!.ImageParts.Count();
        Assert.Equal(2 + imageCount, doc.MainDocumentPart.Document!.Body!.Elements<Paragraph>().Count());
    }

    [Fact]
    public async Task GenerateAsync_UpToOneMebibyteOfFillerText_EmbedsNoImage()
    {
        using var stream = new MemoryStream();
        await _generator.GenerateAsync(stream, LastTextOnlySize, seed: null);

        Assert.Equal(LastTextOnlySize, stream.Length);
        stream.Position = 0;
        using var doc = WordprocessingDocument.Open(stream, isEditable: false);

        Assert.Empty(doc.MainDocumentPart!.ImageParts);
        Assert.Equal(MaxFillerText, doc.MainDocumentPart.Document!.Body!.Elements<Paragraph>().Last().InnerText.Length);
    }

    [Theory]
    [InlineData(0L)] // first size past the text cap: the smallest valid PNG
    [InlineData(1L)]
    [InlineData(65_537L)]
    [InlineData(5L * 1024 * 1024)]
    public async Task GenerateAsync_PastTheTextCap_EmbedsAPngInsteadOfMoreText(long beyondCap)
    {
        var target = LastTextOnlySize + 1 + beyondCap;
        using var stream = new MemoryStream();
        await _generator.GenerateAsync(stream, target, seed: 3);

        Assert.Equal(target, stream.Length);
        stream.Position = 0;
        using var doc = WordprocessingDocument.Open(stream, isEditable: false);

        var errors = new OpenXmlValidator().Validate(doc).ToList();
        Assert.True(errors.Count == 0, string.Join("; ", errors.Select(e => e.Description)));

        var imagePart = Assert.Single(doc.MainDocumentPart!.ImageParts);
        Assert.Equal("image/png", imagePart.ContentType);

        // The drawing's blip points at the embedded part.
        var blip = doc.MainDocumentPart.Document!.Descendants<DocumentFormat.OpenXml.Drawing.Blip>().Single();
        Assert.Same(imagePart, doc.MainDocumentPart.GetPartById(blip.Embed!.Value!));

        using var imageStream = imagePart.GetStream();
        using var image = ImageSharpImage.Load(imageStream);
        Assert.True(image.Width >= 16);

        Assert.True(doc.MainDocumentPart.Document.Body!.Elements<Paragraph>().Last().InnerText.Length < MaxFillerText);
    }

    [Fact]
    public async Task GenerateAsync_AtDefaultMaxSize_KeepsTextSmallAndStaysExact()
    {
        const long target = 100L * 1024 * 1024;
        using var stream = new MemoryStream();
        await _generator.GenerateAsync(stream, target, seed: null);

        Assert.Equal(target, stream.Length);
        stream.Position = 0;
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        // Far under Word's 32 MB text limit; the image carries the bulk.
        Assert.True(archive.GetEntry("word/document.xml")!.Length < 2 * MaxFillerText);
        using var imageStream = archive.GetEntry("word/media/image1.png")!.Open();
        var info = ImageSharpImage.Identify(imageStream);
        Assert.Equal(4096, info.Width);
    }

    [Fact]
    public async Task GenerateAsync_AtMinSize_FillerParagraphIsEmpty()
    {
        using var stream = new MemoryStream();
        await _generator.GenerateAsync(stream, _generator.MinSizeBytes, seed: null);
        stream.Position = 0;

        using var doc = WordprocessingDocument.Open(stream, isEditable: false);
        var paragraphs = doc.MainDocumentPart!.Document!.Body!.Elements<Paragraph>().ToList();

        Assert.Equal(2, paragraphs.Count);
        Assert.Equal(string.Empty, paragraphs[1].InnerText);
    }

    [Fact]
    public async Task GenerateAsync_SeededFillerPhrase_AppearsInTheDocumentText()
    {
        using var stream = new MemoryStream();
        await _generator.GenerateAsync(stream, targetSizeBytes: 8192, seed: 2);
        stream.Position = 0;

        using var doc = WordprocessingDocument.Open(stream, isEditable: false);
        var filler = doc.MainDocumentPart!.Document!.Body!.Elements<Paragraph>().Last().InnerText;

        Assert.StartsWith("How vexingly quick daft zebras jump. ", filler);
    }

    [Fact]
    public async Task GenerateAsync_SeedChangesBytesButNotSize()
    {
        using var first = new MemoryStream();
        using var second = new MemoryStream();

        await _generator.GenerateAsync(first, targetSizeBytes: 8192, seed: null);
        await _generator.GenerateAsync(second, targetSizeBytes: 8192, seed: 1);

        Assert.Equal(first.Length, second.Length);
        Assert.NotEqual(first.ToArray(), second.ToArray());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public async Task GenerateAsync_NegativeSeed_ProducesValidOutput(int seed)
    {
        using var stream = new MemoryStream();

        await _generator.GenerateAsync(stream, targetSizeBytes: 8192, seed: seed);

        Assert.Equal(8192, stream.Length);
        stream.Position = 0;
        using var doc = WordprocessingDocument.Open(stream, isEditable: false);
        Assert.NotNull(doc.MainDocumentPart?.Document?.Body);
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
    public async Task GenerateAsync_ArchiveHoldsExactlyTheSixOoxmlPartsAsStoredXml()
    {
        using var stream = new MemoryStream();
        await _generator.GenerateAsync(stream, targetSizeBytes: 16_384, seed: null);
        stream.Position = 0;

        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        Assert.Equal(
            [
                "[Content_Types].xml", "_rels/.rels", "word/_rels/document.xml.rels",
                "docProps/core.xml", "docProps/app.xml", "word/document.xml",
            ],
            archive.Entries.Select(e => e.FullName));

        foreach (var entry in archive.Entries)
        {
            Assert.Equal(entry.Length, entry.CompressedLength); // STORE: no compression
            using var entryStream = entry.Open();
            Assert.NotNull(XDocument.Load(entryStream)); // every part is well-formed XML
        }
    }
}
