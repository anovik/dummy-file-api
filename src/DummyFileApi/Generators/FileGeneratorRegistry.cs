namespace DummyFileApi.Generators;

/// <summary>
/// Single source of truth for which file types exist: both DI registration (Program.cs)
/// and the /api/files/types discovery endpoint iterate this list, so they can't drift.
/// </summary>
public static class FileGeneratorRegistry
{
    public static readonly IReadOnlyList<(string Key, Type Impl)> All =
    [
        ("txt", typeof(TxtFileGenerator)),
    ];
}
