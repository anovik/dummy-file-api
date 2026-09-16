using System.Net;
using System.Net.Http.Json;
using DummyFileApi.Models;

namespace DummyFileApi.Tests.Integration;

public class RateLimitTests(LowRateLimitWebApplicationFactory factory) : IClassFixture<LowRateLimitWebApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private static HttpRequestMessage Request(string clientIp) =>
        TestRequests.ForwardedFor("/api/files/generate?type=txt&size=1KB", clientIp);

    private static string Header(HttpResponseMessage response, string name) =>
        Assert.Single(response.Headers.GetValues(name));

    [Fact]
    public async Task Generate_ExceedingConfiguredMaxPerHour_Returns429WithRetryAfter()
    {
        const string clientIp = "10.20.30.1";

        for (var i = 0; i < 3; i++)
        {
            var allowed = await _client.SendAsync(Request(clientIp));
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }

        var rejected = await _client.SendAsync(Request(clientIp));

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        var error = await rejected.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Contains("Rate limit exceeded", error!.Error);
        Assert.True(rejected.Headers.RetryAfter?.Delta > TimeSpan.Zero);
    }

    [Fact]
    public async Task Generate_ReportsTheWindowInRateLimitHeaders()
    {
        const string clientIp = "10.20.30.4";

        var first = await _client.SendAsync(Request(clientIp));
        var second = await _client.SendAsync(Request(clientIp));

        Assert.Equal("3", Header(first, "X-RateLimit-Limit"));
        Assert.Equal("2", Header(first, "X-RateLimit-Remaining"));
        Assert.Equal("1", Header(second, "X-RateLimit-Remaining"));
        Assert.True(long.Parse(Header(second, "X-RateLimit-Reset")) > DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task Generate_OverTheLimit_StillReportsRateLimitHeaders()
    {
        const string clientIp = "10.20.30.5";

        for (var i = 0; i < 3; i++)
        {
            await _client.SendAsync(Request(clientIp));
        }

        var rejected = await _client.SendAsync(Request(clientIp));

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal("3", Header(rejected, "X-RateLimit-Limit"));
        Assert.Equal("0", Header(rejected, "X-RateLimit-Remaining"));
        Assert.True(long.Parse(Header(rejected, "X-RateLimit-Reset")) > DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task Generate_DifferentClients_EachGetOwnLimit()
    {
        for (var i = 0; i < 3; i++)
        {
            var response = await _client.SendAsync(Request("10.20.30.2"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var otherClient = await _client.SendAsync(Request("10.20.30.3"));

        Assert.Equal(HttpStatusCode.OK, otherClient.StatusCode);
    }
}
