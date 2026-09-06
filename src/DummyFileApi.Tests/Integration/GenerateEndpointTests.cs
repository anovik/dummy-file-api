using System.Net;
using System.Net.Http.Json;
using DummyFileApi.Models;

namespace DummyFileApi.Tests.Integration;

public class GenerateEndpointTests(IntegrationTestWebApplicationFactory factory)
    : IClassFixture<IntegrationTestWebApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private static HttpRequestMessage Request(string url) =>
        TestRequests.ForwardedFor(url, $"10.10.{Random.Shared.Next(256)}.{Random.Shared.Next(256)}");

    [Theory]
    [InlineData("txt", "text/plain", "txt")]
    [InlineData("csv", "text/csv", "csv")]
    [InlineData("png", "image/png", "png")]
    [InlineData("jpeg", "image/jpeg", "jpg")]
    [InlineData("pdf", "application/pdf", "pdf")]
    [InlineData("zip", "application/zip", "zip")]
    [InlineData("docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", "docx")]
    [InlineData("xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "xlsx")]
    [InlineData("json", "application/json", "json")]
    [InlineData("tar", "application/x-tar", "tar")]
    [InlineData("gzip", "application/gzip", "gz")]
    public async Task Generate_HappyPath_ReturnsExactBodyWithMatchingHeaders(string type, string expectedMimeType, string expectedExtension)
    {
        var response = await _client.SendAsync(Request($"/api/files/generate?type={type}&size=64KB"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expectedMimeType, response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(
            $"attachment; filename=\"dummy.{expectedExtension}\"",
            response.Content.Headers.ContentDisposition?.ToString());

        var body = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(65536, body.Length);
    }

    [Fact]
    public async Task Generate_UnknownType_ReturnsBadRequestWithValidTypesListed()
    {
        var response = await _client.SendAsync(Request("/api/files/generate?type=mp3&size=1KB"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Contains("Unsupported type", error!.Error);
    }

    [Fact]
    public async Task Generate_MalformedSize_ReturnsBadRequest()
    {
        var response = await _client.SendAsync(Request("/api/files/generate?type=txt&size=notasize"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Contains("Invalid size", error!.Error);
    }

    [Fact]
    public async Task Generate_SizeBelowGeneratorMinimum_ReturnsBadRequest()
    {
        var response = await _client.SendAsync(Request("/api/files/generate?type=pdf&size=1B"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Contains("must be at least", error!.Error);
    }

    [Fact]
    public async Task Generate_SizeAboveConfiguredMax_ReturnsBadRequest()
    {
        var response = await _client.SendAsync(Request("/api/files/generate?type=txt&size=200MB"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Contains("must not exceed", error!.Error);
    }
}
