using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace DummyFileApi.Tests.Integration;

// Pinned to Production: the docs are public, so what matters is that a deployed
// instance serves them, not that Development does.
public class SwaggerDocumentTests(ProductionWebApplicationFactory factory) : IClassFixture<ProductionWebApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task SwaggerDocument_ReportsTheAssemblyVersionAndAllEndpoints()
    {
        var response = await _client.GetAsync("/swagger/v1/swagger.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        var info = root.GetProperty("info");
        var expectedVersion = typeof(Program).Assembly.GetName().Version!.ToString(3);
        Assert.Equal(expectedVersion, info.GetProperty("version").GetString());
        Assert.Equal("Dummy File API", info.GetProperty("title").GetString());
        Assert.Contains("no API key", info.GetProperty("description").GetString());

        var paths = root.GetProperty("paths").EnumerateObject().Select(p => p.Name).ToHashSet();
        Assert.Equal(["/api/files/generate", "/api/files/types", "/api/files/history"], paths);
    }

    [Fact]
    public async Task SwaggerDocument_DocumentsEveryStatusGenerateCanReturn()
    {
        var response = await _client.GetAsync("/swagger/v1/swagger.json");

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var responses = document.RootElement
            .GetProperty("paths").GetProperty("/api/files/generate")
            .GetProperty("get").GetProperty("responses");

        var statuses = responses.EnumerateObject().Select(p => p.Name).ToHashSet();
        Assert.Equal(["200", "400", "429", "503"], statuses);

        // The headers aren't modelled in the schema, only named in the description.
        Assert.Contains("X-RateLimit-Remaining", responses.GetProperty("200").GetProperty("description").GetString());
    }

    [Fact]
    public async Task SwaggerUi_IsServed()
    {
        var response = await _client.GetAsync("/swagger/index.html");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);

        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Dummy File API reference", html);
    }

    // The landing page and README both point people at /swagger, which only
    // resolves through the UI's redirect to its index.
    [Fact]
    public async Task SwaggerPrefix_RedirectsToTheUi()
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var response = await client.GetAsync("/swagger");

        Assert.Equal(HttpStatusCode.MovedPermanently, response.StatusCode);
        Assert.Equal("swagger/index.html", response.Headers.Location?.ToString());
    }
}
