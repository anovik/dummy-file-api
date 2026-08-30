namespace DummyFileApi.Generators;

internal static class SeedIndex
{
    /// <summary>
    /// Maps an optional seed onto an index in [0, count). A null seed picks 0;
    /// any other value wraps with a negative-safe modulo (including int.MinValue).
    /// </summary>
    public static int Wrap(int? seed, int count) =>
        seed is int s ? ((s % count) + count) % count : 0;
}
