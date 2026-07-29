namespace DummyFileApi.Tests.Integration;

/// <summary>Shared helper for simulating a distinct client IP via the forwarded-headers path the factory enables.</summary>
internal static class TestRequests
{
    public static HttpRequestMessage ForwardedFor(string url, string clientIp) => new(HttpMethod.Get, url)
    {
        Headers = { { "X-Forwarded-For", clientIp } },
    };
}
