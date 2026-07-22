using DummyFileApi.Generators;
using DummyFileApi.Models;
using DummyFileApi.Options;
using DummyFileApi.Validation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DummyFileApi.Controllers;

[ApiController]
[Route("api/files")]
public class FilesController(IServiceProvider serviceProvider, IOptions<FileGenerationOptions> options) : ControllerBase
{
    [HttpGet("generate")]
    public async Task<IActionResult> Generate([FromQuery] string? type, [FromQuery] string? size, [FromQuery] int? seed, CancellationToken cancellationToken)
    {
        if (!RequestValidation.TryValidateType(type, out var key, out var typeError))
        {
            return BadRequest(new ErrorResponse(typeError!));
        }

        if (!RequestValidation.TryValidateSize(size, out var targetSizeBytes, out var sizeError))
        {
            return BadRequest(new ErrorResponse(sizeError!));
        }

        var generator = serviceProvider.GetRequiredKeyedService<IFileGenerator>(key);

        if (!RequestValidation.TryValidateBounds(targetSizeBytes, generator, options.Value.MaxSizeBytes, out var boundsError))
        {
            return BadRequest(new ErrorResponse(boundsError!));
        }

        Response.ContentType = generator.MimeType;
        Response.Headers.ContentDisposition = $"attachment; filename=\"dummy.{generator.FileExtension}\"";
        Response.ContentLength = targetSizeBytes;

        await generator.GenerateAsync(Response.Body, targetSizeBytes, seed, cancellationToken);

        return new EmptyResult();
    }
}
