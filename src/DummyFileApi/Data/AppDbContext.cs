using Microsoft.EntityFrameworkCore;

namespace DummyFileApi.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<GenerationRequest> GenerationRequests => Set<GenerationRequest>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Both the history query and the upcoming rate-limit check filter by
        // client and time, so index that pair.
        modelBuilder.Entity<GenerationRequest>()
            .HasIndex(r => new { r.ClientId, r.CreatedAtUtc });
    }
}
