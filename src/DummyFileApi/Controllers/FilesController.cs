using System.Diagnostics;
using System.Globalization;
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
public class FilesController(
    IServiceProvider serviceProvider,
    IOptions<FileGenerationOptions> options,
    AppDbContext dbContext,
    GenerationRateLimiter rateLimiter) : ControllerBase
{
    /// <summary>Streams a generated dummy file of the exact requested byte size.</summary>
    /// <param name="type">One of the types returned by <c>GET /api/files/types</c> (e.g. <c>txt</c>, <c>csv</c>, <c>pdf</c>, <c>jpeg</c>, <c>png</c>, <c>zip</c>, <c>docx</c>, <c>xlsx</c>, <c>json</c>, <c>tar</c>, <c>gzip</c>, <c>svg</c>, <c>wav</c>).</param>
    /// <param name="size">Human-readable size, binary units (e.g. <c>100KB</c> = 102,400 bytes, <c>1MB</c> = 1,048,576 bytes). <c>KiB</c>/<c>MiB</c> are accepted aliases.</param>
    /// <param name="seed">
    /// Optional, meaning varies by type:
    /// <c>png</c>/<c>jpeg</c>/<c>pdf</c>/<c>svg</c> use it to pick the checkerboard fill color from a fixed palette
    /// (omit for the first palette color); <c>zip</c>/<c>docx</c>/<c>tar</c>/<c>gzip</c> use it to pick the filler phrase from a
    /// fixed set (omit for the first), which changes the bytes but not the size (a <c>docx</c> large enough to embed
    /// an image also uses it for the image's color); <c>wav</c> uses it to pick the
    /// sample waveform from a fixed set (omit for silence), which likewise changes the audio but not the size;
    /// <c>csv</c>, <c>xlsx</c> and <c>json</c> use it as the starting row/record Id, counting up from there (omit,
    /// or pass a non-positive value, for 1; also falls back to 1 if the requested size is too small to fit even
    /// one row at that Id); <c>txt</c> ignores it entirely.
    /// </param>
    /// <param name="cancellationToken">Cancellation token for the request.</param>
    /// <response code="200">The generated file as an attachment, served with the requested type's MIME type.</response>
    /// <response code="400">Unsupported type, unparseable size, or size outside the type's min/max bounds.</response>
    /// <response code="429">Rate limit exceeded; the <c>Retry-After</c> header gives the seconds to wait.</response>
    [HttpGet("generate")]
    [ProducesResponseType<FileResult>(StatusCodes.Status200OK, "application/octet-stream")]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status400BadRequest, "application/json")]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status429TooManyRequests, "application/json")]
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

        var clientId = ClientIdentifier.GetClientId(HttpContext);
        var rateLimitResult = await rateLimiter.CheckAsync(clientId, cancellationToken);
        if (!rateLimitResult.IsAllowed)
        {
            Response.Headers.RetryAfter = rateLimitResult.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
            return StatusCode(StatusCodes.Status429TooManyRequests, new ErrorResponse(
                $"Rate limit exceeded: max {rateLimitResult.Limit} requests per hour. Retry after {rateLimitResult.RetryAfterSeconds} seconds."));
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
            ClientId = clientId,
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

    /// <summary>Lists supported file types with MIME type, extension, and min/max allowed size.</summary>
    /// <response code="200">One entry per supported type.</response>
    [HttpGet("types")]
    [ProducesResponseType<IReadOnlyList<FileTypeDto>>(StatusCodes.Status200OK, "application/json")]
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

    /// <summary>Paged history of past generation requests for the calling client (identified by IP), newest first.</summary>
    /// <param name="page">1-based page number (default 1).</param>
    /// <param name="pageSize">Items per page, 1 to 100 (default 20).</param>
    /// <param name="cancellationToken">Cancellation token for the request.</param>
    /// <response code="200">One page of history items plus paging metadata.</response>
    /// <response code="400"><c>page</c> or <c>pageSize</c> out of range.</response>
    [HttpGet("history")]
    [ProducesResponseType<PagedHistoryResponse>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status400BadRequest, "application/json")]
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
