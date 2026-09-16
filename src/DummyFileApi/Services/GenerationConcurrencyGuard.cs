using DummyFileApi.Options;
using Microsoft.Extensions.Options;

namespace DummyFileApi.Services;

/// <summary>
/// Caps how many generations run at once, so a burst of large requests can't
/// pin a small single-instance host. Global rather than per-client, and soft
/// protection rather than a quota: it sheds the overflow with a 503 instead
/// of queueing.
/// </summary>
public sealed class GenerationConcurrencyGuard : IDisposable
{
    private readonly SemaphoreSlim? _slots;

    public GenerationConcurrencyGuard(IOptions<FileGenerationOptions> options)
    {
        var max = options.Value.MaxConcurrentGenerations;
        _slots = max > 0 ? new SemaphoreSlim(max, max) : null;
        RetryAfterSeconds = Math.Max(options.Value.BusyRetryAfterSeconds, 1);
    }

    public int RetryAfterSeconds { get; }

    /// <summary>Takes a slot if one is free, without waiting. Release() on success.</summary>
    public bool TryAcquire() => _slots is null || _slots.Wait(0);

    public void Release() => _slots?.Release();

    public void Dispose() => _slots?.Dispose();
}
