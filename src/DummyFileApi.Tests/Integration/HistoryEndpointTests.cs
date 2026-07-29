using System.Net;
using System.Net.Http.Json;
using DummyFileApi.Models;

namespace DummyFileApi.Tests.Integration;

public class HistoryEndpointTests(IntegrationTestWebApplicationFactory factory) : IClassFixture<IntegrationTestWebApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private static HttpRequestMessage Request(string url, string clientIp) => TestRequests.ForwardedFor(url, clientIp);

    private async Task GenerateAsync(string clientIp, string type = "txt", string size = "1KB") =>
        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(Request($"/api/files/generate?type={type}&size={size}", clientIp))).StatusCode);

    [Fact]
    public async Task GetHistory_ReturnsOwnRequestsNewestFirst()
    {
        const string clientIp = "10.40.1.1";
        await GenerateAsync(clientIp, "txt", "1KB");
        // CreatedAtUtc ties break on a random Guid (FilesController.GetHistory), so
        // sequential rows need a real time gap or the asserted order below is flaky.
        await Task.Delay(20);
        await GenerateAsync(clientIp, "csv", "1KB");
        await Task.Delay(20);
        await GenerateAsync(clientIp, "png", "16KB");

        var response = await _client.SendAsync(Request("/api/files/history", clientIp));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<PagedHistoryResponse>();

        Assert.Equal(3, page!.TotalCount);
        Assert.Equal(new[] { "png", "csv", "txt" }, page.Items.Select(i => i.FileType));
    }

    [Fact]
    public async Task GetHistory_DoesNotReturnOtherClientsRequests()
    {
        const string ownIp = "10.40.2.1";
        const string otherIp = "10.40.2.2";
        await GenerateAsync(ownIp);
        await GenerateAsync(otherIp);
        await GenerateAsync(otherIp);

        var response = await _client.SendAsync(Request("/api/files/history", ownIp));
        var page = await response.Content.ReadFromJsonAsync<PagedHistoryResponse>();

        Assert.Equal(1, page!.TotalCount);
        Assert.Single(page.Items);
    }

    [Fact]
    public async Task GetHistory_PaginatesAndReportsTotalCount()
    {
        const string clientIp = "10.40.3.1";
        for (var i = 0; i < 5; i++)
        {
            await GenerateAsync(clientIp);
        }

        var response = await _client.SendAsync(Request("/api/files/history?page=2&pageSize=2", clientIp));
        var page = await response.Content.ReadFromJsonAsync<PagedHistoryResponse>();

        Assert.Equal(5, page!.TotalCount);
        Assert.Equal(2, page.Page);
        Assert.Equal(2, page.PageSize);
        Assert.Equal(2, page.Items.Count);
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(1, 0)]
    [InlineData(1, 101)]
    public async Task GetHistory_InvalidPagination_ReturnsBadRequest(int page, int pageSize)
    {
        var response = await _client.SendAsync(Request($"/api/files/history?page={page}&pageSize={pageSize}", "10.40.4.1"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotNull(await response.Content.ReadFromJsonAsync<ErrorResponse>());
    }
}
