using DummyFileApi.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DummyFileApi.Tests.Data;

public class AppDbContextTests
{
    [Fact]
    public async Task CreatedAtUtc_RoundTripsWithUtcKind()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        using var db = new AppDbContext(options);
        db.Database.EnsureCreated();

        var createdAt = new DateTime(2026, 7, 23, 12, 0, 0, DateTimeKind.Utc);
        db.GenerationRequests.Add(new GenerationRequest
        {
            Id = Guid.NewGuid(),
            ClientId = "10.1.2.3",
            FileType = "txt",
            RequestedSizeBytes = 1024,
            ActualSizeBytes = 1024,
            CreatedAtUtc = createdAt,
            DurationMs = 5,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var row = await db.GenerationRequests.SingleAsync();

        Assert.Equal(createdAt, row.CreatedAtUtc);
        Assert.Equal(DateTimeKind.Utc, row.CreatedAtUtc.Kind);
    }
}
