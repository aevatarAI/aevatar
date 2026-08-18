using Aevatar.AIWorkspace.Application.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Aevatar.Mainnet.Host.Api.AI;

internal static class AIWorkspaceActivityEndpoints
{
    private const int DefaultPageSize = 50;

    public static RouteGroupBuilder MapAIWorkspaceActivityEndpoints(this RouteGroupBuilder api)
    {
        var activity = api.MapGet("/activity", GetActivityAsync);
        var conversations = api.MapGet("/activity/conversations", GetConversationsAsync);
        var runs = api.MapGet("/activity/runs", GetRunsAsync);
        var run = api.MapGet("/activity/runs/{runId}", GetRunAsync);
        AIWorkspaceEndpoints.Audit(activity, "activity");
        AIWorkspaceEndpoints.Audit(conversations, "activity.conversations");
        AIWorkspaceEndpoints.Audit(runs, "activity.runs");
        AIWorkspaceEndpoints.Audit(run, "activity.run");
        return api;
    }

    private static async Task<IResult> GetActivityAsync(
        HttpContext http,
        [FromServices] IAIWorkspaceActivityQueryService service,
        int? take,
        string? conversationCursor,
        string? runCursor,
        CancellationToken ct)
    {
        if (!AIWorkspaceEndpoints.TryGetScopeId(http, out var scopeId, out var error))
            return error;

        return AIWorkspaceEndpoints.ToResult(await service.QueryAsync(
            scopeId,
            new AIWorkspaceActivityQuery(take ?? DefaultPageSize, conversationCursor, runCursor),
            ct).ConfigureAwait(false));
    }

    private static async Task<IResult> GetConversationsAsync(
        HttpContext http,
        [FromServices] IAIWorkspaceActivityQueryService service,
        int? take,
        string? cursor,
        CancellationToken ct)
    {
        if (!AIWorkspaceEndpoints.TryGetScopeId(http, out var scopeId, out var error))
            return error;

        var result = await service.QueryConversationsAsync(
            scopeId,
            new AIWorkspacePageQuery(take ?? DefaultPageSize, cursor),
            ct).ConfigureAwait(false);
        if (result.Value is { Availability: AIWorkspaceSourceAvailability.Unavailable } unavailable)
            return Results.Json(unavailable, statusCode: StatusCodes.Status503ServiceUnavailable);
        return AIWorkspaceEndpoints.ToResult(result);
    }

    private static async Task<IResult> GetRunsAsync(
        HttpContext http,
        [FromServices] IAIWorkspaceActivityQueryService service,
        string? status,
        string? origins,
        string? workflowId,
        string? q,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int? take,
        string? cursor,
        bool includeTotalCount = false,
        CancellationToken ct = default)
    {
        if (!AIWorkspaceEndpoints.TryGetScopeId(http, out var scopeId, out var error))
            return error;

        var result = await service.QueryRunsAsync(
            scopeId,
            new AIWorkspaceRunsQuery(
                status,
                SplitCsv(origins),
                workflowId,
                q,
                from,
                to,
                take ?? DefaultPageSize,
                cursor,
                includeTotalCount),
            ct).ConfigureAwait(false);
        if (result.Value is { Availability: AIWorkspaceSourceAvailability.Unavailable } unavailable)
            return Results.Json(unavailable, statusCode: StatusCodes.Status503ServiceUnavailable);
        return AIWorkspaceEndpoints.ToResult(result);
    }

    private static async Task<IResult> GetRunAsync(
        HttpContext http,
        string runId,
        [FromServices] IAIWorkspaceActivityQueryService service,
        CancellationToken ct)
    {
        if (!AIWorkspaceEndpoints.TryGetScopeId(http, out var scopeId, out var error))
            return error;

        return AIWorkspaceEndpoints.ToResult(
            await service.GetRunAsync(scopeId, runId, ct).ConfigureAwait(false));
    }

    private static IReadOnlyList<string> SplitCsv(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
}
