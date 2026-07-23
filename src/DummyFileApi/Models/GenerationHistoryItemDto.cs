namespace DummyFileApi.Models;

public record GenerationHistoryItemDto(
    Guid Id,
    string FileType,
    long RequestedSizeBytes,
    long ActualSizeBytes,
    DateTime CreatedAtUtc,
    int DurationMs);
