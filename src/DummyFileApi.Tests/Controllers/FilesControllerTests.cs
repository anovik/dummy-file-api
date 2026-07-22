using DummyFileApi.Controllers;
using DummyFileApi.Generators;
using DummyFileApi.Models;
using DummyFileApi.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DummyFileApi.Tests.Controllers;

public class FilesControllerTests
{
    private static FilesController CreateController(long maxSizeBytes = 104_857_600)
    {
        var services = new ServiceCollection();
        foreach (var (key, impl) in FileGeneratorRegistry.All)
        {
            services.AddKeyedSingleton(typeof(IFileGenerator), key, impl);
        }

        var provider = services.BuildServiceProvider();
        var options = Microsoft.Extensions.Options.Options.Create(new FileGenerationOptions { MaxSizeBytes = maxSizeBytes });

        return new FilesController(provider, options);
    }

    private static (FilesController Controller, MemoryStream ResponseBody) CreateControllerWithHttpContext(long maxSizeBytes = 104_857_600)
    {
        var controller = CreateController(maxSizeBytes);
        var responseBody = new MemoryStream();
        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = responseBody;
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        return (controller, responseBody);
    }

    [Fact]
    public void GetTypes_ReturnsOneEntryPerRegisteredGenerator()
    {
        var controller = CreateController();

        var result = Assert.IsType<OkObjectResult>(controller.GetTypes());
        var types = Assert.IsAssignableFrom<IEnumerable<FileTypeDto>>(result.Value).ToList();

        Assert.Equal(FileGeneratorRegistry.All.Count, types.Count);
    }

    [Fact]
    public void GetTypes_TxtEntry_MatchesGeneratorMetadataAndConfiguredMax()
    {
        var controller = CreateController(maxSizeBytes: 555);

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
        var (controller, responseBody) = CreateControllerWithHttpContext();

        var result = await controller.Generate(type: "txt", size: "2KB", seed: null, cancellationToken: CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal(2048, responseBody.Length);
        Assert.Equal("text/plain", controller.Response.ContentType);
        Assert.Equal("attachment; filename=\"dummy.txt\"", controller.Response.Headers.ContentDisposition.ToString());
        Assert.Equal(2048, controller.Response.ContentLength);
    }

    [Fact]
    public async Task Generate_UnknownType_ReturnsBadRequestWithoutWritingBody()
    {
        var (controller, responseBody) = CreateControllerWithHttpContext();

        var result = await controller.Generate(type: "pdf", size: "1KB", seed: null, cancellationToken: CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var error = Assert.IsType<ErrorResponse>(badRequest.Value);
        Assert.Contains("Unsupported type", error.Error);
        Assert.Equal(0, responseBody.Length);
    }

    [Fact]
    public async Task Generate_InvalidSize_ReturnsBadRequestWithoutWritingBody()
    {
        var (controller, responseBody) = CreateControllerWithHttpContext();

        var result = await controller.Generate(type: "txt", size: "notasize", seed: null, cancellationToken: CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var error = Assert.IsType<ErrorResponse>(badRequest.Value);
        Assert.Contains("Invalid size", error.Error);
        Assert.Equal(0, responseBody.Length);
    }

    [Fact]
    public async Task Generate_SizeAboveConfiguredMax_ReturnsBadRequestWithoutWritingBody()
    {
        var (controller, responseBody) = CreateControllerWithHttpContext(maxSizeBytes: 1000);

        var result = await controller.Generate(type: "txt", size: "2KB", seed: null, cancellationToken: CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var error = Assert.IsType<ErrorResponse>(badRequest.Value);
        Assert.Contains("must not exceed", error.Error);
        Assert.Equal(0, responseBody.Length);
    }
}
