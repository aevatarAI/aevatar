using System.Security.Claims;
using System.Text.Json.Serialization;
using Aevatar.AI.Abstractions;
using Aevatar.Capabilities;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Identity;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Schedules;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgentService.Hosting.Endpoints;

public static class ScopeWorkflowScheduleEndpoints
{
    internal const string WorkflowScheduleServiceEndpointId = "chat";
    internal const string DefaultWorkflowScheduleNyxIdScope = "proxy";

    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/{scopeId}/workflows/{workflowId}/schedules", List)
            .WithTags("ScopeWorkflows")
            .Produces<ScheduledDispatchListResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);
        group.MapPost("/{scopeId}/workflows/{workflowId}/schedules/preview", Preview)
            .WithTags("ScopeWorkflows")
            .Produces<ScheduledDispatchPreview>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);
        group.MapPost("/{scopeId}/workflows/{workflowId}/schedules", Create)
            .WithTags("ScopeWorkflows")
            .Produces<ScheduledDispatchMutationReceipt>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);
        group.MapGet("/{scopeId}/workflows/{workflowId}/schedules/{scheduleId}", Get)
            .WithTags("ScopeWorkflows")
            .Produces<ScheduledDispatchDetail>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);
        group.MapPut("/{scopeId}/workflows/{workflowId}/schedules/{scheduleId}", Update)
            .WithTags("ScopeWorkflows")
            .Produces<ScheduledDispatchMutationReceipt>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);
        group.MapPost("/{scopeId}/workflows/{workflowId}/schedules/{scheduleId}:enable", Enable)
            .WithTags("ScopeWorkflows")
            .Produces<ScheduledDispatchMutationReceipt>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);
        group.MapPost("/{scopeId}/workflows/{workflowId}/schedules/{scheduleId}:disable", Disable)
            .WithTags("ScopeWorkflows")
            .Produces<ScheduledDispatchMutationReceipt>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);
        group.MapPost("/{scopeId}/workflows/{workflowId}/schedules/{scheduleId}:run-now", RunNow)
            .WithTags("ScopeWorkflows")
            .Produces<ScheduledDispatchRunNowReceipt>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);
        group.MapDelete("/{scopeId}/workflows/{workflowId}/schedules/{scheduleId}", Delete)
            .WithTags("ScopeWorkflows")
            .Produces<ScheduledDispatchMutationReceipt>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);
    }

    internal static async Task<IResult> Create(
        HttpContext http,
        string scopeId,
        string workflowId,
        WorkflowScheduleConfigurationHttpRequest request,
        [FromServices] IScopeWorkflowQueryPort workflowQueryPort,
        [FromServices] IScheduledDispatchApplicationService schedules,
        CancellationToken ct = default)
    {
        try
        {
            var resolved = await ResolveWorkflowAsync(http, scopeId, workflowId, workflowQueryPort, ct);
            if (resolved.Error != null)
                return resolved.Error;

            var workflow = resolved.Workflow!;
            var context = ResolveMutationContext(http, scopeId);
            var configuration = await request.ToConfigurationAsync(
                workflow,
                NormalizeRequired(request.ScheduleId, nameof(request.ScheduleId)),
                context.AuthenticatedNyxIdOwnerSubject,
                ct);
            var receipt = await schedules.CreateAsync(configuration, context, ct);
            return Results.Accepted(BuildScheduleLocation(scopeId, workflowId, receipt.ScheduleId), receipt);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new
            {
                code = "INVALID_USER_WORKFLOW_REQUEST",
                message = ex.Message,
            });
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    internal static async Task<IResult> Preview(
        HttpContext http,
        string scopeId,
        string workflowId,
        WorkflowSchedulePreviewHttpRequest request,
        [FromServices] IScopeWorkflowQueryPort workflowQueryPort,
        [FromServices] IScheduledDispatchApplicationService schedules,
        CancellationToken ct = default)
    {
        var resolved = await ResolveWorkflowAsync(http, scopeId, workflowId, workflowQueryPort, ct);
        if (resolved.Error != null)
            return resolved.Error;

        try
        {
            return Results.Ok(await schedules.PreviewAsync(
                request.CronExpression,
                request.Timezone,
                request.Count <= 0 ? 5 : request.Count,
                request.FromUtc,
                ct));
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    internal static async Task<IResult> List(
        HttpContext http,
        string scopeId,
        string workflowId,
        [FromServices] IScopeWorkflowQueryPort workflowQueryPort,
        [FromServices] IScheduledDispatchApplicationService schedules,
        int take = 50,
        string? cursor = null,
        bool includeTotalCount = false,
        CancellationToken ct = default)
    {
        var resolved = await ResolveWorkflowAsync(http, scopeId, workflowId, workflowQueryPort, ct);
        if (resolved.Error != null)
            return resolved.Error;

        var workflow = resolved.Workflow!;
        var query = new ScheduledDispatchListQuery(
            Take: take,
            Cursor: cursor,
            IncludeTotalCount: includeTotalCount,
            TargetKind: ScheduledDispatchTargetKind.ServiceInvocation,
            ServiceEndpointId: WorkflowScheduleServiceEndpointId,
            ScheduleKind: ScheduledDispatchScheduleKind.Workflow,
            ExcludeTeamOwned: true,
            ServiceKey: NormalizeOptional(workflow.ServiceKey),
            ServiceId: NormalizeOptional(workflow.PublishedServiceId));

        return Results.Ok(await schedules.ListAsync(query, ct));
    }

    internal static async Task<IResult> Get(
        HttpContext http,
        string scopeId,
        string workflowId,
        string scheduleId,
        [FromServices] IScopeWorkflowQueryPort workflowQueryPort,
        [FromServices] IScheduledDispatchApplicationService schedules,
        CancellationToken ct = default)
    {
        var resolved = await ResolveWorkflowAsync(http, scopeId, workflowId, workflowQueryPort, ct);
        if (resolved.Error != null)
            return resolved.Error;

        var detail = await schedules.GetAsync(scheduleId, ct);
        if (!BelongsToWorkflow(detail, resolved.Workflow!))
            return WorkflowScheduleNotFound(scopeId, workflowId, scheduleId);

        return Results.Ok(detail);
    }

    internal static async Task<IResult> Update(
        HttpContext http,
        string scopeId,
        string workflowId,
        string scheduleId,
        WorkflowScheduleConfigurationHttpRequest request,
        [FromServices] IScopeWorkflowQueryPort workflowQueryPort,
        [FromServices] IScheduledDispatchApplicationService schedules,
        CancellationToken ct = default)
    {
        try
        {
            var resolved = await ResolveWorkflowAsync(http, scopeId, workflowId, workflowQueryPort, ct);
            if (resolved.Error != null)
                return resolved.Error;

            var workflow = resolved.Workflow!;
            var detail = await schedules.GetAsync(scheduleId, ct);
            if (!BelongsToWorkflow(detail, workflow))
                return WorkflowScheduleNotFound(scopeId, workflowId, scheduleId);

            var context = ResolveMutationContext(http, scopeId);
            var configuration = await request.ToConfigurationAsync(
                workflow,
                scheduleId,
                context.AuthenticatedNyxIdOwnerSubject,
                ct);
            var receipt = await schedules.UpdateAsync(scheduleId, configuration, context, ct);
            return Results.Accepted(BuildScheduleLocation(scopeId, workflowId, receipt.ScheduleId), receipt);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new
            {
                code = "INVALID_USER_WORKFLOW_REQUEST",
                message = ex.Message,
            });
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    internal static async Task<IResult> Enable(
        HttpContext http,
        string scopeId,
        string workflowId,
        string scheduleId,
        [FromServices] IScopeWorkflowQueryPort workflowQueryPort,
        [FromServices] IScheduledDispatchApplicationService schedules,
        CancellationToken ct = default)
    {
        var resolved = await ResolveWorkflowAsync(http, scopeId, workflowId, workflowQueryPort, ct);
        if (resolved.Error != null)
            return resolved.Error;

        if (!await EnsureWorkflowScheduleOwnershipAsync(scheduleId, resolved.Workflow!, schedules, ct))
            return WorkflowScheduleNotFound(scopeId, workflowId, scheduleId);

        var receipt = await schedules.EnableAsync(scheduleId, string.Empty, ct);
        return Results.Accepted(BuildScheduleLocation(scopeId, workflowId, receipt.ScheduleId), receipt);
    }

    internal static async Task<IResult> Disable(
        HttpContext http,
        string scopeId,
        string workflowId,
        string scheduleId,
        [FromServices] IScopeWorkflowQueryPort workflowQueryPort,
        [FromServices] IScheduledDispatchApplicationService schedules,
        CancellationToken ct = default)
    {
        var resolved = await ResolveWorkflowAsync(http, scopeId, workflowId, workflowQueryPort, ct);
        if (resolved.Error != null)
            return resolved.Error;

        if (!await EnsureWorkflowScheduleOwnershipAsync(scheduleId, resolved.Workflow!, schedules, ct))
            return WorkflowScheduleNotFound(scopeId, workflowId, scheduleId);

        var receipt = await schedules.DisableAsync(scheduleId, string.Empty, ct);
        return Results.Accepted(BuildScheduleLocation(scopeId, workflowId, receipt.ScheduleId), receipt);
    }

    internal static async Task<IResult> Delete(
        HttpContext http,
        string scopeId,
        string workflowId,
        string scheduleId,
        [FromServices] IScopeWorkflowQueryPort workflowQueryPort,
        [FromServices] IScheduledDispatchApplicationService schedules,
        CancellationToken ct = default)
    {
        var resolved = await ResolveWorkflowAsync(http, scopeId, workflowId, workflowQueryPort, ct);
        if (resolved.Error != null)
            return resolved.Error;

        if (!await EnsureWorkflowScheduleOwnershipAsync(scheduleId, resolved.Workflow!, schedules, ct))
            return WorkflowScheduleNotFound(scopeId, workflowId, scheduleId);

        var receipt = await schedules.DeleteAsync(scheduleId, string.Empty, ct);
        return Results.Accepted(BuildScheduleLocation(scopeId, workflowId, receipt.ScheduleId), receipt);
    }

    internal static async Task<IResult> RunNow(
        HttpContext http,
        string scopeId,
        string workflowId,
        string scheduleId,
        [FromServices] IScopeWorkflowQueryPort workflowQueryPort,
        [FromServices] IScheduledDispatchApplicationService schedules,
        CancellationToken ct = default)
    {
        var resolved = await ResolveWorkflowAsync(http, scopeId, workflowId, workflowQueryPort, ct);
        if (resolved.Error != null)
            return resolved.Error;

        if (!await EnsureWorkflowScheduleOwnershipAsync(scheduleId, resolved.Workflow!, schedules, ct))
            return WorkflowScheduleNotFound(scopeId, workflowId, scheduleId);

        var receipt = await schedules.RunNowAsync(scheduleId, ct);
        return Results.Accepted(BuildScheduleLocation(scopeId, workflowId, receipt.ScheduleId), receipt);
    }

    private static async Task<(ScopeWorkflowSummary? Workflow, IResult? Error)> ResolveWorkflowAsync(
        HttpContext http,
        string scopeId,
        string workflowId,
        IScopeWorkflowQueryPort workflowQueryPort,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(workflowQueryPort);

        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return (null, denied);

        var lookup = await workflowQueryPort.LookupByWorkflowIdAsync(scopeId, workflowId, ct);
        if (!lookup.IsRunnable)
        {
            var (statusCode, code, message) = ScopeWorkflowEndpoints.MapWorkflowLookupError(scopeId, workflowId, lookup);
            return (null, Results.Json(new { code, message }, statusCode: statusCode));
        }

        return (lookup.Workflow, null);
    }

    private static async Task<bool> EnsureWorkflowScheduleOwnershipAsync(
        string scheduleId,
        ScopeWorkflowSummary workflow,
        IScheduledDispatchApplicationService schedules,
        CancellationToken ct)
    {
        var detail = await schedules.GetAsync(scheduleId, ct);
        return BelongsToWorkflow(detail, workflow);
    }

    private static bool BelongsToWorkflow(ScheduledDispatchDetail? detail, ScopeWorkflowSummary workflow)
    {
        if (detail == null)
            return false;

        var schedule = detail.Schedule;
        if (schedule.ScheduleKind != ScheduledDispatchScheduleKind.Workflow)
            return false;
        if (schedule.TargetKind != ScheduledDispatchTargetKind.ServiceInvocation)
            return false;
        if (!string.Equals(schedule.ServiceEndpointId, ScopeWorkflowScheduleEndpoints.WorkflowScheduleServiceEndpointId, StringComparison.Ordinal))
            return false;
        if (!string.Equals(schedule.ServiceId, ScopeWorkflowScheduleEndpoints.NormalizeRequired(workflow.PublishedServiceId, nameof(workflow.PublishedServiceId)), StringComparison.Ordinal))
            return false;

        var routeServiceKey = NormalizeOptional(workflow.ServiceKey);
        if (routeServiceKey != null && !string.Equals(schedule.ServiceKey, routeServiceKey, StringComparison.Ordinal))
            return false;

        return true;
    }

    private static ScheduledDispatchMutationContext ResolveMutationContext(HttpContext http, string scopeId)
    {
        ArgumentNullException.ThrowIfNull(http);
        return new ScheduledDispatchMutationContext(
            scopeId,
            ResolveAuthenticatedNyxIdOwnerSubject(http));
    }

    private static ScheduledServiceInvocationNyxIdSubjectRef? ResolveAuthenticatedNyxIdOwnerSubject(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);

        var ownerUserId = ReadFirstClaim(
            http.User,
            "uid",
            "sub",
            ClaimTypes.NameIdentifier,
            "user_id");
        if (string.IsNullOrWhiteSpace(ownerUserId))
            return null;

        return new ScheduledServiceInvocationNyxIdSubjectRef(
            OwnerScope.NyxIdPlatform,
            string.Empty,
            ownerUserId.Trim());
    }

    private static string? ReadFirstClaim(ClaimsPrincipal? user, params string[] claimTypes)
    {
        if (user == null)
            return null;

        foreach (var claimType in claimTypes)
        {
            var value = user.FindFirst(claimType)?.Value?.Trim();
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private static IResult WorkflowScheduleNotFound(string scopeId, string workflowId, string scheduleId) =>
        Results.Json(new
        {
            code = "WORKFLOW_SCHEDULE_NOT_FOUND",
            message = $"Schedule '{scheduleId}' was not found for workflow '{workflowId}' in scope '{scopeId}'.",
        }, statusCode: StatusCodes.Status404NotFound);

    private static string BuildScheduleLocation(string scopeId, string workflowId, string scheduleId) =>
        $"/api/scopes/{scopeId}/workflows/{workflowId}/schedules/{scheduleId}";

    internal static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    internal static string NormalizeRequired(string? value, string paramName)
    {
        var normalized = NormalizeOptional(value);
        if (normalized == null)
            throw new InvalidOperationException($"{paramName} is required.");

        return normalized;
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkflowScheduleConfigurationHttpRequest
{
    public string? ScheduleId { get; init; }
    public string? DisplayName { get; init; }
    public required string CronExpression { get; init; }
    public string? Timezone { get; init; }
    public bool Enabled { get; init; } = true;
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
    public string? Prompt { get; init; }

    public Task<ScheduledDispatchConfiguration> ToConfigurationAsync(
        ScopeWorkflowSummary workflow,
        string scheduleId,
        ScheduledServiceInvocationNyxIdSubjectRef? authenticatedOwnerSubject,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        var normalizedScheduleId = ScopeWorkflowScheduleEndpoints.NormalizeRequired(scheduleId, nameof(scheduleId));
        var serviceIdentity = new ServiceIdentity
        {
            TenantId = ScopeWorkflowScheduleEndpoints.NormalizeRequired(workflow.ScopeId, nameof(workflow.ScopeId)),
            AppId = ScopeWorkflowScheduleEndpoints.NormalizeRequired(workflow.ServiceAppId, nameof(workflow.ServiceAppId)),
            Namespace = ScopeWorkflowScheduleEndpoints.NormalizeRequired(workflow.ServiceNamespace, nameof(workflow.ServiceNamespace)),
            ServiceId = ScopeWorkflowScheduleEndpoints.NormalizeRequired(workflow.PublishedServiceId, nameof(workflow.PublishedServiceId)),
        };

        var headers = Headers == null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(Headers, StringComparer.Ordinal);

        var chatRequest = new ChatRequestEvent
        {
            Prompt = ScopeWorkflowScheduleEndpoints.NormalizeOptional(Prompt) ?? string.Empty,
            ScopeId = ScopeWorkflowScheduleEndpoints.NormalizeRequired(workflow.ScopeId, nameof(workflow.ScopeId)),
        };
        foreach (var (key, value) in headers)
        {
            chatRequest.Headers[key] = value;
            chatRequest.Metadata[key] = value;
        }

        var target = new ScheduledDispatchTargetDescriptor(
            ScheduledDispatchTargetKind.ServiceInvocation,
            ServiceInvocation: new ScheduledServiceInvocationTargetDescriptor(
                serviceIdentity,
                ScopeWorkflowScheduleEndpoints.WorkflowScheduleServiceEndpointId,
                Any.Pack(chatRequest),
                ScopeWorkflowScheduleEndpoints.NormalizeRequired(workflow.ActiveRevisionId, nameof(workflow.ActiveRevisionId)),
                Auth: BuildWorkflowScheduleAuth(authenticatedOwnerSubject)));

        return Task.FromResult(new ScheduledDispatchConfiguration(
            normalizedScheduleId,
            ScopeWorkflowScheduleEndpoints.NormalizeOptional(DisplayName) ?? string.Empty,
            target,
            ScopeWorkflowScheduleEndpoints.NormalizeRequired(CronExpression, nameof(CronExpression)),
            ScopeWorkflowScheduleEndpoints.NormalizeOptional(Timezone) ?? string.Empty,
            Enabled,
            headers,
            ScheduledDispatchScheduleKind.Workflow)
        {
            CredentialRequirementTargetKind = ScheduledDispatchCredentialRequirementTargetKind.WorkflowService,
        });
    }

    private static ScheduledServiceInvocationAuth BuildWorkflowScheduleAuth(
        ScheduledServiceInvocationNyxIdSubjectRef? authenticatedOwnerSubject)
    {
        if (authenticatedOwnerSubject == null)
            throw new InvalidOperationException("Authenticated NyxID owner subject is required for workflow schedule auth.");

        return new ScheduledServiceInvocationAuth(
            new ScheduledServiceInvocationNyxIdCredentialSource(
                authenticatedOwnerSubject,
                ScopeWorkflowScheduleEndpoints.DefaultWorkflowScheduleNyxIdScope,
                ScheduledServiceInvocationNyxIdCredentialRole.ScopeOwner));
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkflowSchedulePreviewHttpRequest
{
    public required string CronExpression { get; init; }
    public string? Timezone { get; init; }
    public int Count { get; init; } = 5;
    public DateTimeOffset? FromUtc { get; init; }
}
