namespace DummyFileApi.Models;

public record FileTypeDto(string Type, string MimeType, string Extension, long MinSizeBytes, long MaxSizeBytes);
