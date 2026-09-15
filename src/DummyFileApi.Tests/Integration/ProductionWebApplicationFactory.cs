using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;

namespace DummyFileApi.Tests.Integration;

/// <summary>
/// Same as the base factory but pinned to the Production environment, so tests
/// assert what a deployed instance serves rather than what Development adds.
/// </summary>
public class ProductionWebApplicationFactory : IntegrationTestWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Production);
        base.ConfigureWebHost(builder);
    }
}
