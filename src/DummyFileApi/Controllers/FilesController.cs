using DummyFileApi.Generators;
using DummyFileApi.Sizes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace DummyFileApi.Controllers;

[ApiController]
[Route("api/files")]
public class FilesController(IServiceProvider serviceProvider) : ControllerBase
{
    [HttpGet("generate")]
    public async Task<IActionResult> Generate([FromQuery] string? type, [FromQuery] string? size, [FromQuery] int? seed, CancellationToken cancellationToken)
    {
        var key = type?.Trim().ToLowerInvariant();
        if (key is null || FileGeneratorRegistry.All.All(g => g.Key != key))
        {
            return BadRequest(new { error = $"Unsupported type '{type}'." });
        }

        if (!SizeParser.TryParse(size, out var targetSizeBytes))
        {
            return BadRequest(new { error = $"Invalid size '{size}'. Expected a value like '100KB' or '1.5MB'." });
        }

        var generator = serviceProvider.GetRequiredKeyedService<IFileGenerator>(key);

        if (targetSizeBytes < generator.MinSizeBytes)
        {
            return BadRequest(new { error = $"Size must be at least {generator.MinSizeBytes} bytes for type '{key}'." });
        }

        Response.ContentType = generator.MimeType;
        Response.Headers.ContentDisposition = $"attachment; filename=\"dummy.{generator.FileExtension}\"";
        Response.ContentLength = targetSizeBytes;

        await generator.GenerateAsync(Response.Body, targetSizeBytes, seed, cancellationToken);

        return new EmptyResult();
    }
}
