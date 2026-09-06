namespace DummyFileApi.Generators;

/// <summary>Single source of truth for which file types exist — DI registration and GET /api/files/types both iterate this list.</summary>
public static class FileGeneratorRegistry
{
    public static readonly IReadOnlyList<(string Key, Type Impl)> All =
    [
        ("txt", typeof(TxtFileGenerator)),
        ("csv", typeof(CsvFileGenerator)),
        ("png", typeof(PngFileGenerator)),
        ("jpeg", typeof(JpegFileGenerator)),
        ("pdf", typeof(PdfFileGenerator)),
        ("zip", typeof(ZipFileGenerator)),
        ("docx", typeof(DocxFileGenerator)),
        ("xlsx", typeof(XlsxFileGenerator)),
        ("json", typeof(JsonFileGenerator)),
        ("tar", typeof(TarFileGenerator)),
        ("gzip", typeof(GzipFileGenerator)),
    ];
}
