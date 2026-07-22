using DummyFileApi.Controllers;
using DummyFileApi.Generators;
using DummyFileApi.Models;
using DummyFileApi.Options;
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
}
