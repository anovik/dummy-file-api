using DummyFileApi.Data;
using DummyFileApi.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DummyFileApi.Services;

public readonly record struct RateLimitResult(bool IsAllowed, int RetryAfterSeconds, int Limit)
{
    public static RateLimitResult Allowed(int limit) => new(true, 0, limit);

    public static RateLimitResult Exceeded(int retryAfterSeconds, int limit) => new(false, retryAfterSeconds, limit);
}

/// <summary>
/// DB-backed sliding-window rate limiter, reusing GenerationRequests (the
/// same table history reads from) as the single source of truth per client.
/// Best-effort, not atomic: concurrent requests from the same client can
/// both pass the check before either is recorded.
/// </summary>
public class GenerationRateLimiter(AppDbContext dbContext, IOptions<RateLimitingOptions> options)
{
    private static readonly TimeSpan Window = TimeSpan.FromHours(1);

    public async Task<RateLimitResult> CheckAsync(string clientId, CancellationToken cancellationToken)
    {
        var maxPerHour = options.Value.MaxPerHour;
        var cutoff = DateTime.UtcNow - Window;

        var window = await dbContext.GenerationRequests
            .Where(r => r.ClientId == clientId && r.CreatedAtUtc > cutoff)
            .GroupBy(_ => 1)
            .Select(g => new { Count = g.Count(), Oldest = g.Min(r => r.CreatedAtUtc) })
            .SingleOrDefaultAsync(cancellationToken);

        if ((window?.Count ?? 0) < maxPerHour)
        {
            return RateLimitResult.Allowed(maxPerHour);
        }

        // window is null when there are zero rows in the window at all, which
        // only reaches here if maxPerHour <= 0 — there's no oldest request to
        // measure from, so fall back to a full window as the retry hint.
        var retryAfterSeconds = window is null
            ? (int)Window.TotalSeconds
            : Math.Max((int)Math.Ceiling((window.Oldest + Window - DateTime.UtcNow).TotalSeconds), 1);

        return RateLimitResult.Exceeded(retryAfterSeconds, maxPerHour);
    }
}
