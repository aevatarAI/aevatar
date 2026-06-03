using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Schedules;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

public static class ScheduledDispatchEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapPost("/scheduled-dispatches", Create)
            .WithTags("Scheduled dispatches")
            .Produces<ScheduledDispatchMutationReceipt>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status409Conflict);
        group.MapPut("/scheduled-dispatches/{scheduleId}", Update)
            .WithTags("Scheduled dispatches")
            .Produces<ScheduledDispatchMutationReceipt>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status409Conflict);
        group.MapPost("/scheduled-dispatches/{scheduleId}/enable", Enable)
            .WithTags("Scheduled dispatches")
            .Produces<ScheduledDispatchMutationReceipt>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);
        group.MapPost("/scheduled-dispatches/{scheduleId}/disable", Disable)
            .WithTags("Scheduled dispatches")
            .Produces<ScheduledDispatchMutationReceipt>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);
        group.MapGet("/scheduled-dispatches", List)
            .WithTags("Scheduled dispatches")
            .Produces<ScheduledDispatchListResult>(StatusCodes.Status200OK);
        group.MapGet("/scheduled-dispatches/{scheduleId}", Get)
            .WithTags("Scheduled dispatches")
            .Produces<ScheduledDispatchDetail>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);
        group.MapPost("/scheduled-dispatches/preview", Preview)
            .WithTags("Scheduled dispatches")
            .Produces<ScheduledDispatchPreview>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest);
        group.MapPost("/scheduled-dispatches/{scheduleId}/run-now", RunNow)
            .WithTags("Scheduled dispatches")
            .Produces<ScheduledDispatchRunNowReceipt>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);
    }

    internal static async Task<IResult> Create(
        ScheduledDispatchConfigurationHttpRequest input,
        IScheduledDispatchApplicationService schedules,
        CancellationToken ct = default)
    {
        try
        {
            var receipt = await schedules.CreateAsync(input.ToConfiguration(input.ScheduleId), ct);
            return Results.Accepted($"/api/scheduled-dispatches/{receipt.ScheduleId}", receipt);
        }
        catch (Exception ex) when (TryMapScheduleMutationError(ex, out var result))
        {
            return result;
        }
    }

    internal static async Task<IResult> Update(
        string scheduleId,
        ScheduledDispatchConfigurationHttpRequest input,
        IScheduledDispatchApplicationService schedules,
        CancellationToken ct = default)
    {
        try
        {
            var receipt = await schedules.UpdateAsync(scheduleId, input.ToConfiguration(scheduleId), ct);
            return Results.Accepted($"/api/scheduled-dispatches/{receipt.ScheduleId}", receipt);
        }
        catch (Exception ex) when (TryMapScheduleMutationError(ex, out var result))
        {
            return result;
        }
    }

    internal static async Task<IResult> Enable(
        string scheduleId,
        ScheduledDispatchStateChangeHttpRequest? input,
        IScheduledDispatchApplicationService schedules,
        CancellationToken ct = default)
    {
        try
        {
            var receipt = await schedules.EnableAsync(scheduleId, input?.Reason ?? string.Empty, ct);
            return Results.Accepted($"/api/scheduled-dispatches/{receipt.ScheduleId}", receipt);
        }
        catch (Exception ex) when (TryMapScheduleMutationError(ex, out var result))
        {
            return result;
        }
    }

    internal static async Task<IResult> Disable(
        string scheduleId,
        ScheduledDispatchStateChangeHttpRequest? input,
        IScheduledDispatchApplicationService schedules,
        CancellationToken ct = default)
    {
        try
        {
            var receipt = await schedules.DisableAsync(scheduleId, input?.Reason ?? string.Empty, ct);
            return Results.Accepted($"/api/scheduled-dispatches/{receipt.ScheduleId}", receipt);
        }
        catch (Exception ex) when (TryMapScheduleMutationError(ex, out var result))
        {
            return result;
        }
    }

    internal static async Task<IResult> List(
        IScheduledDispatchApplicationService schedules,
        int take = 50,
        string? cursor = null,
        bool includeTotalCount = false,
        CancellationToken ct = default)
    {
        return Results.Ok(await schedules.ListAsync(take, cursor, includeTotalCount, ct));
    }

    internal static async Task<IResult> Get(
        string scheduleId,
        IScheduledDispatchApplicationService schedules,
        CancellationToken ct = default)
    {
        try
        {
            var schedule = await schedules.GetAsync(scheduleId, ct);
            return schedule == null ? Results.NotFound() : Results.Ok(schedule);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    internal static async Task<IResult> Preview(
        ScheduledDispatchPreviewHttpRequest input,
        IScheduledDispatchApplicationService schedules,
        CancellationToken ct = default)
    {
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

    internal static async Task<IResult> RunNow(
        string scheduleId,
        IScheduledDispatchApplicationService schedules,
        CancellationToken ct = default)
    {
        try
        {
            var receipt = await schedules.RunNowAsync(scheduleId, ct);
            return Results.Accepted($"/api/scheduled-dispatches/{receipt.ScheduleId}", receipt);
        }
        catch (Exception ex) when (TryMapScheduleMutationError(ex, out var result))
        {
            return result;
        }
    }

    internal static bool TryMapScheduleMutationError(Exception ex, out IResult result)
    {
        switch (ex)
        {
            case ArgumentException argument:
                result = Results.BadRequest(new { error = argument.Message });
                return true;
            case ScheduledDispatchNotFoundException notFound:
                result = Results.NotFound(new { error = notFound.Message });
                return true;
            case ScheduledDispatchConflictException conflict:
                result = Results.Conflict(new { error = conflict.Message });
                return true;
            default:
                result = Results.Empty;
                return false;
        }
    }
}

public sealed record ScheduledDispatchConfigurationHttpRequest
{
    public string? ScheduleId { get; init; }
    public string? DisplayName { get; init; }
    public required string CronExpression { get; init; }
    public string? Timezone { get; init; }
    public bool Enabled { get; init; } = true;
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
    public ScheduledDispatchEnvelopeTargetHttpRequest? Envelope { get; init; }
    public ScheduledDispatchServiceInvocationTargetHttpRequest? ServiceInvocation { get; init; }
    public ScheduledDispatchWorkflowTargetHttpRequest? Workflow { get; init; }

    public ScheduledDispatchConfiguration ToConfiguration(string? fallbackScheduleId) =>
        new(
            ScheduleId: string.IsNullOrWhiteSpace(ScheduleId) ? fallbackScheduleId ?? string.Empty : ScheduleId,
            DisplayName: DisplayName ?? string.Empty,
            Target: ResolveTarget(),
            CronExpression: CronExpression,
            Timezone: Timezone ?? string.Empty,
            Enabled: Enabled,
            Headers: Headers ?? new Dictionary<string, string>(StringComparer.Ordinal));

    private ScheduledDispatchTargetDescriptor ResolveTarget()
    {
        var targetCount = (Envelope == null ? 0 : 1) +
                          (ServiceInvocation == null ? 0 : 1) +
                          (Workflow == null ? 0 : 1);
        if (targetCount != 1)
            throw new ArgumentException("Exactly one scheduled dispatch target is required.");

        if (Envelope != null)
            return Envelope.ToTarget();
        if (ServiceInvocation != null)
            return ServiceInvocation.ToTarget();

        return Workflow!.ToTarget();
    }
}

public sealed record ScheduledDispatchEnvelopeTargetHttpRequest
{
    public string? ActorId { get; init; }
    public required EventEnvelope Envelope { get; init; }

    public ScheduledDispatchTargetDescriptor ToTarget() =>
        new(
            ScheduledDispatchTargetKind.Envelope,
            ActorId: ActorId,
            Envelope: Envelope);
}

public sealed record ScheduledDispatchServiceInvocationTargetHttpRequest
{
    public required ServiceIdentity Identity { get; init; }
    public required string EndpointId { get; init; }
    public required Any Payload { get; init; }
    public string? RevisionId { get; init; }
    public ServiceInvocationCaller? Caller { get; init; }

    public ScheduledDispatchTargetDescriptor ToTarget() =>
        new(
            ScheduledDispatchTargetKind.ServiceInvocation,
            ServiceInvocation: new ScheduledServiceInvocationTargetDescriptor(
                Identity,
                EndpointId,
                Payload,
                RevisionId,
                Caller));
}

public sealed record ScheduledDispatchWorkflowTargetHttpRequest
{
    public required string WorkflowName { get; init; }
    public required string Prompt { get; init; }
    public string? ScopeId { get; init; }
    public string? SourceActorId { get; init; }

    public ScheduledDispatchTargetDescriptor ToTarget() =>
        new(
            ScheduledDispatchTargetKind.Workflow,
            Workflow: new WorkflowScheduleTargetDescriptor(
                WorkflowName,
                Prompt,
                ScopeId ?? string.Empty,
                SourceActorId ?? string.Empty));
}

public sealed record ScheduledDispatchPreviewHttpRequest
{
    public required string CronExpression { get; init; }
    public string? Timezone { get; init; }
    public int Count { get; init; } = 5;
    public DateTimeOffset? FromUtc { get; init; }
}

public sealed record ScheduledDispatchStateChangeHttpRequest
{
    public string? Reason { get; init; }
}
