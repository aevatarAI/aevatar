using System.Security.Claims;
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
        group.MapGet("/{scopeId}/workflows/{workflowId}/schedules", List)
            .Produces<ScheduledDispatchListResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);
        group.MapPost("/{scopeId}/workflows/{workflowId}/schedules/preview", Preview)
            .Produces<ScheduledDispatchPreview>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest);
        group.MapPost("/{scopeId}/workflows/{workflowId}/schedules", Create)
            .Produces<ScheduledDispatchMutationReceipt>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);
        group.MapGet("/{scopeId}/workflows/{workflowId}/schedules/{scheduleId}", Get)
            .Produces<ScheduledDispatchDetail>(StatusCodes.Status200OK)
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
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);
        group.MapPost("/{scopeId}/workflows/{workflowId}/schedules/{scheduleId}:disable", Disable)
            .Produces<ScheduledDispatchMutationReceipt>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);
        group.MapPost("/{scopeId}/workflows/{workflowId}/schedules/{scheduleId}:run-now", RunNow)
            .Produces<ScheduledDispatchRunNowReceipt>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);
        group.MapDelete("/{scopeId}/workflows/{workflowId}/schedules/{scheduleId}", Delete)
            .Produces<ScheduledDispatchMutationReceipt>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);
        return group;
    }

    internal static Task<IResult> Create(
        HttpContext http,
        string scopeId,
        string workflowId,
        WorkflowScheduleConfigurationHttpRequest input,
        [FromServices] IScopeWorkflowQueryPort workflowQueryPort,
        [FromServices] IScheduledDispatchApplicationService schedules,
        CancellationToken ct = default) =>
        ScopeWorkflowScheduleOrchestration.CreateAsync(http, scopeId, workflowId, input, workflowQueryPort, schedules, ct);

    internal static Task<IResult> Update(
        HttpContext http,
        string scopeId,
        string workflowId,
        string scheduleId,
        WorkflowScheduleConfigurationHttpRequest input,
        [FromServices] IScopeWorkflowQueryPort workflowQueryPort,
        [FromServices] IScheduledDispatchApplicationService schedules,
        CancellationToken ct = default) =>
        ScopeWorkflowScheduleOrchestration.UpdateAsync(http, scopeId, workflowId, scheduleId, input, workflowQueryPort, schedules, ct);

    internal static Task<IResult> List(
        HttpContext http,
        string scopeId,
        string workflowId,
        [FromServices] IScopeWorkflowQueryPort workflowQueryPort,
        [FromServices] IScheduledDispatchApplicationService schedules,
        int take = 50,
        string? cursor = null,
        bool includeTotalCount = false,
        CancellationToken ct = default) =>
        ScopeWorkflowScheduleOrchestration.ListAsync(http, scopeId, workflowId, workflowQueryPort, schedules, take, cursor, includeTotalCount, ct);

    internal static Task<IResult> Get(
        HttpContext http,
        string scopeId,
        string workflowId,
        string scheduleId,
        [FromServices] IScopeWorkflowQueryPort workflowQueryPort,
        [FromServices] IScheduledDispatchApplicationService schedules,
        CancellationToken ct = default) =>
        ScopeWorkflowScheduleOrchestration.GetAsync(http, scopeId, workflowId, scheduleId, workflowQueryPort, schedules, ct);

    internal static Task<IResult> Preview(
        WorkflowSchedulePreviewHttpRequest input,
        [FromServices] IScheduledDispatchApplicationService schedules,
        CancellationToken ct = default) =>
        ScopeWorkflowScheduleOrchestration.PreviewAsync(input, schedules, ct);

    internal static Task<IResult> Enable(
        HttpContext http,
        string scopeId,
        string workflowId,
        string scheduleId,
        WorkflowScheduleStateChangeHttpRequest? input,
        [FromServices] IScopeWorkflowQueryPort workflowQueryPort,
        [FromServices] IScheduledDispatchApplicationService schedules,
        CancellationToken ct = default) =>
        ScopeWorkflowScheduleOrchestration.EnableAsync(http, scopeId, workflowId, scheduleId, input, workflowQueryPort, schedules, ct);

    internal static Task<IResult> Disable(
        HttpContext http,
        string scopeId,
        string workflowId,
        string scheduleId,
        WorkflowScheduleStateChangeHttpRequest? input,
        [FromServices] IScopeWorkflowQueryPort workflowQueryPort,
        [FromServices] IScheduledDispatchApplicationService schedules,
        CancellationToken ct = default) =>
        ScopeWorkflowScheduleOrchestration.DisableAsync(http, scopeId, workflowId, scheduleId, input, workflowQueryPort, schedules, ct);

    internal static Task<IResult> Delete(
        HttpContext http,
        string scopeId,
        string workflowId,
        string scheduleId,
        [FromQuery] string? reason,
        [FromBody] WorkflowScheduleStateChangeHttpRequest? input,
        [FromServices] IScopeWorkflowQueryPort workflowQueryPort,
        [FromServices] IScheduledDispatchApplicationService schedules,
        CancellationToken ct = default) =>
        ScopeWorkflowScheduleOrchestration.DeleteAsync(http, scopeId, workflowId, scheduleId, reason, input, workflowQueryPort, schedules, ct);

    internal static Task<IResult> RunNow(
        HttpContext http,
        string scopeId,
        string workflowId,
        string scheduleId,
        [FromServices] IScopeWorkflowQueryPort workflowQueryPort,
        [FromServices] IScheduledDispatchApplicationService schedules,
        CancellationToken ct = default) =>
        ScopeWorkflowScheduleOrchestration.RunNowAsync(http, scopeId, workflowId, scheduleId, workflowQueryPort, schedules, ct);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkflowScheduleConfigurationHttpRequest
{
    public string? ScheduleId { get; init; }
    public string? DisplayName { get; init; }
    public required string CronExpression { get; init; }
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
