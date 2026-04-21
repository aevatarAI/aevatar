using Aevatar.Studio.Application;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Application.Studio.Services;
using Aevatar.Studio.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Aevatar.Studio.Hosting.Controllers;

[ApiController]
[Route("api/workspace")]
public sealed class WorkspaceController : ControllerBase
{
    private readonly WorkspaceService _workspaceService;
    private readonly AppScopedWorkflowService _scopeWorkflowService;
    private readonly IAppScopeResolver _scopeResolver;
    private readonly StudioHostingOptions _hostingOptions;

    public WorkspaceController(
        WorkspaceService workspaceService,
        AppScopedWorkflowService scopeWorkflowService,
        IAppScopeResolver scopeResolver,
        IOptions<StudioHostingOptions> hostingOptions)
    {
        _workspaceService = workspaceService;
        _scopeWorkflowService = scopeWorkflowService;
        _scopeResolver = scopeResolver;
        _hostingOptions = hostingOptions?.Value ?? throw new ArgumentNullException(nameof(hostingOptions));
    }

    [HttpGet]
    public async Task<ActionResult<WorkspaceSettingsResponse>> GetSettings(
        [FromQuery] string? scopeId,
        CancellationToken cancellationToken)
    {
        var settings = await _workspaceService.GetSettingsAsync(cancellationToken);
        var scopeResolution = await ResolveScopeContextAsync(scopeId);
        if (scopeResolution.Failure != null)
            return scopeResolution.Failure;

        var scopeContext = scopeResolution.Context;
        if (scopeContext == null)
            return Ok(settings);

        var scopeDirectory = AppScopedWorkflowService.CreateScopeDirectory(scopeContext.ScopeId);
        return Ok(settings with { Directories = [scopeDirectory] });
    }

    [HttpPut("settings")]
    public async Task<ActionResult<WorkspaceSettingsResponse>> UpdateSettings(
        [FromBody] UpdateWorkspaceSettingsRequest request,
        CancellationToken cancellationToken) =>
        Ok(await _workspaceService.UpdateSettingsAsync(request, cancellationToken));

    [HttpPost("directories")]
    public async Task<ActionResult<WorkspaceSettingsResponse>> AddDirectory(
        [FromBody] AddWorkflowDirectoryRequest request,
        [FromQuery] string? scopeId,
        CancellationToken cancellationToken)
    {
        var scopeResolution = await ResolveScopeContextAsync(scopeId);
        if (scopeResolution.Failure != null)
            return scopeResolution.Failure;

        if (scopeResolution.Context != null)
            return BadRequest(new { message = "Workflow directories are unavailable when workflows are scoped to the current login." });

        try
        {
            return Ok(await _workspaceService.AddDirectoryAsync(request, cancellationToken));
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return BadRequest(new { message = exception.Message });
        }
    }

    [HttpDelete("directories/{directoryId}")]
    public async Task<ActionResult<WorkspaceSettingsResponse>> RemoveDirectory(
        string directoryId,
        [FromQuery] string? scopeId,
        CancellationToken cancellationToken)
    {
        var scopeResolution = await ResolveScopeContextAsync(scopeId);
        if (scopeResolution.Failure != null)
            return scopeResolution.Failure;

        if (scopeResolution.Context != null)
            return BadRequest(new { message = "Workflow directories are unavailable when workflows are scoped to the current login." });

        return Ok(await _workspaceService.RemoveDirectoryAsync(directoryId, cancellationToken));
    }

    [HttpGet("workflow-drafts")]
    public async Task<ActionResult<IReadOnlyList<WorkflowDraftSummary>>> ListDrafts(
        [FromQuery] string? scopeId,
        CancellationToken cancellationToken)
    {
        var scopeResolution = await ResolveScopeContextAsync(scopeId);
        if (scopeResolution.Failure != null)
            return scopeResolution.Failure;

        var scopeContext = scopeResolution.Context;
        if (scopeContext != null)
        {
            try
            {
                return Ok(await _scopeWorkflowService.ListDraftsAsync(scopeContext.ScopeId, cancellationToken));
            }
            catch (AppApiException exception)
            {
                return StatusCode(exception.StatusCode, AppApiErrors.CreatePayload(exception));
            }
            catch (InvalidOperationException exception)
            {
                return BadRequest(new { message = exception.Message });
            }
        }

        return Ok(await _workspaceService.ListDraftsAsync(cancellationToken));
    }

    [HttpGet("workflow-drafts/{workflowId}")]
    public async Task<ActionResult<WorkflowDraftResponse>> GetDraft(
        string workflowId,
        [FromQuery] string? scopeId,
        CancellationToken cancellationToken)
    {
        var scopeResolution = await ResolveScopeContextAsync(scopeId);
        if (scopeResolution.Failure != null)
            return scopeResolution.Failure;

        var scopeContext = scopeResolution.Context;
        WorkflowDraftResponse? workflow;
        if (scopeContext != null)
        {
            try
            {
                workflow = await _scopeWorkflowService.GetDraftAsync(scopeContext.ScopeId, workflowId, cancellationToken);
            }
            catch (AppApiException exception)
            {
                return StatusCode(exception.StatusCode, AppApiErrors.CreatePayload(exception));
            }
            catch (InvalidOperationException exception)
            {
                return BadRequest(new { message = exception.Message });
            }
        }
        else
        {
            workflow = await _workspaceService.GetDraftAsync(workflowId, cancellationToken);
        }

        return workflow is null ? NotFound() : Ok(workflow);
    }

