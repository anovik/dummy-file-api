namespace DummyFileApi.Generators;

public interface IFileGenerator
{
    string TypeKey { get; }
    string MimeType { get; }
    string FileExtension { get; }

    /// <summary>Smallest byte count this generator can produce as a structurally valid file.</summary>
    long MinSizeBytes { get; }

    // Async because Kestrel disallows synchronous writes to the response body by default.
    Task GenerateAsync(Stream output, long targetSizeBytes, int? seed, CancellationToken cancellationToken = default);
}
