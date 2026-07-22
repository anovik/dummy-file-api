namespace DummyFileApi.Generators;

public interface IFileGenerator
{
    string TypeKey { get; }
    string MimeType { get; }
    string FileExtension { get; }

    /// <summary>Smallest byte count this generator can produce as a structurally valid file.</summary>
    long MinSizeBytes { get; }

    // Async because ASP.NET Core/Kestrel disallows synchronous writes to the
    // response body stream by default — this is the real streaming path, not
    // a buffered write.
    Task GenerateAsync(Stream output, long targetSizeBytes, int? seed, CancellationToken cancellationToken = default);
}
