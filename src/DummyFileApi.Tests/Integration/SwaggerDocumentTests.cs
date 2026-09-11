using System.Net;
using System.Text.Json;

namespace DummyFileApi.Tests.Integration;

public class SwaggerDocumentTests(IntegrationTestWebApplicationFactory factory) : IClassFixture<IntegrationTestWebApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task SwaggerDocument_ReportsTheAssemblyVersionAndAllEndpoints()
    {
        var response = await _client.GetAsync("/swagger/v1/swagger.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        var expectedVersion = typeof(Program).Assembly.GetName().Version!.ToString(3);
        Assert.Equal(expectedVersion, root.GetProperty("info").GetProperty("version").GetString());

        var paths = root.GetProperty("paths").EnumerateObject().Select(p => p.Name).ToHashSet();
        Assert.Equal(["/api/files/generate", "/api/files/types", "/api/files/history"], paths);
    }
}
