using DummyFileApi.Data;
using DummyFileApi.Options;
using DummyFileApi.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DummyFileApi.Tests.Services;

public class GenerationRateLimiterTests
{
    private static AppDbContext CreateInMemoryDb()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var db = new AppDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }

    private static GenerationRequest CreateRow(string clientId, DateTime createdAtUtc) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = clientId,
        FileType = "txt",
        RequestedSizeBytes = 1024,
        ActualSizeBytes = 1024,
        CreatedAtUtc = createdAtUtc,
        DurationMs = 5,
    };

    private static GenerationRateLimiter CreateLimiter(AppDbContext db, int maxPerHour) =>
        new(db, Microsoft.Extensions.Options.Options.Create(new RateLimitingOptions { MaxPerHour = maxPerHour }));

    [Fact]
    public async Task CheckAsync_UnderLimit_IsAllowed()
    {
        using var db = CreateInMemoryDb();
        db.GenerationRequests.Add(CreateRow("1.2.3.4", DateTime.UtcNow));
        await db.SaveChangesAsync();
        var limiter = CreateLimiter(db, maxPerHour: 2);

        var result = await limiter.CheckAsync("1.2.3.4", CancellationToken.None);

        Assert.True(result.IsAllowed);
    }

    [Fact]
    public async Task CheckAsync_AtLimit_IsExceededWithPositiveRetryAfter()
    {
        using var db = CreateInMemoryDb();
        db.GenerationRequests.Add(CreateRow("1.2.3.4", DateTime.UtcNow.AddMinutes(-10)));
        await db.SaveChangesAsync();
        var limiter = CreateLimiter(db, maxPerHour: 1);

        var result = await limiter.CheckAsync("1.2.3.4", CancellationToken.None);

        Assert.False(result.IsAllowed);
        // Oldest request ages out of the 1-hour window in ~50 minutes.
        Assert.InRange(result.RetryAfterSeconds, 49 * 60, 50 * 60);
    }

    [Fact]
    public async Task CheckAsync_RequestOutsideWindow_DoesNotCountTowardLimit()
    {
        using var db = CreateInMemoryDb();
        db.GenerationRequests.Add(CreateRow("1.2.3.4", DateTime.UtcNow.AddHours(-2)));
        await db.SaveChangesAsync();
        var limiter = CreateLimiter(db, maxPerHour: 1);

        var result = await limiter.CheckAsync("1.2.3.4", CancellationToken.None);

        Assert.True(result.IsAllowed);
    }

    [Fact]
    public async Task CheckAsync_TracksClientsIndependently()
    {
        using var db = CreateInMemoryDb();
        db.GenerationRequests.Add(CreateRow("1.2.3.4", DateTime.UtcNow));
        await db.SaveChangesAsync();
        var limiter = CreateLimiter(db, maxPerHour: 1);

        var result = await limiter.CheckAsync("5.6.7.8", CancellationToken.None);

        Assert.True(result.IsAllowed);
    }

    [Fact]
    public async Task CheckAsync_ZeroMaxPerHourWithNoPriorRequests_IsExceededWithoutThrowing()
    {
        // A brand-new client has no rows to compute a "when does the oldest
        // one age out" retry hint from, so this must not crash.
        using var db = CreateInMemoryDb();
        var limiter = CreateLimiter(db, maxPerHour: 0);

        var result = await limiter.CheckAsync("1.2.3.4", CancellationToken.None);

        Assert.False(result.IsAllowed);
        Assert.True(result.RetryAfterSeconds > 0);
    }

    [Fact]
    public async Task CheckAsync_ExposesConfiguredLimit()
    {
        using var db = CreateInMemoryDb();
        var limiter = CreateLimiter(db, maxPerHour: 7);

        var result = await limiter.CheckAsync("1.2.3.4", CancellationToken.None);

        Assert.Equal(7, result.Limit);
    }

    [Fact]
    public async Task CheckAsync_Remaining_CountsTheCallersOwnRequestAsSpent()
    {
        using var db = CreateInMemoryDb();
        db.GenerationRequests.Add(CreateRow("1.2.3.4", DateTime.UtcNow));
        db.GenerationRequests.Add(CreateRow("1.2.3.4", DateTime.UtcNow));
        await db.SaveChangesAsync();
        var limiter = CreateLimiter(db, maxPerHour: 10);

        var result = await limiter.CheckAsync("1.2.3.4", CancellationToken.None);

        // 2 already recorded, and this one is about to be: 7 left afterwards.
        Assert.Equal(7, result.Remaining);
    }

    [Fact]
    public async Task CheckAsync_Remaining_IsZeroOnTheLastAllowedRequest()
    {
        using var db = CreateInMemoryDb();
        db.GenerationRequests.Add(CreateRow("1.2.3.4", DateTime.UtcNow));
        await db.SaveChangesAsync();
        var limiter = CreateLimiter(db, maxPerHour: 2);

        var result = await limiter.CheckAsync("1.2.3.4", CancellationToken.None);

        Assert.True(result.IsAllowed);
        Assert.Equal(0, result.Remaining);
    }

    [Fact]
    public async Task CheckAsync_Remaining_IsZeroWhenExceeded()
    {
        using var db = CreateInMemoryDb();
        db.GenerationRequests.Add(CreateRow("1.2.3.4", DateTime.UtcNow));
        await db.SaveChangesAsync();
        var limiter = CreateLimiter(db, maxPerHour: 1);

        var result = await limiter.CheckAsync("1.2.3.4", CancellationToken.None);

        Assert.False(result.IsAllowed);
        Assert.Equal(0, result.Remaining);
    }

    [Fact]
    public async Task CheckAsync_Reset_IsWhenTheOldestRequestAgesOut()
    {
        using var db = CreateInMemoryDb();
        var oldest = DateTime.UtcNow.AddMinutes(-10);
        db.GenerationRequests.Add(CreateRow("1.2.3.4", oldest));
        db.GenerationRequests.Add(CreateRow("1.2.3.4", DateTime.UtcNow));
        await db.SaveChangesAsync();
        var limiter = CreateLimiter(db, maxPerHour: 10);

        var result = await limiter.CheckAsync("1.2.3.4", CancellationToken.None);

        var expected = new DateTimeOffset(oldest.AddHours(1), TimeSpan.Zero).ToUnixTimeSeconds();
        Assert.InRange(result.ResetUnixSeconds, expected - 2, expected + 2);
    }

    [Fact]
    public async Task CheckAsync_Reset_IsAFullWindowAheadWhenNothingIsRecorded()
    {
        using var db = CreateInMemoryDb();
        var limiter = CreateLimiter(db, maxPerHour: 10);

        var result = await limiter.CheckAsync("1.2.3.4", CancellationToken.None);

        var expected = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        Assert.InRange(result.ResetUnixSeconds, expected - 2, expected + 2);
    }
}
