using System.Globalization;
using System.Text.RegularExpressions;

namespace DummyFileApi.Sizes;

/// <summary>
/// Parses human-readable sizes like "100KB" or "2.5MB" using binary units
/// (KB = 1024 bytes, MB = 1024*1024 bytes) — KiB/MiB are accepted as aliases
/// for the same values, since "KB"/"MB" here deliberately mean binary, not SI.
/// </summary>
public static partial class SizeParser
{
    [GeneratedRegex(@"^\s*(?<value>\d+(\.\d+)?)\s*(?<unit>B|KB|KIB|MB|MIB)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex Pattern();

    public static bool TryParse(string? input, out long bytes)
    {
        bytes = 0;

        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var match = Pattern().Match(input);
        if (!match.Success)
        {
            return false;
        }

        if (!decimal.TryParse(match.Groups["value"].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
            || value <= 0)
        {
            return false;
        }

        var multiplier = match.Groups["unit"].Value.ToUpperInvariant() switch
        {
            "B" => 1m,
            "KB" or "KIB" => 1024m,
            "MB" or "MIB" => 1024m * 1024m,
            _ => 0m,
        };

        if (multiplier == 0m)
        {
            return false;
        }

        decimal rounded;
        try
        {
            // A large enough value can overflow decimal arithmetic here even
            // though it parsed fine on its own; decimal overflow throws, unlike double.
            rounded = Math.Round(value * multiplier, MidpointRounding.AwayFromZero);
        }
        catch (OverflowException)
        {
            return false;
        }

        if (rounded <= 0 || rounded > long.MaxValue)
        {
            return false;
        }

        bytes = (long)rounded;
        return true;
    }
}
