using DummyFileApi.Options;
using DummyFileApi.Services;

namespace DummyFileApi.Tests.Services;

public class GenerationConcurrencyGuardTests
{
    private static GenerationConcurrencyGuard CreateGuard(int maxConcurrent, int retryAfterSeconds = 5) =>
        new(Microsoft.Extensions.Options.Options.Create(new FileGenerationOptions
        {
            MaxConcurrentGenerations = maxConcurrent,
            BusyRetryAfterSeconds = retryAfterSeconds,
        }));

    [Fact]
    public void TryAcquire_UpToTheConfiguredMax_Succeeds()
    {
        using var guard = CreateGuard(maxConcurrent: 3);

        for (var i = 0; i < 3; i++)
        {
            Assert.True(guard.TryAcquire());
        }
    }

    [Fact]
    public void TryAcquire_PastTheConfiguredMax_FailsWithoutBlocking()
    {
        using var guard = CreateGuard(maxConcurrent: 2);
        Assert.True(guard.TryAcquire());
        Assert.True(guard.TryAcquire());

        Assert.False(guard.TryAcquire());
    }

    [Fact]
    public void Release_FreesTheSlotForTheNextCaller()
    {
        using var guard = CreateGuard(maxConcurrent: 1);
        Assert.True(guard.TryAcquire());
        Assert.False(guard.TryAcquire());

        guard.Release();

        Assert.True(guard.TryAcquire());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void TryAcquire_WhenDisabled_AlwaysSucceeds(int maxConcurrent)
    {
        using var guard = CreateGuard(maxConcurrent);

        for (var i = 0; i < 50; i++)
        {
            Assert.True(guard.TryAcquire());
        }

        // Release stays a no-op rather than throwing on an unheld slot.
        guard.Release();
    }

    [Fact]
    public void RetryAfterSeconds_IsAtLeastOne_EvenWhenMisconfigured()
    {
        using var guard = CreateGuard(maxConcurrent: 1, retryAfterSeconds: 0);

        Assert.Equal(1, guard.RetryAfterSeconds);
    }
}
