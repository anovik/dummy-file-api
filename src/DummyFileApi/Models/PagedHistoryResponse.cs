namespace DummyFileApi.Models;

public record PagedHistoryResponse(
    IReadOnlyList<GenerationHistoryItemDto> Items,
    int Page,
    int PageSize,
    int TotalCount);
