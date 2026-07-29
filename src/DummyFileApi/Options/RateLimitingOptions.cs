namespace DummyFileApi.Options;

public class RateLimitingOptions
{
    public const string SectionName = "RateLimiting";

    public int MaxPerHour { get; set; } = 100;
}
