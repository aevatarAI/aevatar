using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Aevatar.AI.Abstractions;
using Aevatar.Capabilities;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Hosting.Endpoints.Schedules;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Aevatar.GAgentService.Hosting.Endpoints;

internal static class ScopeWorkflowScheduleEndpoints
{
    private const string ChatEndpointId = "chat";
    private const string DefaultWorkflowScheduleNyxIdScope = "proxy";

    public static RouteGroupBuilder MapScopeWorkflowScheduleEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/{scopeId}/workflows/{workflowId}/external-trigger", UpsertExternalTrigger)
            .Produces<WorkflowExternalTriggerHttpResult>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);
        group.MapGet("/{scopeId}/workflows/{workflowId}/external-trigger", GetExternalTrigger)
            .Produces<WorkflowExternalTriggerHttpResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);
        group.MapPost("/{scopeId}/workflow-triggers/{triggerId}:fire", FireExternalTrigger)
            .Produces<WorkflowExternalTriggerFireHttpResult>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);
        group.MapGet("/{scopeId}/workflows/{workflowId}/schedules", List)
            .Produces<ScheduledDispatchListResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);
        group.MapPost("/{scopeId}/workflows/{workflowId}/schedules/preview", Preview)
            .Produces<ScheduledDispatchPreview>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);
        group.MapPost("/{scopeId}/workflows/{workflowId}/schedules", Create)
            .Produces<ScheduledDispatchMutationReceipt>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);
        group.MapGet("/{scopeId}/workflows/{workflowId}/schedules/{scheduleId}", Get)
            .Produces<ScheduledDispatchDetail>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);
        group.MapPut("/{scopeId}/workflows/{workflowId}/schedules/{scheduleId}", Update)
            .Produces<ScheduledDispatchMutationReceipt>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);
        group.MapPost("/{scopeId}/workflows/{workflowId}/schedules/{scheduleId}:enable", Enable)
            .Produces<ScheduledDispatchMutationReceipt>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);
        group.MapPost("/{scopeId}/workflows/{workflowId}/schedules/{scheduleId}:disable", Disable)
            .Produces<ScheduledDispatchMutationReceipt>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);
        group.MapPost("/{scopeId}/workflows/{workflowId}/schedules/{scheduleId}:run-now", RunNow)
            .Produces<ScheduledDispatchRunNowReceipt>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);
        group.MapDelete("/{scopeId}/workflows/{workflowId}/schedules/{scheduleId}", Delete)
            .Produces<ScheduledDispatchMutationReceipt>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);
        return group;
    }

    internal static async Task<IResult> UpsertExternalTrigger(
        HttpContext http,
        string scopeId,
        string workflowId,
        WorkflowExternalTriggerConfigurationHttpRequest input,
        [FromServices] IScopeWorkflowQueryPort workflowQueryPort,
        [FromServices] IScheduledDispatchApplicationService schedules,
        CancellationToken ct = default)
    {
        var resolved = await ResolveWorkflowAsync(http, scopeId, workflowId, workflowQueryPort, ct);
        if (resolved.Result != null)
            return resolved.Result;

        ScheduledDispatchConfiguration configuration;
        ScheduledDispatchMutationContext context;
        try
        {
            context = ResolveMutationContext(http, resolved.Workflow!);
            configuration = BuildExternalTriggerConfiguration(
                resolved.Workflow!,
                input,
                BuildExternalTriggerId(resolved.Workflow!),
                context.AuthenticatedNyxIdOwnerSubject);
        }
        catch (Exception ex) when (ScheduledDispatchEndpoints.TryMapScheduleConfigurationError(ex, out var result))
        {
            return result;
        }

        try
        {
            var receipt = await schedules.EnsureAsync(configuration, context, ct);
            var response = WorkflowExternalTriggerHttpResult.FromMutation(
                resolved.Workflow!,
                configuration,
                receipt,
                ScheduledDispatchCredentialSourceKind.ScopeOwnerNyxId,
                CredentialExpiresAt: null,
                PermissionDigest: string.Empty,
                PolicyVersion: string.Empty);
            return Results.Accepted(BuildExternalTriggerLocation(scopeId, receipt.ScheduleId), response);
        }
        catch (Exception ex) when (ScheduledDispatchEndpoints.TryMapScheduleMutationError(ex, out var result))
        {
            return result;
        }
    }

    internal static async Task<IResult> GetExternalTrigger(
        HttpContext http,
        string scopeId,
        string workflowId,
        [FromServices] IScopeWorkflowQueryPort workflowQueryPort,
        [FromServices] IScheduledDispatchApplicationService schedules,
        CancellationToken ct = default)
    {
        var resolved = await ResolveWorkflowAsync(http, scopeId, workflowId, workflowQueryPort, ct);
        if (resolved.Result != null)
            return resolved.Result;

        var triggerId = BuildExternalTriggerId(resolved.Workflow!);
        var detail = await schedules.GetAsync(triggerId, ct);
        if (detail == null || !BelongsToWorkflow(detail.Schedule, resolved.Workflow!))
        {
            return Results.Ok(WorkflowExternalTriggerHttpResult.NotConfigured(
                resolved.Workflow!,
                triggerId));
        }

        return Results.Ok(WorkflowExternalTriggerHttpResult.FromSummary(
            configured: true,
            resolved.Workflow!,
            detail.Schedule));
    }

    internal static async Task<IResult> FireExternalTrigger(
        HttpContext http,
        string scopeId,
        string triggerId,
        [FromServices] IScheduledDispatchApplicationService schedules,
        CancellationToken ct = default)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;
        if (TryCreateInvalidScheduleIdResult(triggerId, out var invalidTriggerId))
            return invalidTriggerId;

        var detail = await schedules.GetAsync(triggerId, ct);
        if (detail == null || !IsWorkflowExternalTriggerForScope(detail.Schedule, scopeId, triggerId))
        {
            return Results.NotFound(new
            {
                code = "WORKFLOW_EXTERNAL_TRIGGER_NOT_FOUND",
                message = $"Workflow trigger '{triggerId}' was not found for scope '{scopeId}'.",
            });
        }

        try
        {
            var receipt = await schedules.RunNowAsync(
                triggerId,
                new ScheduledDispatchMutationContext(
                    scopeId,
                    ExpectedServiceTarget: new ScheduledDispatchExpectedServiceTarget(
                        ScheduledDispatchScheduleKind.Workflow,
                        ScheduledDispatchTargetKind.ServiceInvocation,
                        detail.Schedule.ServiceIdentity,
                        ChatEndpointId)),
                ct);
            return Results.Accepted(
                BuildExternalTriggerLocation(scopeId, triggerId),
                WorkflowExternalTriggerFireHttpResult.FromReceipt(receipt));
        }
        catch (Exception ex) when (ScheduledDispatchEndpoints.TryMapScheduleMutationError(ex, out var result))
        {
            return result;
        }
    }

    internal static async Task<IResult> Create(
        HttpContext http,
        string scopeId,
        string workflowId,
        WorkflowScheduleConfigurationHttpRequest input,
        [FromServices] IScopeWorkflowQueryPort workflowQueryPort,
        [FromServices] IScheduledDispatchApplicationService schedules,
        CancellationToken ct = default)
    {
        var resolved = await ResolveWorkflowAsync(http, scopeId, workflowId, workflowQueryPort, ct);
        if (resolved.Result != null)
            return resolved.Result;

        ScheduledDispatchConfiguration configuration;
        ScheduledDispatchMutationContext context;
        try
        {
            context = ResolveMutationContext(http, resolved.Workflow!);
            configuration = BuildConfiguration(resolved.Workflow!, input, input.ScheduleId, context.AuthenticatedNyxIdOwnerSubject);
        }
        catch (Exception ex) when (ScheduledDispatchEndpoints.TryMapScheduleConfigurationError(ex, out var result))
        {
            return result;
        }

        try
        {
            var receipt = await schedules.CreateAsync(configuration, context, ct);
            return Results.Accepted(BuildWorkflowScheduleLocation(scopeId, workflowId, receipt.ScheduleId), receipt);
        }
        catch (Exception ex) when (ScheduledDispatchEndpoints.TryMapScheduleMutationError(ex, out var result))
        {
            return result;
        }
    }

    internal static async Task<IResult> Update(
        HttpContext http,
        string scopeId,
        string workflowId,
        string scheduleId,
        WorkflowScheduleConfigurationHttpRequest input,
        [FromServices] IScopeWorkflowQueryPort workflowQueryPort,
        [FromServices] IScheduledDispatchApplicationService schedules,
        CancellationToken ct = default)
    {
        var resolved = await ResolveWorkflowAsync(http, scopeId, workflowId, workflowQueryPort, ct);
        if (resolved.Result != null)
            return resolved.Result;

        var ownership = await RequireWorkflowScheduleAsync(schedules, scheduleId, resolved.Workflow!, ct);
        if (ownership.Result != null)
            return ownership.Result;

        ScheduledDispatchConfiguration configuration;
        ScheduledDispatchMutationContext context;
        try
        {
            context = ResolveMutationContext(http, resolved.Workflow!);
            configuration = BuildConfiguration(
                resolved.Workflow!,
                input with { ScheduleId = null },
                scheduleId,
                context.AuthenticatedNyxIdOwnerSubject);
        }
        catch (Exception ex) when (ScheduledDispatchEndpoints.TryMapScheduleConfigurationError(ex, out var result))
        {
            return result;
        }

        try
        {
            var receipt = await schedules.UpdateAsync(scheduleId, configuration, context, ct);
            return Results.Accepted(BuildWorkflowScheduleLocation(scopeId, workflowId, receipt.ScheduleId), receipt);
        }
        catch (Exception ex) when (ScheduledDispatchEndpoints.TryMapScheduleMutationError(ex, out var result))
        {
            return result;
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
        if (resolved.Result != null)
            return resolved.Result;

        var workflow = resolved.Workflow!;
        var result = await schedules.ListAsync(new ScheduledDispatchListQuery(
            Take: take,
            Cursor: cursor,
            IncludeTotalCount: includeTotalCount,
            TargetKind: ScheduledDispatchTargetKind.ServiceInvocation,
            ServiceEndpointId: ChatEndpointId,
            ServiceKey: workflow.ServiceKey,
            ServiceId: workflow.PublishedServiceId,
            ScheduleKind: ScheduledDispatchScheduleKind.Workflow,
            ExcludeTeamOwned: true), ct);
        return Results.Ok(result);
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
        if (resolved.Result != null)
            return resolved.Result;

        var ownership = await RequireWorkflowScheduleAsync(schedules, scheduleId, resolved.Workflow!, ct);
        if (ownership.Result != null)
            return ownership.Result;

        return Results.Ok(ownership.Detail);
    }

    internal static async Task<IResult> Preview(
        HttpContext http,
        string scopeId,
        string workflowId,
        WorkflowSchedulePreviewHttpRequest input,
        [FromServices] IScopeWorkflowQueryPort workflowQueryPort,
        [FromServices] IScheduledDispatchApplicationService schedules,
        CancellationToken ct = default)
    {
        var resolved = await ResolveWorkflowAsync(http, scopeId, workflowId, workflowQueryPort, ct);
        if (resolved.Result != null)
            return resolved.Result;

        try
        {
            return Results.Ok(await schedules.PreviewAsync(
                input.CronExpression,
                input.Timezone,
                input.Count <= 0 ? 5 : input.Count,
                input.FromUtc,
                ct));
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
        WorkflowScheduleStateChangeHttpRequest? input,
        [FromServices] IScopeWorkflowQueryPort workflowQueryPort,
        [FromServices] IScheduledDispatchApplicationService schedules,
        CancellationToken ct = default) =>
        await ChangeStateAsync(
            http,
            scopeId,
            workflowId,
            scheduleId,
            input?.Reason ?? string.Empty,
            workflowQueryPort,
            workflowCataloguePort: null,
            schedules,
            static (service, id, reason, context, token) => service.EnableAsync(id, reason, context, token),
            requireRunnableWorkflow: true,
            ct);

    internal static async Task<IResult> Disable(
        HttpContext http,
        string scopeId,
        string workflowId,
        string scheduleId,
        WorkflowScheduleStateChangeHttpRequest? input,
        [FromServices] IScopeWorkflowQueryPort workflowQueryPort,
        [FromServices] IScopeWorkflowCatalogueCommittedSourcePort workflowCataloguePort,
        [FromServices] IScheduledDispatchApplicationService schedules,
        CancellationToken ct = default) =>
        await ChangeStateAsync(
            http,
            scopeId,
            workflowId,
            scheduleId,
            input?.Reason ?? string.Empty,
            workflowQueryPort,
            workflowCataloguePort,
            schedules,
            static (service, id, reason, context, token) => service.DisableAsync(id, reason, context, token),
            requireRunnableWorkflow: false,
            ct);

    internal static async Task<IResult> Delete(
        HttpContext http,
        string scopeId,
        string workflowId,
        string scheduleId,
        [FromQuery] string? reason,
        [FromBody] WorkflowScheduleStateChangeHttpRequest? input,
        [FromServices] IScopeWorkflowQueryPort workflowQueryPort,
        [FromServices] IScopeWorkflowCatalogueCommittedSourcePort workflowCataloguePort,
        [FromServices] IScheduledDispatchApplicationService schedules,
        CancellationToken ct = default) =>
        await ChangeStateAsync(
            http,
            scopeId,
            workflowId,
            scheduleId,
            input?.Reason ?? reason ?? string.Empty,
            workflowQueryPort,
            workflowCataloguePort,
            schedules,
            static (service, id, deleteReason, context, token) => service.DeleteAsync(id, deleteReason, context, token),
            requireRunnableWorkflow: false,
            ct);

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
        if (resolved.Result != null)
            return resolved.Result;

        var ownership = await RequireWorkflowScheduleAsync(schedules, scheduleId, resolved.Workflow!, ct);
        if (ownership.Result != null)
            return ownership.Result;

        try
        {
            var receipt = await schedules.RunNowAsync(scheduleId, ResolveMutationContext(http, resolved.Workflow!), ct: ct);
            return Results.Accepted(BuildWorkflowScheduleLocation(scopeId, workflowId, receipt.ScheduleId), receipt);
        }
        catch (Exception ex) when (ScheduledDispatchEndpoints.TryMapScheduleMutationError(ex, out var result))
        {
            return result;
        }
    }

    private static async Task<IResult> ChangeStateAsync(
        HttpContext http,
        string scopeId,
        string workflowId,
        string scheduleId,
        string reason,
        IScopeWorkflowQueryPort workflowQueryPort,
        IScopeWorkflowCatalogueCommittedSourcePort? workflowCataloguePort,
        IScheduledDispatchApplicationService schedules,
        Func<IScheduledDispatchApplicationService, string, string, ScheduledDispatchMutationContext, CancellationToken, Task<ScheduledDispatchMutationReceipt>> mutateAsync,
        bool requireRunnableWorkflow,
        CancellationToken ct)
    {
        var resolved = requireRunnableWorkflow
            ? await ResolveWorkflowAsync(http, scopeId, workflowId, workflowQueryPort, ct)
            : await ResolveWorkflowForTeardownAsync(
                http,
                scopeId,
                workflowId,
                workflowCataloguePort ?? throw new InvalidOperationException(
                    "Workflow catalogue source is required for schedule teardown."),
                ct);
        if (resolved.Result != null)
            return resolved.Result;

        var context = ResolveMutationContext(http, resolved.Workflow!);
        var ownership = await RequireWorkflowScheduleAsync(schedules, scheduleId, resolved.Workflow!, ct);
        if (ownership.Result != null)
            return ownership.Result;

        try
        {
            var receipt = await mutateAsync(schedules, scheduleId, reason, context, ct);
            return Results.Accepted(BuildWorkflowScheduleLocation(scopeId, workflowId, receipt.ScheduleId), receipt);
        }
        catch (Exception ex) when (ScheduledDispatchEndpoints.TryMapScheduleMutationError(ex, out var result))
        {
            return result;
        }
    }

    private static async Task<ResolvedWorkflowResult> ResolveWorkflowAsync(
        HttpContext http,
        string scopeId,
        string workflowId,
        IScopeWorkflowQueryPort workflowQueryPort,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return new ResolvedWorkflowResult(null, denied);
        if (TryCreateInvalidWorkflowIdResult(workflowId, out var invalidWorkflowId))
            return new ResolvedWorkflowResult(null, invalidWorkflowId);

        var lookup = await workflowQueryPort.LookupByWorkflowIdAsync(scopeId, workflowId, ct);
        if (lookup.IsRunnable)
            return new ResolvedWorkflowResult(lookup.Workflow, null);

        var (statusCode, code, message) = ScopeWorkflowEndpoints.MapWorkflowLookupError(scopeId, workflowId, lookup);
        return new ResolvedWorkflowResult(
            null,
            Results.Json(new { code, message }, statusCode: statusCode));
    }

    private static async Task<ResolvedWorkflowResult> ResolveWorkflowForTeardownAsync(
        HttpContext http,
        string scopeId,
        string workflowId,
        IScopeWorkflowCatalogueCommittedSourcePort workflowCataloguePort,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return new ResolvedWorkflowResult(null, denied);
        if (TryCreateInvalidWorkflowIdResult(workflowId, out var invalidWorkflowId))
            return new ResolvedWorkflowResult(null, invalidWorkflowId);

        var catalogueLookup = await workflowCataloguePort.LookupCatalogueByWorkflowIdAsync(scopeId, workflowId, ct);
        var workflow = catalogueLookup.Workflow;
        if (catalogueLookup.Status == ScopeWorkflowCatalogueLookupStatus.Found &&
            workflow != null &&
            !string.IsNullOrWhiteSpace(workflow.ServiceAppId) &&
            !string.IsNullOrWhiteSpace(workflow.ServiceNamespace) &&
            !string.IsNullOrWhiteSpace(workflow.PublishedServiceId))
        {
            return new ResolvedWorkflowResult(workflow, null);
        }

        if (catalogueLookup.Status == ScopeWorkflowCatalogueLookupStatus.Ambiguous)
        {
            return new ResolvedWorkflowResult(
                null,
                Results.Json(
                    new
                    {
                        code = "USER_WORKFLOW_AMBIGUOUS",
                        message = $"Workflow '{workflowId}' resolves to multiple published services for scope '{scopeId}'.",
                    },
                    statusCode: StatusCodes.Status409Conflict));
        }

        var lookup = catalogueLookup.Status == ScopeWorkflowCatalogueLookupStatus.Found
            ? new ScopeWorkflowLookupResult(ScopeWorkflowLookupStatus.Stale, null, "catalogue_service_identity_incomplete")
            : new ScopeWorkflowLookupResult(ScopeWorkflowLookupStatus.NotFound, null, string.Empty);
        var (statusCode, code, message) = ScopeWorkflowEndpoints.MapWorkflowLookupError(scopeId, workflowId, lookup);
        return new ResolvedWorkflowResult(
            null,
            Results.Json(new { code, message }, statusCode: statusCode));
    }

    private static async Task<WorkflowScheduleOwnershipResult> RequireWorkflowScheduleAsync(
        IScheduledDispatchApplicationService schedules,
        string scheduleId,
        ScopeWorkflowSummary workflow,
        CancellationToken ct)
    {
        if (TryCreateInvalidScheduleIdResult(scheduleId, out var invalidScheduleId))
            return new WorkflowScheduleOwnershipResult(null, invalidScheduleId);

        var detail = await schedules.GetAsync(scheduleId, ct);
        if (detail == null || !BelongsToWorkflow(detail.Schedule, workflow))
        {
            return new WorkflowScheduleOwnershipResult(
                null,
                Results.NotFound(new
                {
                    code = "WORKFLOW_SCHEDULE_NOT_FOUND",
                    message = $"Schedule '{scheduleId}' was not found for workflow '{workflow.WorkflowId}'.",
                }));
        }

        return new WorkflowScheduleOwnershipResult(detail, null);
    }

    private static bool BelongsToWorkflow(ScheduledDispatchSummary schedule, ScopeWorkflowSummary workflow)
    {
        if (schedule.ScheduleKind != ScheduledDispatchScheduleKind.Workflow ||
            schedule.TargetKind != ScheduledDispatchTargetKind.ServiceInvocation ||
            !string.Equals(schedule.ServiceEndpointId, ChatEndpointId, StringComparison.Ordinal) ||
            !string.Equals(schedule.ServiceIdentity.TenantId, workflow.ScopeId, StringComparison.Ordinal) ||
            !string.Equals(schedule.ServiceId, workflow.PublishedServiceId, StringComparison.Ordinal))
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(workflow.ServiceKey) ||
               string.Equals(schedule.ServiceKey, workflow.ServiceKey, StringComparison.Ordinal);
    }

    private static ScheduledDispatchConfiguration BuildExternalTriggerConfiguration(
        ScopeWorkflowSummary workflow,
        WorkflowExternalTriggerConfigurationHttpRequest input,
        string triggerId,
        ScheduledServiceInvocationNyxIdSubjectRef? authenticatedOwnerSubject) =>
        BuildConfiguration(
            workflow,
            new WorkflowScheduleConfigurationHttpRequest
            {
                ScheduleId = triggerId,
                DisplayName = input.DisplayName,
                CronExpression = input.CronExpression,
                Timezone = input.Timezone,
                Enabled = input.Enabled,
                Prompt = input.Prompt,
                ScheduleMode = input.ScheduleMode,
                OneShotFireAt = input.OneShotFireAt,
            },
            triggerId,
            authenticatedOwnerSubject);

    private static ScheduledDispatchConfiguration BuildConfiguration(
        ScopeWorkflowSummary workflow,
        WorkflowScheduleConfigurationHttpRequest input,
        string? fallbackScheduleId,
        ScheduledServiceInvocationNyxIdSubjectRef? authenticatedOwnerSubject)
    {
        var serviceInvocation = new ScheduledServiceInvocationTargetDescriptor(
            BuildServiceIdentity(workflow),
            ChatEndpointId,
            Any.Pack(BuildChatRequest(input)),
            workflow.ActiveRevisionId,
            Auth: BuildDefaultWorkflowScheduleAuth(authenticatedOwnerSubject));
        return new ScheduledDispatchConfiguration(
            string.IsNullOrWhiteSpace(input.ScheduleId) ? fallbackScheduleId ?? string.Empty : input.ScheduleId,
            input.DisplayName ?? string.Empty,
            new ScheduledDispatchTargetDescriptor(
                ScheduledDispatchTargetKind.ServiceInvocation,
                ServiceInvocation: serviceInvocation),
            input.CronExpression ?? string.Empty,
            input.Timezone ?? string.Empty,
            input.Enabled,
            input.Headers ?? new Dictionary<string, string>(StringComparer.Ordinal),
            ScheduledDispatchScheduleKind.Workflow,
            input.ScheduleMode,
            input.OneShotFireAt)
        {
            CredentialRequirementTargetKind = ScheduledDispatchCredentialRequirementTargetKind.WorkflowService,
        };
    }

    private static ServiceIdentity BuildServiceIdentity(ScopeWorkflowSummary workflow) => new()
    {
        TenantId = workflow.ScopeId,
        AppId = workflow.ServiceAppId,
        Namespace = workflow.ServiceNamespace,
        ServiceId = workflow.PublishedServiceId,
    };

    private static ChatRequestEvent BuildChatRequest(WorkflowScheduleConfigurationHttpRequest input)
    {
        var request = new ChatRequestEvent
        {
            Prompt = input.Prompt ?? string.Empty,
        };

        if (input.Headers == null)
            return request;

        foreach (var (key, value) in input.Headers)
            request.Metadata[key] = value;
        return request;
    }

    private static ScheduledServiceInvocationAuth BuildDefaultWorkflowScheduleAuth(
        ScheduledServiceInvocationNyxIdSubjectRef? authenticatedOwnerSubject)
    {
        if (authenticatedOwnerSubject == null)
        {
            throw new ArgumentException(
                "Authenticated NyxID owner subject is required for workflow schedule auth.",
                nameof(authenticatedOwnerSubject));
        }

        return new ScheduledServiceInvocationAuth(
            new ScheduledServiceInvocationNyxIdCredentialSource(
                authenticatedOwnerSubject,
                DefaultWorkflowScheduleNyxIdScope,
                ScheduledServiceInvocationNyxIdCredentialRole.ScopeOwner));
    }

    private static ScheduledDispatchMutationContext ResolveMutationContext(
        HttpContext http,
        ScopeWorkflowSummary workflow)
    {
        ArgumentNullException.ThrowIfNull(http);
        return new ScheduledDispatchMutationContext(
            ReadFirstClaim(http.User, "scope_id", "workflow.scope_id"),
            ResolveAuthenticatedNyxIdOwnerSubject(http),
            ExpectedServiceTarget: BuildExpectedServiceTarget(workflow));
    }

    private static ScheduledDispatchExpectedServiceTarget BuildExpectedServiceTarget(ScopeWorkflowSummary workflow) => new(
        ScheduledDispatchScheduleKind.Workflow,
        ScheduledDispatchTargetKind.ServiceInvocation,
        BuildServiceIdentity(workflow),
        ChatEndpointId);

    private static bool TryCreateInvalidWorkflowIdResult(string? workflowId, out IResult result)
    {
        if (string.IsNullOrWhiteSpace(workflowId) || workflowId.Trim().Contains(':', StringComparison.Ordinal))
        {
            result = Results.BadRequest(new
            {
                code = "INVALID_WORKFLOW_ID",
                message = "workflowId is required and must not contain ':'.",
            });
            return true;
        }

        result = null!;
        return false;
    }

    private static bool TryCreateInvalidScheduleIdResult(string? scheduleId, out IResult result)
    {
        var normalized = scheduleId?.Trim();
        if (string.IsNullOrWhiteSpace(normalized) ||
            normalized.Any(static ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-')))
        {
            result = Results.BadRequest(new
            {
                code = "INVALID_SCHEDULE_ID",
                message = "scheduleId may only contain letters, digits, '.', '_', and '-'.",
            });
            return true;
        }

        result = null!;
        return false;
    }

    private static ScheduledServiceInvocationNyxIdSubjectRef? ResolveAuthenticatedNyxIdOwnerSubject(HttpContext http)
    {
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

    private static string BuildExternalTriggerId(ScopeWorkflowSummary workflow)
    {
        var triggerKey = string.Join(
            ":",
            workflow.ScopeId.Trim(),
            workflow.DefinitionActorId.Trim(),
            workflow.PublishedServiceId.Trim());
        var triggerHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(triggerKey)))
            .ToLowerInvariant()[..32];
        return $"workflow-trigger-{triggerHash}";
    }

    private static string BuildExternalTriggerLocation(string scopeId, string triggerId) =>
        $"/api/scopes/{Uri.EscapeDataString(scopeId)}/workflow-triggers/{Uri.EscapeDataString(triggerId)}:fire";

    private static bool IsWorkflowExternalTriggerForScope(
        ScheduledDispatchSummary schedule,
        string scopeId,
        string triggerId) =>
        schedule.ScheduleId == triggerId &&
        schedule.ScheduleKind == ScheduledDispatchScheduleKind.Workflow &&
        schedule.TargetKind == ScheduledDispatchTargetKind.ServiceInvocation &&
        string.Equals(schedule.ServiceEndpointId, ChatEndpointId, StringComparison.Ordinal) &&
        string.Equals(schedule.ServiceIdentity.TenantId, scopeId, StringComparison.Ordinal);

    private static string BuildWorkflowScheduleLocation(string scopeId, string workflowId, string scheduleId) =>
        $"/api/scopes/{Uri.EscapeDataString(scopeId)}/workflows/{Uri.EscapeDataString(workflowId)}/schedules/{Uri.EscapeDataString(scheduleId)}";

    private sealed record ResolvedWorkflowResult(
        ScopeWorkflowSummary? Workflow,
        IResult? Result);

    private sealed record WorkflowScheduleOwnershipResult(
        ScheduledDispatchDetail? Detail,
        IResult? Result);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkflowExternalTriggerConfigurationHttpRequest
{
    public string? DisplayName { get; init; }
    public string? CronExpression { get; init; }
    public string? Timezone { get; init; }
    public bool Enabled { get; init; } = true;
    public string? Prompt { get; init; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ScheduledDispatchScheduleMode ScheduleMode { get; init; } = ScheduledDispatchScheduleMode.RecurringCron;
    public DateTimeOffset? OneShotFireAt { get; init; }
}

public sealed record WorkflowExternalTriggerHttpResult
{
    public bool Configured { get; init; }
    public string AcceptanceStage { get; init; } = string.Empty;
    public string CommandId { get; init; } = string.Empty;
    public string CorrelationId { get; init; } = string.Empty;
    public DateTimeOffset? AcceptedAt { get; init; }
    public required string TriggerId { get; init; }
    public required string WorkflowId { get; init; }
    public required string ServiceId { get; init; }
    public string RevisionId { get; init; } = string.Empty;
    public string EndpointId { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public bool Enabled { get; init; }
    public string CronExpression { get; init; } = string.Empty;
    public string Timezone { get; init; } = string.Empty;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ScheduledDispatchScheduleMode ScheduleMode { get; init; }
    public DateTimeOffset? OneShotFireAt { get; init; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ScheduledDispatchCredentialSourceKind CredentialSourceKind { get; init; }
    public bool AgentKeyReady { get; init; }
    public DateTimeOffset? CredentialExpiresAt { get; init; }
    public string PermissionDigest { get; init; } = string.Empty;
    public string PolicyVersion { get; init; } = string.Empty;

    public static WorkflowExternalTriggerHttpResult FromMutation(
        ScopeWorkflowSummary workflow,
        ScheduledDispatchConfiguration configuration,
        ScheduledDispatchMutationReceipt receipt,
        ScheduledDispatchCredentialSourceKind credentialSourceKind,
        DateTimeOffset? CredentialExpiresAt,
        string PermissionDigest,
        string PolicyVersion)
    {
        var invocation = configuration.Target.ServiceInvocation!;
        return new WorkflowExternalTriggerHttpResult
        {
            Configured = false,
            AcceptanceStage = receipt.AckStage,
            CommandId = receipt.CommandId,
            CorrelationId = receipt.CorrelationId,
            AcceptedAt = receipt.AckedAt,
            TriggerId = receipt.ScheduleId,
            WorkflowId = workflow.WorkflowId,
            ServiceId = invocation.Identity.ServiceId,
            RevisionId = invocation.RevisionId ?? workflow.ActiveRevisionId,
            EndpointId = invocation.EndpointId,
            Status = "pending",
            Enabled = configuration.Enabled,
            CronExpression = configuration.CronExpression,
            Timezone = configuration.Timezone,
            ScheduleMode = configuration.ScheduleMode,
            OneShotFireAt = configuration.OneShotFireAt,
            CredentialSourceKind = credentialSourceKind,
            AgentKeyReady = credentialSourceKind == ScheduledDispatchCredentialSourceKind.ScheduledInvocationAgentKey,
            CredentialExpiresAt = CredentialExpiresAt,
            PermissionDigest = PermissionDigest,
            PolicyVersion = PolicyVersion,
        };
    }

    public static WorkflowExternalTriggerHttpResult FromSummary(
        bool configured,
        ScopeWorkflowSummary workflow,
        ScheduledDispatchSummary schedule) =>
        new()
        {
            Configured = configured,
            TriggerId = schedule.ScheduleId,
            WorkflowId = workflow.WorkflowId,
            ServiceId = schedule.ServiceId,
            RevisionId = schedule.ServiceRevisionId,
            EndpointId = schedule.ServiceEndpointId,
            Status = schedule.Deleted ? "deleted" : schedule.Enabled ? "enabled" : "disabled",
            Enabled = schedule.Enabled,
            CronExpression = schedule.CronExpression,
            Timezone = schedule.Timezone,
            ScheduleMode = schedule.ScheduleMode,
            OneShotFireAt = schedule.OneShotFireAt,
            CredentialSourceKind = schedule.CredentialSourceKind,
            AgentKeyReady = schedule.CredentialSourceKind == ScheduledDispatchCredentialSourceKind.ScheduledInvocationAgentKey,
            CredentialExpiresAt = schedule.CredentialExpiresAt,
            PermissionDigest = schedule.PermissionDigest,
            PolicyVersion = schedule.PolicyVersion,
        };

    public static WorkflowExternalTriggerHttpResult NotConfigured(
        ScopeWorkflowSummary workflow,
        string triggerId) =>
        new()
        {
            Configured = false,
            TriggerId = triggerId,
            WorkflowId = workflow.WorkflowId,
            ServiceId = workflow.PublishedServiceId,
            RevisionId = workflow.ActiveRevisionId,
            EndpointId = "chat",
            Status = "not_configured",
            CredentialSourceKind = ScheduledDispatchCredentialSourceKind.None,
        };
}

public sealed record WorkflowExternalTriggerFireHttpResult
{
    public required string TriggerId { get; init; }
    public bool Accepted { get; init; }
    public DateTimeOffset ScheduledFireAt { get; init; }
    public required string IdempotencyKey { get; init; }
    public required string CommandId { get; init; }
    public required string CorrelationId { get; init; }
    public DateTimeOffset AckedAt { get; init; }
    public string AckStage { get; init; } = string.Empty;

    public static WorkflowExternalTriggerFireHttpResult FromReceipt(
        ScheduledDispatchRunNowReceipt receipt) =>
        new()
        {
            TriggerId = receipt.ScheduleId,
            Accepted = receipt.Accepted,
            ScheduledFireAt = receipt.ScheduledFireAt,
            IdempotencyKey = receipt.IdempotencyKey,
            CommandId = receipt.CommandId,
            CorrelationId = receipt.CorrelationId,
            AckedAt = receipt.AckedAt,
            AckStage = receipt.AckStage,
        };
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkflowScheduleConfigurationHttpRequest
{
    public string? ScheduleId { get; init; }
    public string? DisplayName { get; init; }
    public string? CronExpression { get; init; }
    public string? Timezone { get; init; }
    public bool Enabled { get; init; } = true;
    public string? Prompt { get; init; }
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ScheduledDispatchScheduleMode ScheduleMode { get; init; } = ScheduledDispatchScheduleMode.RecurringCron;
    public DateTimeOffset? OneShotFireAt { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkflowSchedulePreviewHttpRequest
{
    public required string CronExpression { get; init; }
    public string? Timezone { get; init; }
    public int Count { get; init; } = 5;
    public DateTimeOffset? FromUtc { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkflowScheduleStateChangeHttpRequest
{
    public string? Reason { get; init; }
}
