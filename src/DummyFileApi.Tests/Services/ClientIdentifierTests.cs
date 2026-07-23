using System.Net;
using DummyFileApi.Services;
using Microsoft.AspNetCore.Http;

namespace DummyFileApi.Tests.Services;

public class ClientIdentifierTests
{
    private static HttpContext CreateContext(IPAddress? remoteIp)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = remoteIp;
        return context;
    }

    [Fact]
    public void GetClientId_NoRemoteIp_ReturnsUnknown()
    {
        Assert.Equal(ClientIdentifier.Unknown, ClientIdentifier.GetClientId(CreateContext(null)));
    }

    [Fact]
    public void GetClientId_Ipv4_ReturnsDottedQuad()
    {
        Assert.Equal("10.1.2.3", ClientIdentifier.GetClientId(CreateContext(IPAddress.Parse("10.1.2.3"))));
    }

    [Fact]
    public void GetClientId_Ipv4MappedToIpv6_NormalizesToIpv4()
    {
        Assert.Equal("10.1.2.3", ClientIdentifier.GetClientId(CreateContext(IPAddress.Parse("::ffff:10.1.2.3"))));
    }

    [Fact]
    public void GetClientId_Ipv6_ReturnsIpv6String()
    {
        Assert.Equal("2001:db8::1", ClientIdentifier.GetClientId(CreateContext(IPAddress.Parse("2001:db8::1"))));
    }
}
