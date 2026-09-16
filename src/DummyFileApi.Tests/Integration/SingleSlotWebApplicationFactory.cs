namespace DummyFileApi.Tests.Integration;

/// <summary>Same as the base factory but with a single generation slot, so tests can saturate the guard.</summary>
public class SingleSlotWebApplicationFactory : IntegrationTestWebApplicationFactory
{
    protected override int MaxConcurrentGenerations => 1;
}
