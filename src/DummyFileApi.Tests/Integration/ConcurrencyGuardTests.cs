using System.Net;
using System.Net.Http.Json;
using DummyFileApi.Models;
using DummyFileApi.Services;
using Microsoft.Extensions.DependencyInjection;

namespace DummyFileApi.Tests.Integration;

public class ConcurrencyGuardTests(SingleSlotWebApplicationFactory factory) : IClassFixture<SingleSlotWebApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    // The guard is a singleton, so holding its only slot from the test is the
    // same as a generation being in flight — without racing two real requests.
    private readonly GenerationConcurrencyGuard _guard = factory.Services.GetRequiredService<GenerationConcurrencyGuard>();

    private static HttpRequestMessage Request(string clientIp) =>
        TestRequests.ForwardedFor("/api/files/generate?type=txt&size=1KB", clientIp);

    [Fact]
    public async Task Generate_WithNoSlotFree_Returns503WithRetryAfter()
    {
        Assert.True(_guard.TryAcquire());
        try
        {
            var response = await _client.SendAsync(Request("10.40.30.1"));

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
            Assert.Contains("Too many generations in flight", error!.Error);
            Assert.True(response.Headers.RetryAfter?.Delta > TimeSpan.Zero);
        }
        finally
        {
            _guard.Release();
        }
    }

    [Fact]
    public async Task Generate_WithNoSlotFree_DoesNotCountTowardTheClientsRateLimit()
    {
        const string clientIp = "10.40.30.2";

        Assert.True(_guard.TryAcquire());
        try
        {
            var shed = await _client.SendAsync(Request(clientIp));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, shed.StatusCode);
        }
        finally
        {
            _guard.Release();
        }

        var allowed = await _client.SendAsync(Request(clientIp));

        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal("99", Assert.Single(allowed.Headers.GetValues("X-RateLimit-Remaining")));
    }

    [Fact]
    public async Task Generate_OnceTheSlotIsReleased_SucceedsAgain()
    {
        var first = await _client.SendAsync(Request("10.40.30.3"));
        var second = await _client.SendAsync(Request("10.40.30.3"));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
    }
}
