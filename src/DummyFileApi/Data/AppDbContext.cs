using Microsoft.EntityFrameworkCore;

namespace DummyFileApi.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<GenerationRequest> GenerationRequests => Set<GenerationRequest>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // History and rate-limit queries filter by client and time, so index
        // that pair.
        modelBuilder.Entity<GenerationRequest>()
            .HasIndex(r => new { r.ClientId, r.CreatedAtUtc });

        // SQLite stores DateTime as text without timezone info and reads it
        // back as Unspecified; restamp Utc so serialized values keep the 'Z'.
        modelBuilder.Entity<GenerationRequest>()
            .Property(r => r.CreatedAtUtc)
            .HasConversion(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
    }
}
