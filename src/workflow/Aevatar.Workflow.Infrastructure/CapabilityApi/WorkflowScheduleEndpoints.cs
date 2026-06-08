using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Abstractions.Schedules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

public static class WorkflowScheduleEndpoints
{
    public static IEndpointRouteBuilder MapWorkflowScheduleEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/workflow-schedules").WithTags("Workflow Schedules");

        group.MapPost("", HandleCreateAsync);
        group.MapGet("", HandleListAsync);
        group.MapGet("/{scheduleId}", HandleGetAsync);
        group.MapPut("/{scheduleId}", HandleUpdateAsync);
        group.MapPost("/{scheduleId}:enable", HandleEnableAsync);
        group.MapPost("/{scheduleId}:disable", HandleDisableAsync);
        group.MapPost("/preview", HandlePreviewAsync);
        group.MapPost("/{scheduleId}:run-now", HandleRunNowAsync);

        return app;
    }

    internal static async Task<IResult> HandleCreateAsync(
        WorkflowScheduleCreateInput input,
        [FromServices] IWorkflowScheduleApplicationService service,
        CancellationToken ct = default)
    {
        var target = NormalizeTarget(input.Target);
        if (target == null)
            return Results.BadRequest(new { error = "Schedule target is required." });

        var result = await service.CreateAsync(
            new WorkflowScheduleCreateCommand(
                input.ScheduleId,
                input.Name ?? string.Empty,
                input.Cron ?? string.Empty,
                input.Timezone ?? "UTC",
                target,
                input.Enabled),
            ct);
        if (!result.Succeeded)
            return MapError(result.Error);

        return Results.Created(
            $"/api/workflow-schedules/{Uri.EscapeDataString(result.Value!.ScheduleId)}",
            result.Value);
    }

    internal static async Task<IResult> HandleUpdateAsync(
        string scheduleId,
        WorkflowScheduleUpdateInput input,
        [FromServices] IWorkflowScheduleApplicationService service,
        CancellationToken ct = default)
    {
        var result = await service.UpdateAsync(
            scheduleId,
            new WorkflowScheduleUpdateCommand(
                input.Name,
                input.Cron,
                input.Timezone,
                input.Target == null ? null : NormalizeTarget(input.Target)),
            ct);
        return result.Succeeded ? Results.Ok(result.Value) : MapError(result.Error);
    }

    internal static async Task<IResult> HandleEnableAsync(
        string scheduleId,
        [FromServices] IWorkflowScheduleApplicationService service,
        CancellationToken ct = default)
    {
        var result = await service.EnableAsync(scheduleId, ct);
        return result.Succeeded ? Results.Ok(result.Value) : MapError(result.Error);
    }

    internal static async Task<IResult> HandleDisableAsync(
        string scheduleId,
        [FromServices] IWorkflowScheduleApplicationService service,
        CancellationToken ct = default)
    {
        var result = await service.DisableAsync(scheduleId, ct);
        return result.Succeeded ? Results.Ok(result.Value) : MapError(result.Error);
    }

    internal static async Task<IResult> HandleGetAsync(
        string scheduleId,
        [FromServices] IWorkflowScheduleApplicationService service,
        CancellationToken ct = default)
    {
        var result = await service.GetAsync(scheduleId, ct);
        return result.Succeeded ? Results.Ok(result.Value) : MapError(result.Error);
    }

    internal static async Task<IResult> HandleListAsync(
        [FromServices] IWorkflowScheduleApplicationService service,
        [FromQuery] string? status = null,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 100,
        CancellationToken ct = default)
    {
        var parsedStatus = ParseStatus(status);
        var result = await service.ListAsync(new WorkflowScheduleListQuery(parsedStatus, skip, take), ct);
        return Results.Ok(result);
    }

    internal static async Task<IResult> HandlePreviewAsync(
        WorkflowSchedulePreviewInput input,
        [FromServices] IWorkflowScheduleApplicationService service,
        CancellationToken ct = default)
    {
        var result = await service.PreviewAsync(
            input.Cron ?? string.Empty,
            input.Timezone ?? "UTC",
            input.FromUtc,
            input.Count,
            ct);
        return result.Succeeded ? Results.Ok(result.Value) : MapError(result.Error);
    }

    internal static async Task<IResult> HandleRunNowAsync(
        string scheduleId,
        WorkflowScheduleRunNowInput? input,
        [FromServices] IWorkflowScheduleApplicationService service,
        CancellationToken ct = default)
    {
        var result = await service.RunNowAsync(
            new WorkflowScheduleFireRequest(
                scheduleId,
                input?.ScheduledFireAtUtc,
                input?.Force ?? false),
            ct);
        if (!result.Succeeded)
            return MapError(result.Error);

        var value = result.Value!;
        return value.Status == WorkflowScheduleFireStatus.Accepted
            ? Results.Accepted(value.StatusUrl, value)
            : Results.Ok(value);
    }

    private static WorkflowScheduleTarget? NormalizeTarget(WorkflowScheduleTargetInput? input)
    {
        if (input == null)
            return null;

        return new WorkflowScheduleTarget(
            input.Prompt ?? string.Empty,
            NormalizeSource(input.Source),
            NormalizeOptional(input.SessionId),
            InputParts: null,
            Annotations: NormalizeMap(input.Annotations),
            ScopeId: NormalizeOptional(input.ScopeId),
            Headers: NormalizeMap(input.Headers));
    }

    private static WorkflowChatSource NormalizeSource(WorkflowChatSourceInput? input)
    {
        if (input == null)
            return WorkflowChatSource.Direct();

        var kind = NormalizeSourceKind(input.Kind);
        var workflowName = NormalizeOptional(input.WorkflowName);
        var actorId = NormalizeOptional(input.ActorId);
        var workflowYamls = input.WorkflowYamls?.Where(x => !string.IsNullOrWhiteSpace(x)).ToList() ?? [];
        return kind switch
        {
            WorkflowChatSourceKind.CatalogWorkflow =>
                WorkflowChatSource.CatalogWorkflow(workflowName ?? string.Empty),
            WorkflowChatSourceKind.DefinitionActor =>
                WorkflowChatSource.DefinitionActor(actorId ?? string.Empty, workflowName),
            WorkflowChatSourceKind.InlineYamlBundle =>
                WorkflowChatSource.InlineYamlBundle(workflowYamls, workflowName, actorId),
            WorkflowChatSourceKind.Direct =>
                WorkflowChatSource.Direct(actorId),
            _ => WorkflowChatSource.Direct(actorId),
        };
    }

    private static WorkflowChatSourceKind NormalizeSourceKind(string? kind) =>
        kind?.Trim().ToLowerInvariant() switch
        {
            "catalog_workflow" or "catalog-workflow" or "catalog" or "workflow" =>
                WorkflowChatSourceKind.CatalogWorkflow,
            "definition_actor" or "definition-actor" or "actor" =>
                WorkflowChatSourceKind.DefinitionActor,
            "inline_yaml_bundle" or "inline-yaml-bundle" or "inline_yaml" or "inline-yaml" =>
                WorkflowChatSourceKind.InlineYamlBundle,
            "direct" => WorkflowChatSourceKind.Direct,
            _ => WorkflowChatSourceKind.Direct,
        };

    private static WorkflowScheduleStatus? ParseStatus(string? status) =>
        System.Enum.TryParse<WorkflowScheduleStatus>(status, ignoreCase: true, out var parsed)
            ? parsed
            : null;

    private static IReadOnlyDictionary<string, string>? NormalizeMap(IDictionary<string, string>? source)
    {
        if (source is not { Count: > 0 })
            return null;

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in source)
        {
            var normalizedKey = NormalizeOptional(key);
            if (normalizedKey != null)
                result[normalizedKey] = value?.Trim() ?? string.Empty;
        }

        return result.Count == 0 ? null : result;
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static IResult MapError(WorkflowScheduleError error)
    {
        var statusCode = error.Code switch
        {
            WorkflowScheduleErrorCode.InvalidScheduleId or
                WorkflowScheduleErrorCode.InvalidName or
                WorkflowScheduleErrorCode.InvalidCron or
                WorkflowScheduleErrorCode.InvalidTimezone or
                WorkflowScheduleErrorCode.InvalidTarget => StatusCodes.Status400BadRequest,
            WorkflowScheduleErrorCode.NotFound => StatusCodes.Status404NotFound,
            WorkflowScheduleErrorCode.AlreadyExists => StatusCodes.Status409Conflict,
            WorkflowScheduleErrorCode.Disabled or
                WorkflowScheduleErrorCode.DispatchRejected => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status500InternalServerError,
        };
        return Results.Json(
            new
            {
                code = error.Code.ToString(),
                message = error.Message,
            },
            statusCode: statusCode);
    }
}
