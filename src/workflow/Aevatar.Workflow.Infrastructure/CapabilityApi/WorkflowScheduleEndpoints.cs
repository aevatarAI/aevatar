using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.Workflow.Application.Abstractions.Schedules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

public static class WorkflowScheduleEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapPost("/workflow-schedules", Create)
            .WithTags("Workflow schedules")
            .Produces<WorkflowScheduleMutationReceipt>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status400BadRequest);
        group.MapPut("/workflow-schedules/{scheduleId}", Update)
            .WithTags("Workflow schedules")
            .Produces<WorkflowScheduleMutationReceipt>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status400BadRequest);
        group.MapPost("/workflow-schedules/{scheduleId}/enable", Enable)
            .WithTags("Workflow schedules")
            .Produces<WorkflowScheduleMutationReceipt>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);
        group.MapPost("/workflow-schedules/{scheduleId}/disable", Disable)
            .WithTags("Workflow schedules")
            .Produces<WorkflowScheduleMutationReceipt>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);
        group.MapGet("/workflow-schedules", List)
            .WithTags("Workflow schedules")
            .Produces<WorkflowScheduleListResult>(StatusCodes.Status200OK);
        group.MapGet("/workflow-schedules/{scheduleId}", Get)
            .WithTags("Workflow schedules")
            .Produces<WorkflowScheduleDetail>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);
        group.MapPost("/workflow-schedules/preview", Preview)
            .WithTags("Workflow schedules")
            .Produces<WorkflowSchedulePreview>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest);
        group.MapPost("/workflow-schedules/{scheduleId}/run-now", RunNow)
            .WithTags("Workflow schedules")
            .Produces<WorkflowScheduleRunNowReceipt>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);
    }

    internal static async Task<IResult> Create(
        WorkflowScheduleConfigurationHttpRequest input,
        IWorkflowScheduleApplicationService schedules,
        CancellationToken ct = default)
    {
        try
        {
            var receipt = await schedules.CreateAsync(input.ToConfiguration(input.ScheduleId), ct);
            return Results.Accepted($"/api/workflow-schedules/{receipt.ScheduleId}", receipt);
        }
        catch (Exception ex) when (TryMapScheduleMutationError(ex, out var result))
        {
            return result;
        }
    }

    internal static async Task<IResult> Update(
        string scheduleId,
        WorkflowScheduleConfigurationHttpRequest input,
        IWorkflowScheduleApplicationService schedules,
        CancellationToken ct = default)
    {
        try
        {
            var receipt = await schedules.UpdateAsync(scheduleId, input.ToConfiguration(scheduleId), ct);
            return Results.Accepted($"/api/workflow-schedules/{receipt.ScheduleId}", receipt);
        }
        catch (Exception ex) when (TryMapScheduleMutationError(ex, out var result))
        {
            return result;
        }
    }

    internal static async Task<IResult> Enable(
        string scheduleId,
        WorkflowScheduleStateChangeHttpRequest? input,
        IWorkflowScheduleApplicationService schedules,
        CancellationToken ct = default)
    {
        try
        {
            var receipt = await schedules.EnableAsync(scheduleId, input?.Reason ?? string.Empty, ct);
            return Results.Accepted($"/api/workflow-schedules/{receipt.ScheduleId}", receipt);
        }
        catch (Exception ex) when (TryMapScheduleMutationError(ex, out var result))
        {
            return result;
        }
    }

    internal static async Task<IResult> Disable(
        string scheduleId,
        WorkflowScheduleStateChangeHttpRequest? input,
        IWorkflowScheduleApplicationService schedules,
        CancellationToken ct = default)
    {
        try
        {
            var receipt = await schedules.DisableAsync(scheduleId, input?.Reason ?? string.Empty, ct);
            return Results.Accepted($"/api/workflow-schedules/{receipt.ScheduleId}", receipt);
        }
        catch (Exception ex) when (TryMapScheduleMutationError(ex, out var result))
        {
            return result;
        }
    }

    internal static async Task<IResult> List(
        IWorkflowScheduleApplicationService schedules,
        int take = 50,
        string? cursor = null,
        bool includeTotalCount = false,
        CancellationToken ct = default)
    {
        return Results.Ok(await schedules.ListAsync(take, cursor, includeTotalCount, ct));
    }

    internal static async Task<IResult> Get(
        string scheduleId,
        IWorkflowScheduleApplicationService schedules,
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
        WorkflowSchedulePreviewHttpRequest input,
        IWorkflowScheduleApplicationService schedules,
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
        IWorkflowScheduleApplicationService schedules,
        CancellationToken ct = default)
    {
        try
        {
            var receipt = await schedules.RunNowAsync(scheduleId, ct);
            return Results.Accepted($"/api/workflow-schedules/{receipt.ScheduleId}", receipt);
        }
        catch (Exception ex) when (TryMapScheduleMutationError(ex, out var result))
        {
            return result;
        }
    }

    private static bool TryMapScheduleMutationError(Exception ex, out IResult result)
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

