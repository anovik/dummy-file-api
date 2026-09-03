using System.Text;

namespace DummyFileApi.Generators;

/// <summary>
/// Seed-selected filler pangrams shared by the archive generators. All pure
/// ASCII with no XML-significant characters, so byte length equals character
/// length and the text drops into XML content without escaping.
/// </summary>
internal static class FillerPhrases
{
    public static readonly string[] All =
    [
        "The quick brown fox jumps over the lazy dog. ",
        "Pack my box with five dozen liquor jugs. ",
        "How vexingly quick daft zebras jump. ",
        "Sphinx of black quartz, judge my vow. ",
        "The five boxing wizards jump in quickly. ",
        "Jackdaws love my big sphinx of quartz. ",
        "Bright vixens jump; dozy fowl quack. ",
        "Quick zephyrs blow, vexing daft Jim. ",
    ];

    /// <summary>The same phrases as ASCII bytes, for generators that stream filler directly.</summary>
    public static readonly byte[][] AllBytes = All.Select(Encoding.ASCII.GetBytes).ToArray();
}
