using System.Globalization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace DummyFileApi.Tests.Integration;

/// <summary>
/// Boots the real ASP.NET Core pipeline against an isolated SQLite file per
/// instance. Trusts X-Forwarded-For unconditionally so tests can pick a fake
/// client IP to isolate their own rate-limit/history state.
/// </summary>
public class IntegrationTestWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"dummyfileapi-test-{Guid.NewGuid():N}.db");

    protected virtual int MaxPerHour => 100;

    protected virtual long MaxSizeBytes => 52_428_800;

    // Well above the production default: test classes run in parallel, and a
    // spurious 503 from one of them would be a confusing flake.
    protected virtual int MaxConcurrentGenerations => 256;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Pooling=False so Dispose() below can delete the file immediately —
                // pooled Sqlite connections otherwise outlive host shutdown and lock it.
                ["ConnectionStrings:Default"] = $"Data Source={_dbPath};Pooling=False",
                ["RateLimiting:MaxPerHour"] = MaxPerHour.ToString(CultureInfo.InvariantCulture),
                ["FileGeneration:MaxSizeBytes"] = MaxSizeBytes.ToString(CultureInfo.InvariantCulture),
                ["FileGeneration:MaxConcurrentGenerations"] = MaxConcurrentGenerations.ToString(CultureInfo.InvariantCulture),
                ["Proxy:TrustForwardedHeaders"] = "true",
            });
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing)
        {
            return;
        }

        foreach (var suffix in new[] { "", "-shm", "-wal" })
        {
            DeleteWithRetry(_dbPath + suffix);
        }
    }

    // A brief external lock (e.g. an AV scan) can outlast connection disposal;
    // retry rather than let that fail the test class's cleanup.
    private static void DeleteWithRetry(string path, int attempts = 5)
    {
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                File.Delete(path);
                return;
            }
            catch (IOException) when (attempt < attempts)
            {
                Thread.Sleep(50);
            }
        }
    }
}
