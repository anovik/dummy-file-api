using System.Net;
using System.Net.Http.Json;
using DummyFileApi.Generators;
using DummyFileApi.Models;

namespace DummyFileApi.Tests.Integration;

public class TypesEndpointTests(IntegrationTestWebApplicationFactory factory) : IClassFixture<IntegrationTestWebApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task GetTypes_ReturnsOneEntryPerRegisteredGeneratorWithConfiguredMax()
    {
        var response = await _client.GetAsync("/api/files/types");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var types = await response.Content.ReadFromJsonAsync<List<FileTypeDto>>();

        Assert.Equal(FileGeneratorRegistry.All.Count, types!.Count);
        Assert.Equal(FileGeneratorRegistry.All.Select(g => g.Key).ToHashSet(), types.Select(t => t.Type).ToHashSet());
        Assert.All(types, t =>
        {
            Assert.True(t.MinSizeBytes > 0);
            Assert.Equal(104_857_600, t.MaxSizeBytes);
        });
    }
}
