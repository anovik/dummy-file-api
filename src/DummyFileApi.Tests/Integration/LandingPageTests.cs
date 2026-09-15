using System.Net.Http.Json;
using System.Net;

namespace DummyFileApi.Tests.Integration;

public class LandingPageTests(IntegrationTestWebApplicationFactory factory) : IClassFixture<IntegrationTestWebApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Root_ServesTheLandingPage()
    {
        var response = await _client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);

        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("<form id=\"generate-form\"", html);
        Assert.Contains("app.js", html);
        Assert.Contains("href=\"/swagger\"", html);
    }

    [Theory]
    [InlineData("/app.js")]
    [InlineData("/styles.css")]
    public async Task PageAssets_AreServed(string path)
    {
        var response = await _client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Content.Headers.ContentLength > 0);
    }

    // The page builds its format list from this endpoint rather than a
    // hardcoded copy, so a drift here would empty the dropdown.
    [Fact]
    public async Task TypesEndpoint_ServesTheFieldsThePageReads()
    {
        var response = await _client.GetAsync("/api/files/types");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var types = await response.Content.ReadFromJsonAsync<List<Dictionary<string, object>>>();
        Assert.NotEmpty(types!);
        Assert.All(types!, t =>
        {
            foreach (var field in new[] { "type", "mimeType", "extension", "minSizeBytes", "maxSizeBytes" })
            {
                Assert.True(t.ContainsKey(field), $"types entry is missing '{field}'");
            }
        });
    }
}
