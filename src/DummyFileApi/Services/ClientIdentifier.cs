namespace DummyFileApi.Services;

/// <summary>
/// Derives the client identity used for history and rate limiting. No auth in
/// v1, so identity is just the remote IP (as rewritten by the forwarded-headers
/// middleware when behind a proxy).
/// </summary>
public static class ClientIdentifier
{
    public const string Unknown = "unknown";

    public static string GetClientId(HttpContext httpContext)
    {
        var ip = httpContext.Connection.RemoteIpAddress;
        if (ip is null)
        {
            return Unknown;
        }

        // Normalize so the same client isn't counted twice across dual-stack sockets.
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        return ip.ToString();
    }
}
