using System.Net;
using DummyFileApi.Controllers;
using DummyFileApi.Data;
using DummyFileApi.Generators;
using DummyFileApi.Models;
using DummyFileApi.Options;
using DummyFileApi.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DummyFileApi.Tests.Controllers;

public class FilesControllerTests
{
    private static AppDbContext CreateInMemoryDb()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var db = new AppDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }

    private static (FilesController Controller, AppDbContext Db) CreateController(long maxSizeBytes = 104_857_600, int maxPerHour = 100)
    {
        var services = new ServiceCollection();
        foreach (var (key, impl) in FileGeneratorRegistry.All)
        {
            services.AddKeyedSingleton(typeof(IFileGenerator), key, impl);
        }

        var provider = services.BuildServiceProvider();
        var options = Microsoft.Extensions.Options.Options.Create(new FileGenerationOptions { MaxSizeBytes = maxSizeBytes });
        var rateLimitingOptions = Microsoft.Extensions.Options.Options.Create(new RateLimitingOptions { MaxPerHour = maxPerHour });
        var db = CreateInMemoryDb();
        var rateLimiter = new GenerationRateLimiter(db, rateLimitingOptions);

        return (new FilesController(provider, options, db, rateLimiter), db);
    }

    private static (FilesController Controller, AppDbContext Db, MemoryStream ResponseBody) CreateControllerWithHttpContext(
        long maxSizeBytes = 104_857_600, string clientIp = "127.0.0.1", int maxPerHour = 100)
    {
        var (controller, db) = CreateController(maxSizeBytes, maxPerHour);
        var responseBody = new MemoryStream();
        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = responseBody;
        httpContext.Connection.RemoteIpAddress = IPAddress.Parse(clientIp);
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        return (controller, db, responseBody);
    }

    private static GenerationRequest CreateRow(string clientId, DateTime createdAtUtc, string fileType = "txt", long sizeBytes = 1024) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = clientId,
        FileType = fileType,
        RequestedSizeBytes = sizeBytes,
        ActualSizeBytes = sizeBytes,
        CreatedAtUtc = createdAtUtc,
        DurationMs = 5,
    };

    [Fact]
    public void GetTypes_ReturnsOneEntryPerRegisteredGenerator()
    {
        var (controller, _) = CreateController();

        var result = Assert.IsType<OkObjectResult>(controller.GetTypes());
        var types = Assert.IsAssignableFrom<IEnumerable<FileTypeDto>>(result.Value).ToList();

        Assert.Equal(FileGeneratorRegistry.All.Count, types.Count);
    }

    [Fact]
    public void GetTypes_TxtEntry_MatchesGeneratorMetadataAndConfiguredMax()
    {
        var (controller, _) = CreateController(maxSizeBytes: 555);

        var result = Assert.IsType<OkObjectResult>(controller.GetTypes());
        var types = Assert.IsAssignableFrom<IEnumerable<FileTypeDto>>(result.Value).ToList();

        var txt = Assert.Single(types, t => t.Type == "txt");
        Assert.Equal("text/plain", txt.MimeType);
        Assert.Equal("txt", txt.Extension);
        Assert.Equal(1, txt.MinSizeBytes);
        Assert.Equal(555, txt.MaxSizeBytes);
    }

    [Fact]
    public async Task Generate_ValidRequest_StreamsExactByteCountAndSetsHeaders()
    {
        var (controller, _, responseBody) = CreateControllerWithHttpContext();

        var result = await controller.Generate(type: "txt", size: "2KB", seed: null, cancellationToken: CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal(2048, responseBody.Length);
        Assert.Equal("text/plain", controller.Response.ContentType);
        Assert.Equal("attachment; filename=\"dummy.txt\"", controller.Response.Headers.ContentDisposition.ToString());
        Assert.Equal(2048, controller.Response.ContentLength);
    }

    [Fact]
    public async Task Generate_ValidRequest_RecordsGenerationRequestRow()
    {
        var (controller, db, _) = CreateControllerWithHttpContext(clientIp: "10.1.2.3");
        var before = DateTime.UtcNow;

        await controller.Generate(type: "txt", size: "2KB", seed: null, cancellationToken: CancellationToken.None);

        var row = Assert.Single(db.GenerationRequests.ToList());
        Assert.Equal("10.1.2.3", row.ClientId);
        Assert.Equal("txt", row.FileType);
        Assert.Equal(2048, row.RequestedSizeBytes);
        Assert.Equal(2048, row.ActualSizeBytes);
        Assert.InRange(row.CreatedAtUtc, before, DateTime.UtcNow);
        Assert.True(row.DurationMs >= 0);
    }

    [Fact]
    public async Task Generate_UnknownType_ReturnsBadRequestWithoutWritingBodyOrRow()
    {
        var (controller, db, responseBody) = CreateControllerWithHttpContext();

        var result = await controller.Generate(type: "docx", size: "1KB", seed: null, cancellationToken: CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var error = Assert.IsType<ErrorResponse>(badRequest.Value);
        Assert.Contains("Unsupported type", error.Error);
        Assert.Equal(0, responseBody.Length);
        Assert.Empty(db.GenerationRequests.ToList());
    }

    [Fact]
    public async Task Generate_InvalidSize_ReturnsBadRequestWithoutWritingBody()
    {
        var (controller, _, responseBody) = CreateControllerWithHttpContext();

        var result = await controller.Generate(type: "txt", size: "notasize", seed: null, cancellationToken: CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var error = Assert.IsType<ErrorResponse>(badRequest.Value);
        Assert.Contains("Invalid size", error.Error);
        Assert.Equal(0, responseBody.Length);
    }

    [Fact]
    public async Task Generate_SizeAboveConfiguredMax_ReturnsBadRequestWithoutWritingBody()
    {
        var (controller, _, responseBody) = CreateControllerWithHttpContext(maxSizeBytes: 1000);

        var result = await controller.Generate(type: "txt", size: "2KB", seed: null, cancellationToken: CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var error = Assert.IsType<ErrorResponse>(badRequest.Value);
        Assert.Contains("must not exceed", error.Error);
        Assert.Equal(0, responseBody.Length);
    }

    [Fact]
    public async Task Generate_RateLimitExceeded_Returns429WithRetryAfterAndDoesNotWriteBodyOrRow()
    {
        var (controller, db, responseBody) = CreateControllerWithHttpContext(clientIp: "10.5.5.5", maxPerHour: 1);
        db.GenerationRequests.Add(CreateRow("10.5.5.5", DateTime.UtcNow.AddMinutes(-1)));
        await db.SaveChangesAsync();

        var result = await controller.Generate(type: "txt", size: "1KB", seed: null, cancellationToken: CancellationToken.None);

        var tooManyRequests = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status429TooManyRequests, tooManyRequests.StatusCode);
        var error = Assert.IsType<ErrorResponse>(tooManyRequests.Value);
        Assert.Contains("Rate limit exceeded", error.Error);
        Assert.True(int.Parse(controller.Response.Headers.RetryAfter.ToString()) > 0);
        Assert.Equal(0, responseBody.Length);
        Assert.Single(db.GenerationRequests.ToList());
    }

    [Fact]
    public async Task Generate_UnderRateLimit_Succeeds()
    {
        var (controller, db, responseBody) = CreateControllerWithHttpContext(clientIp: "10.5.5.6", maxPerHour: 2);
        db.GenerationRequests.Add(CreateRow("10.5.5.6", DateTime.UtcNow.AddMinutes(-1)));
        await db.SaveChangesAsync();

        var result = await controller.Generate(type: "txt", size: "1KB", seed: null, cancellationToken: CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal(1024, responseBody.Length);
        Assert.Equal(2, db.GenerationRequests.ToList().Count);
    }

    [Fact]
    public async Task Generate_PriorRequestsOutsideWindow_DoNotCountTowardLimit()
    {
        var (controller, db, responseBody) = CreateControllerWithHttpContext(clientIp: "10.5.5.7", maxPerHour: 1);
        db.GenerationRequests.Add(CreateRow("10.5.5.7", DateTime.UtcNow.AddHours(-2)));
        await db.SaveChangesAsync();

        var result = await controller.Generate(type: "txt", size: "1KB", seed: null, cancellationToken: CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal(1024, responseBody.Length);
    }

    [Fact]
    public async Task GetHistory_ReturnsOnlyCallingClientsRowsNewestFirst()
    {
        var (controller, db, _) = CreateControllerWithHttpContext(clientIp: "10.1.2.3");
        var baseTime = new DateTime(2026, 7, 23, 12, 0, 0, DateTimeKind.Utc);
        db.GenerationRequests.AddRange(
            CreateRow("10.1.2.3", baseTime.AddMinutes(1)),
            CreateRow("10.1.2.3", baseTime.AddMinutes(3)),
            CreateRow("10.1.2.3", baseTime.AddMinutes(2)),
            CreateRow("99.9.9.9", baseTime.AddMinutes(4)));
        await db.SaveChangesAsync();

        var result = Assert.IsType<OkObjectResult>(await controller.GetHistory());
        var response = Assert.IsType<PagedHistoryResponse>(result.Value);

        Assert.Equal(3, response.TotalCount);
        Assert.Equal(3, response.Items.Count);
        Assert.Equal(
            new[] { baseTime.AddMinutes(3), baseTime.AddMinutes(2), baseTime.AddMinutes(1) },
            response.Items.Select(i => i.CreatedAtUtc));
    }

    [Fact]
    public async Task GetHistory_PaginatesAndReportsTotalCount()
    {
        var (controller, db, _) = CreateControllerWithHttpContext(clientIp: "10.1.2.3");
        var baseTime = new DateTime(2026, 7, 23, 12, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 5; i++)
        {
            db.GenerationRequests.Add(CreateRow("10.1.2.3", baseTime.AddMinutes(i)));
        }
        await db.SaveChangesAsync();

        var result = Assert.IsType<OkObjectResult>(await controller.GetHistory(page: 2, pageSize: 2));
        var response = Assert.IsType<PagedHistoryResponse>(result.Value);

        Assert.Equal(5, response.TotalCount);
        Assert.Equal(2, response.Page);
        Assert.Equal(2, response.PageSize);
        Assert.Equal(
            new[] { baseTime.AddMinutes(2), baseTime.AddMinutes(1) },
            response.Items.Select(i => i.CreatedAtUtc));
    }

    [Fact]
    public async Task GetHistory_PageBeyondData_ReturnsEmptyItemsWithTotalCount()
    {
        var (controller, db, _) = CreateControllerWithHttpContext(clientIp: "10.1.2.3");
        db.GenerationRequests.Add(CreateRow("10.1.2.3", DateTime.UtcNow));
        await db.SaveChangesAsync();

        var result = Assert.IsType<OkObjectResult>(await controller.GetHistory(page: 5, pageSize: 20));
        var response = Assert.IsType<PagedHistoryResponse>(result.Value);

        Assert.Empty(response.Items);
        Assert.Equal(1, response.TotalCount);
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(1, 0)]
    [InlineData(1, 101)]
    public async Task GetHistory_InvalidPagination_ReturnsBadRequest(int page, int pageSize)
    {
        var (controller, _, _) = CreateControllerWithHttpContext();

        var result = await controller.GetHistory(page, pageSize);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.IsType<ErrorResponse>(badRequest.Value);
    }
}
