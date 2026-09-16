using DummyFileApi.Data;
using DummyFileApi.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DummyFileApi.Services;

/// <param name="IsAllowed">Whether the caller is under the limit.</param>
/// <param name="RetryAfterSeconds">Seconds to wait before retrying; zero when allowed.</param>
/// <param name="Limit">The configured requests-per-hour ceiling.</param>
/// <param name="Remaining">Requests left in the window after the current one is recorded.</param>
/// <param name="ResetUnixSeconds">When the oldest counted request ages out of the window, freeing a slot.</param>
public readonly record struct RateLimitResult(
    bool IsAllowed,
    int RetryAfterSeconds,
    int Limit,
    int Remaining,
    long ResetUnixSeconds);

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
        var now = DateTime.UtcNow;
        var cutoff = now - Window;

        var window = await dbContext.GenerationRequests
            .Where(r => r.ClientId == clientId && r.CreatedAtUtc > cutoff)
            .GroupBy(_ => 1)
            .Select(g => new { Count = g.Count(), Oldest = g.Min(r => r.CreatedAtUtc) })
            .SingleOrDefaultAsync(cancellationToken);

        var count = window?.Count ?? 0;

        // window is null when the client has no rows in the window at all, so
        // there's no oldest request to measure from; a full window from now
        // serves as both the reset and the retry hint.
        var resetAt = window is null ? now + Window : window.Oldest + Window;
        var resetUnixSeconds = new DateTimeOffset(DateTime.SpecifyKind(resetAt, DateTimeKind.Utc)).ToUnixTimeSeconds();

        if (count < maxPerHour)
        {
            // The caller's own request will become a row, so it's already spent.
            return new RateLimitResult(true, 0, maxPerHour, Math.Max(maxPerHour - count - 1, 0), resetUnixSeconds);
        }

        var retryAfterSeconds = Math.Max((int)Math.Ceiling((resetAt - now).TotalSeconds), 1);
        return new RateLimitResult(false, retryAfterSeconds, maxPerHour, 0, resetUnixSeconds);
    }
}