    [HttpPost("workflow-drafts")]
    public async Task<ActionResult<WorkflowDraftResponse>> CreateDraft(
        [FromBody] SaveWorkflowDraftRequest request,
        [FromQuery] string? scopeId,
        CancellationToken cancellationToken)
    {
        var scopeResolution = await ResolveScopeContextAsync(scopeId);
        if (scopeResolution.Failure != null)
            return scopeResolution.Failure;

        var scopeContext = scopeResolution.Context;
        if (scopeContext != null)
        {
            try
            {
                return Ok(await _scopeWorkflowService.CreateDraftAsync(scopeContext.ScopeId, request, cancellationToken));
            }
            catch (AppApiException exception)
            {
                return StatusCode(exception.StatusCode, AppApiErrors.CreatePayload(exception));
            }
            catch (WorkflowDraftPathConflictException exception)
            {
                return Conflict(CreateDraftPathConflictPayload(exception));
            }
            catch (InvalidOperationException exception)
            {
                return BadRequest(new { message = exception.Message });
            }
        }

        try
        {
            return Ok(await _workspaceService.CreateDraftAsync(request, cancellationToken));
        }
        catch (WorkflowDraftPathConflictException exception)
        {
            return Conflict(CreateDraftPathConflictPayload(exception));
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
    }

    [HttpPut("workflow-drafts/{workflowId}")]
    public async Task<ActionResult<WorkflowDraftResponse>> UpdateDraft(
        string workflowId,
        [FromBody] SaveWorkflowDraftRequest request,
        [FromQuery] string? scopeId,
        CancellationToken cancellationToken)
    {
        var scopeResolution = await ResolveScopeContextAsync(scopeId);
        if (scopeResolution.Failure != null)
            return scopeResolution.Failure;

        var scopeContext = scopeResolution.Context;
        try
        {
            if (scopeContext != null)
            {
                return Ok(await _scopeWorkflowService.UpdateDraftAsync(
                    scopeContext.ScopeId,
                    workflowId,
                    request,
                    cancellationToken));
            }

            return Ok(await _workspaceService.UpdateDraftAsync(workflowId, request, cancellationToken));
        }
        catch (AppApiException exception)
        {
            return StatusCode(exception.StatusCode, AppApiErrors.CreatePayload(exception));
        }
        catch (WorkflowDraftNotFoundException)
        {
            return NotFound();
        }
        catch (WorkflowDraftPathConflictException exception)
        {
            return Conflict(CreateDraftPathConflictPayload(exception));
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
    }

    private async Task<(AppScopeContext? Context, ActionResult? Failure)> ResolveScopeContextAsync(string? requestedScopeId)
    {
        var ambientScopeContext = _scopeResolver.Resolve(HttpContext);
        var normalizedRequestedScopeId = requestedScopeId?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedRequestedScopeId))
            return (ambientScopeContext, null);

        if (ambientScopeContext != null)
        {
            if (string.Equals(
                    ambientScopeContext.ScopeId,
                    normalizedRequestedScopeId,
                    StringComparison.Ordinal))
            {
                return (ambientScopeContext, null);
            }

            return (null, StatusCode(StatusCodes.Status403Forbidden, new
            {
                message = "Requested scope does not match the authenticated Studio scope.",
            }));
        }

        if (!_hostingOptions.AllowUnauthenticatedScopeQueryFallback)
        {
            return (null, Unauthorized(new
            {
                message = "Studio authentication is required before accessing a scoped workflow workspace.",
            }));
        }

        return (new AppScopeContext(normalizedRequestedScopeId, "query:scopeId"), null);
    }

    [HttpDelete("workflow-drafts/{workflowId}")]
    public async Task<IActionResult> DeleteDraft(
        string workflowId,
        [FromQuery] string? scopeId,
        CancellationToken cancellationToken)
    {
        try
        {
            var scopeResolution = await ResolveScopeContextAsync(scopeId);
            if (scopeResolution.Failure != null)
                return scopeResolution.Failure;

            var scopeContext = scopeResolution.Context;
            if (scopeContext != null)
            {
                await _scopeWorkflowService.DeleteDraftAsync(scopeContext.ScopeId, workflowId, cancellationToken);
            }
            else
            {
                await _workspaceService.DeleteDraftAsync(workflowId, cancellationToken);
            }

            return NoContent();
        }
        catch (AppApiException exception)
        {
            return StatusCode(exception.StatusCode, AppApiErrors.CreatePayload(exception));
        }
        catch (WorkflowDraftNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
    }

    private static object CreateDraftPathConflictPayload(WorkflowDraftPathConflictException exception) => new
    {
        code = "WORKFLOW_DRAFT_PATH_CONFLICT",
        message = exception.Message,
    };
}
