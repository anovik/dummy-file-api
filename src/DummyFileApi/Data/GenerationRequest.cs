namespace DummyFileApi.Data;

public class GenerationRequest
{
    public Guid Id { get; set; }
    public required string ClientId { get; set; }
    public required string FileType { get; set; }
    public long RequestedSizeBytes { get; set; }
    public long ActualSizeBytes { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public int DurationMs { get; set; }
}
