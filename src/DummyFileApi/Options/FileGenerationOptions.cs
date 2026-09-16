namespace DummyFileApi.Options;

public class FileGenerationOptions
{
    public const string SectionName = "FileGeneration";

    /// <summary>
    /// Upper bound for a single request, across every type. Sized for an
    /// unauthenticated public API: large enough to be a useful demo, small
    /// enough that one client can't drive much egress or pin the host.
    /// </summary>
    public long MaxSizeBytes { get; set; } = 52_428_800; // 50 MiB

    /// <summary>
    /// Generations allowed to run at once before /generate sheds load with a
    /// 503. Sized for a small single-instance host, where generation is
    /// CPU-bound and extra concurrency only thrashes. Zero or less disables
    /// the guard.
    /// </summary>
    public int MaxConcurrentGenerations { get; set; } = 4;

    /// <summary>Retry-After hint, in seconds, on a 503 from the concurrency guard.</summary>
    public int BusyRetryAfterSeconds { get; set; } = 5;
}