public sealed record WorkflowScheduleConfigurationHttpRequest
{
    public string? ScheduleId { get; init; }
    public string? DisplayName { get; init; }
    public required string WorkflowName { get; init; }
    public required string Prompt { get; init; }
    public required string CronExpression { get; init; }
    public string? Timezone { get; init; }
    public bool Enabled { get; init; } = true;
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
    public string? ScopeId { get; init; }
    public WorkflowScheduleAuthHttpRequest? Auth { get; init; }

    public WorkflowScheduleConfiguration ToConfiguration(string? fallbackScheduleId) =>
        new(
            ScheduleId: string.IsNullOrWhiteSpace(ScheduleId) ? fallbackScheduleId ?? string.Empty : ScheduleId,
            DisplayName: DisplayName ?? string.Empty,
            WorkflowName: WorkflowName,
            Prompt: Prompt,
            CronExpression: CronExpression,
            Timezone: Timezone ?? string.Empty,
            Enabled: Enabled,
            Headers: Headers ?? new Dictionary<string, string>(StringComparer.Ordinal),
            ScopeId: ScopeId,
            Auth: Auth?.ToAuth());
}

public sealed record WorkflowScheduleAuthHttpRequest
{
    public WorkflowScheduleNyxIdCredentialSourceHttpRequest? SenderNyxId { get; init; }

    public WorkflowScheduleAuth ToAuth()
    {
        if (SenderNyxId == null)
            throw new ArgumentException("Sender NyxID credential source is required.", nameof(SenderNyxId));

        return new WorkflowScheduleAuth(SenderNyxId.ToSource());
    }
}

public sealed record WorkflowScheduleNyxIdCredentialSourceHttpRequest
{
    public required WorkflowScheduleNyxIdSubjectRefHttpRequest Subject { get; init; }
    public required string Scope { get; init; }

    public WorkflowScheduleNyxIdCredentialSource ToSource() =>
        new(NormalizeSubject(Subject), NormalizeRequired(Scope, nameof(Scope)));

    private static WorkflowScheduleNyxIdSubjectRef NormalizeSubject(
        WorkflowScheduleNyxIdSubjectRefHttpRequest? subject)
    {
        if (subject == null)
            throw new ArgumentException("Subject is required.", nameof(Subject));

        return subject.ToSubject();
    }

    private static string NormalizeRequired(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{name} is required.", name);

        return value.Trim();
    }
}

public sealed record WorkflowScheduleNyxIdSubjectRefHttpRequest
{
    public required string Platform { get; init; }
    public string? Tenant { get; init; }
    public required string ExternalUserId { get; init; }

    public WorkflowScheduleNyxIdSubjectRef ToSubject() =>
        new(
            NormalizeRequired(Platform, nameof(Platform)).ToLowerInvariant(),
            NormalizeOptional(Tenant),
            NormalizeRequired(ExternalUserId, nameof(ExternalUserId)));

    private static string NormalizeRequired(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{name} is required.", name);

        return value.Trim();
    }

    private static string NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
}

public sealed record WorkflowSchedulePreviewHttpRequest
{
    public required string CronExpression { get; init; }
    public string? Timezone { get; init; }
    public int Count { get; init; } = 5;
    public DateTimeOffset? FromUtc { get; init; }
}

public sealed record WorkflowScheduleStateChangeHttpRequest
{
    public string? Reason { get; init; }
}
