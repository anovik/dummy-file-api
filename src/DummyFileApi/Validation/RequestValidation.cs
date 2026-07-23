using DummyFileApi.Generators;
using DummyFileApi.Sizes;

namespace DummyFileApi.Validation;

/// <summary>
/// Manual guard clauses for the /generate request. Kept as plain static
/// checks rather than a validation library — two inputs (type, size) with
/// simple, non-composable rules don't justify that weight.
/// </summary>
public static class RequestValidation
{
    public static bool TryValidateType(string? type, out string key, out string? error)
    {
        var normalizedKey = type?.Trim().ToLowerInvariant() ?? string.Empty;
        key = normalizedKey;

        if (type is null || FileGeneratorRegistry.All.All(g => g.Key != normalizedKey))
        {
            var validTypes = string.Join(", ", FileGeneratorRegistry.All.Select(g => g.Key));
            error = $"Unsupported type '{type}'. Valid types: {validTypes}.";
            return false;
        }

        error = null;
        return true;
    }

    public static bool TryValidateSize(string? size, out long bytes, out string? error)
    {
        if (!SizeParser.TryParse(size, out bytes))
        {
            error = $"Invalid size '{size}'. Expected a value like '100KB' or '1.5MB'.";
            return false;
        }

        error = null;
        return true;
    }

    public const int MaxPageSize = 100;

    public static bool TryValidatePagination(int page, int pageSize, out string? error)
    {
        if (page < 1)
        {
            error = "page must be at least 1.";
            return false;
        }

        if (pageSize < 1 || pageSize > MaxPageSize)
        {
            error = $"pageSize must be between 1 and {MaxPageSize}.";
            return false;
        }

        error = null;
        return true;
    }

    public static bool TryValidateBounds(long requestedBytes, IFileGenerator generator, long maxSizeBytes, out string? error)
    {
        if (requestedBytes < generator.MinSizeBytes)
        {
            error = $"Size must be at least {generator.MinSizeBytes} bytes for type '{generator.TypeKey}'.";
            return false;
        }

        if (requestedBytes > maxSizeBytes)
        {
            error = $"Size must not exceed {maxSizeBytes} bytes.";
            return false;
        }

        error = null;
        return true;
    }
}
