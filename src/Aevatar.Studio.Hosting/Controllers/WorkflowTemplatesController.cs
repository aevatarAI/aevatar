using Aevatar.Studio.Application;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Services;
using Aevatar.Studio.Application.WorkflowTemplates;
using Aevatar.Studio.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Aevatar.Studio.Hosting.Controllers;

[ApiController]
[Route("api/workflow-templates")]
public sealed class WorkflowTemplatesController : ControllerBase
{
    private readonly PublicWorkflowTemplateService _templates;
    private readonly IAppScopeResolver _scopeResolver;

    public WorkflowTemplatesController(
        PublicWorkflowTemplateService templates,
        IAppScopeResolver scopeResolver)
    {
        _templates = templates ?? throw new ArgumentNullException(nameof(templates));
        _scopeResolver = scopeResolver ?? throw new ArgumentNullException(nameof(scopeResolver));
    }

    [HttpGet]
    public async Task<ActionResult<PublicWorkflowTemplateListResponse>> List(
        [FromQuery] string? query,
        [FromQuery] string? sort,
        [FromQuery] string? cursor,
        [FromQuery] int? take,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _templates.ListAsync(
                new PublicWorkflowTemplateListRequest(query, sort, cursor, take),
                cancellationToken));
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
    }

    [HttpGet("{templateId}")]
    public async Task<ActionResult<PublicWorkflowTemplateDetailResponse>> Get(
        string templateId,
        CancellationToken cancellationToken)
    {
        try
        {
            var detail = await _templates.GetAsync(templateId, cancellationToken);
            return detail is null ? NotFound() : Ok(detail);
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
    }

    [HttpPost("/api/scopes/{scopeId}/workflow-templates/{templateId}:instantiate")]
    public async Task<ActionResult> Instantiate(
        string scopeId,
        string templateId,
        [FromBody] WorkflowTemplateInstantiateRequest? request,
        CancellationToken cancellationToken)
    {
        if (request == null)
            return BadRequest(new { message = "Request body is required." });

        var scopeResolution = ResolveMutationScopeContext(scopeId);
        if (scopeResolution.Failure != null)
            return scopeResolution.Failure;

        try
        {
            return Accepted(await _templates.InstantiateAsync(
                scopeResolution.Context!.ScopeId,
                templateId,
                request,
                cancellationToken));
        }
        catch (WorkflowTemplateNotFoundException)
        {
            return NotFound();
        }
        catch (WorkflowTemplateVersionConflictException exception)
        {
            return Conflict(new
            {
                code = "WORKFLOW_TEMPLATE_VERSION_CONFLICT",
                message = exception.Message,
                templateId = exception.TemplateId,
                expectedAuthorityStateVersion = exception.ExpectedAuthorityStateVersion,
                actualAuthorityStateVersion = exception.ActualAuthorityStateVersion,
            });
        }
        catch (WorkflowDraftPathConflictException exception)
        {
            return Conflict(new
            {
                code = "WORKFLOW_DRAFT_PATH_CONFLICT",
                message = exception.Message,
            });
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
    }

    private (AppScopeContext? Context, ActionResult? Failure) ResolveMutationScopeContext(string requestedScopeId)
    {
        var normalizedRequestedScopeId = requestedScopeId?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedRequestedScopeId))
        {
            return (null, BadRequest(new
            {
                message = "scopeId is required.",
            }));
        }

        var ambientScopeContext = _scopeResolver.Resolve(HttpContext);
        if (ambientScopeContext == null)
        {
            return (null, Unauthorized(new
            {
                message = "Studio authentication is required before instantiating a scoped workflow template.",
            }));
        }

        if (!string.Equals(ambientScopeContext.ScopeId, normalizedRequestedScopeId, StringComparison.Ordinal))
        {
            return (null, StatusCode(StatusCodes.Status403Forbidden, new
            {
                message = "Requested scope does not match the authenticated Studio scope.",
            }));
        }

        return (ambientScopeContext, null);
    }
}
