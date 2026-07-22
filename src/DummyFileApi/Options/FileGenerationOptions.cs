namespace DummyFileApi.Options;

public class FileGenerationOptions
{
    public const string SectionName = "FileGeneration";

    public long MaxSizeBytes { get; set; } = 104_857_600; // 100 MB
}
