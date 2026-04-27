using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Application.Studio.Services;
using Aevatar.Studio.Infrastructure.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Aevatar.Studio.Hosting.Controllers;

[ApiController]
[Route("api/connectors")]
public sealed class ConnectorsController : ControllerBase
{
    private readonly ConnectorService _connectorService;

    public ConnectorsController(ConnectorService connectorService)
    {
        _connectorService = connectorService;
    }

    [HttpGet]
    public async Task<ActionResult<ConnectorCatalogResponse>> Get(CancellationToken cancellationToken)
    {
        try
        {
            var response = await _connectorService.GetCatalogAsync(cancellationToken);
            ETagSupport.WriteETag(Response, response.Version);
            return Ok(response);
        }
        catch (ChronoStorageServiceException exception)
        {
            return ChronoStorageErrorResponses.ToActionResult(exception);
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { message = exception.Message });
        }
    }

    [HttpGet("draft")]
    public async Task<ActionResult<ConnectorDraftResponse>> GetDraft(CancellationToken cancellationToken)
    {
        try
        {
            var response = await _connectorService.GetDraftAsync(cancellationToken);
            ETagSupport.WriteETag(Response, response.Version);
            return Ok(response);
        }
        catch (ChronoStorageServiceException exception)
        {
            return ChronoStorageErrorResponses.ToActionResult(exception);
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { message = exception.Message });
        }
    }

    [HttpPut]
    public async Task<ActionResult<ConnectorCatalogResponse>> Save(
        [FromBody] SaveConnectorCatalogRequest request,
        CancellationToken cancellationToken)
    {
        if (TryApplyIfMatch(request, out var effectiveRequest, out var malformed))
            return malformed!;
        try
        {
            var response = await _connectorService.SaveCatalogAsync(effectiveRequest, cancellationToken);
            EmitETagIfDeterministic(response.Version);
            return Ok(response);
        }
        catch (EventStoreOptimisticConcurrencyException exception)
        {
            return Conflict(new { code = "VERSION_CONFLICT", message = exception.Message });
        }
        catch (ChronoStorageServiceException exception)
        {
            return ChronoStorageErrorResponses.ToActionResult(exception);
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { message = exception.Message });
        }
    }

    [HttpPost("import")]
    public async Task<ActionResult<ImportConnectorCatalogResponse>> Import(
        [FromForm] IFormFile? file,
        CancellationToken cancellationToken)
    {
        if (file is null || file.Length <= 0)
        {
            return BadRequest(new { message = "Select a connector catalog JSON file to import." });
        }

        try
        {
            await using var stream = file.OpenReadStream();
            return Ok(await _connectorService.ImportCatalogAsync(file.FileName, stream, cancellationToken));
        }
        catch (ChronoStorageServiceException exception)
        {
            return ChronoStorageErrorResponses.ToActionResult(exception);
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
    }

    [HttpPut("draft")]
    public async Task<ActionResult<ConnectorDraftResponse>> SaveDraft(
        [FromBody] SaveConnectorDraftRequest request,
        CancellationToken cancellationToken)
    {
        if (TryApplyIfMatch(request, out var effectiveRequest, out var malformed))
            return malformed!;
        try
        {
            var response = await _connectorService.SaveDraftAsync(effectiveRequest, cancellationToken);
            EmitETagIfDeterministic(response.Version);
            return Ok(response);
        }
        catch (EventStoreOptimisticConcurrencyException exception)
        {
            return Conflict(new { code = "VERSION_CONFLICT", message = exception.Message });
        }
        catch (ChronoStorageServiceException exception)
        {
            return ChronoStorageErrorResponses.ToActionResult(exception);
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { message = exception.Message });
        }
    }

    [HttpDelete("draft")]
    public async Task<IActionResult> DeleteDraft(CancellationToken cancellationToken)
    {
        var status = ETagSupport.ParseIfMatch(Request, out var ifMatchVersion);
        if (status == IfMatchStatus.Invalid)
            return MalformedIfMatch();

        long? expectedVersion = status == IfMatchStatus.Valid ? ifMatchVersion : null;
        try
        {
            await _connectorService.DeleteDraftAsync(expectedVersion, cancellationToken);
            return NoContent();
        }
        catch (EventStoreOptimisticConcurrencyException exception)
        {
            return Conflict(new { code = "VERSION_CONFLICT", message = exception.Message });
        }
        catch (ChronoStorageServiceException exception)
        {
            return ChronoStorageErrorResponses.ToActionResult(exception);
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { message = exception.Message });
        }
    }

    private bool TryApplyIfMatch(SaveConnectorCatalogRequest request, out SaveConnectorCatalogRequest effective, out ActionResult? malformed)
    {
        var status = ETagSupport.ParseIfMatch(Request, out var headerVersion);
        if (status == IfMatchStatus.Invalid)
        {
            effective = request;
            malformed = MalformedIfMatch();
            return true;
        }

        if (status == IfMatchStatus.Valid)
        {
            if (request.ExpectedVersion is { } bodyVersion && bodyVersion != headerVersion)
            {
                effective = request;
                malformed = IfMatchBodyMismatch(headerVersion, bodyVersion);
                return true;
            }

            effective = request with { ExpectedVersion = headerVersion };
            malformed = null;
            return false;
        }

        effective = request;
        malformed = null;
        return false;
    }

    private bool TryApplyIfMatch(SaveConnectorDraftRequest request, out SaveConnectorDraftRequest effective, out ActionResult? malformed)
    {
        var status = ETagSupport.ParseIfMatch(Request, out var headerVersion);
        if (status == IfMatchStatus.Invalid)
        {
            effective = request;
            malformed = MalformedIfMatch();
            return true;
        }

        if (status == IfMatchStatus.Valid)
        {
            if (request.ExpectedVersion is { } bodyVersion && bodyVersion != headerVersion)
            {
                effective = request;
                malformed = IfMatchBodyMismatch(headerVersion, bodyVersion);
                return true;
            }

            effective = request with { ExpectedVersion = headerVersion };
            malformed = null;
            return false;
        }

        effective = request;
        malformed = null;
        return false;
    }

    private void EmitETagIfDeterministic(long version)
    {
        if (version > 0)
            ETagSupport.WriteETag(Response, version);
    }

    private BadRequestObjectResult MalformedIfMatch() =>
        BadRequest(new
        {
            code = "MALFORMED_IF_MATCH",
            message = "If-Match must be a strong validator with a single non-negative integer version (e.g. \"5\").",
        });

    private BadRequestObjectResult IfMatchBodyMismatch(long headerVersion, long bodyVersion) =>
        BadRequest(new
        {
            code = "IF_MATCH_BODY_MISMATCH",
            message = $"If-Match header (\"{headerVersion}\") disagrees with body expectedVersion ({bodyVersion}). Send only one, or set them to the same value.",
        });
}
