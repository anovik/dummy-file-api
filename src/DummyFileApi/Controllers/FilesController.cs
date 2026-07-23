using System.Diagnostics;
using DummyFileApi.Data;
using DummyFileApi.Generators;
using DummyFileApi.Models;
using DummyFileApi.Options;
using DummyFileApi.Services;
using DummyFileApi.Validation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DummyFileApi.Controllers;

[ApiController]
[Route("api/files")]
public class FilesController(IServiceProvider serviceProvider, IOptions<FileGenerationOptions> options, AppDbContext dbContext) : ControllerBase
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

        var stopwatch = Stopwatch.StartNew();
        var countingBody = new CountingStream(Response.Body);
        await generator.GenerateAsync(countingBody, targetSizeBytes, seed, cancellationToken);
        stopwatch.Stop();

        dbContext.GenerationRequests.Add(new GenerationRequest
        {
            Id = Guid.NewGuid(),
            ClientId = ClientIdentifier.GetClientId(HttpContext),
            FileType = generator.TypeKey,
            RequestedSizeBytes = targetSizeBytes,
            ActualSizeBytes = countingBody.BytesWritten,
            CreatedAtUtc = DateTime.UtcNow,
            DurationMs = (int)stopwatch.ElapsedMilliseconds,
        });

        // The file already streamed in full; don't let a client disconnect
        // cancel recording the completed generation.
        await dbContext.SaveChangesAsync(CancellationToken.None);

        return new EmptyResult();
    }

    [HttpGet("types")]
    public IActionResult GetTypes()
    {
        var maxSizeBytes = options.Value.MaxSizeBytes;

        var types = FileGeneratorRegistry.All
            .Select(g => serviceProvider.GetRequiredKeyedService<IFileGenerator>(g.Key))
            .Select(generator => new FileTypeDto(
                generator.TypeKey,
                generator.MimeType,
                generator.FileExtension,
                generator.MinSizeBytes,
                maxSizeBytes))
            .ToList();

        return Ok(types);
    }

    [HttpGet("history")]
    public async Task<IActionResult> GetHistory([FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken cancellationToken = default)
    {
        if (!RequestValidation.TryValidatePagination(page, pageSize, out var paginationError))
        {
            return BadRequest(new ErrorResponse(paginationError!));
        }

        var clientId = ClientIdentifier.GetClientId(HttpContext);
        var query = dbContext.GenerationRequests.Where(r => r.ClientId == clientId);

        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(r => r.CreatedAtUtc)
            .ThenByDescending(r => r.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(r => new GenerationHistoryItemDto(
                r.Id,
                r.FileType,
                r.RequestedSizeBytes,
                r.ActualSizeBytes,
                r.CreatedAtUtc,
                r.DurationMs))
            .ToListAsync(cancellationToken);

        return Ok(new PagedHistoryResponse(items, page, pageSize, totalCount));
    }
}
