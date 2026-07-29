namespace DummyFileApi.Tests.Integration;

/// <summary>Same as the base factory but with a tiny per-hour limit, so tests can actually trigger 429.</summary>
public class LowRateLimitWebApplicationFactory : IntegrationTestWebApplicationFactory
{
    protected override int MaxPerHour => 3;
}
