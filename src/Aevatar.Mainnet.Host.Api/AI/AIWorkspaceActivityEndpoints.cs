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
        if (result.Value is { Availability: AIWorkspaceSourceAvailability.Unavailable })
        {
            return SourceUnavailable(
                "CONVERSATIONS_UNAVAILABLE",
                "Conversation activity is temporarily unavailable.");
        }
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

        if (!TryParseOrigins(origins, out var parsedOrigins))
        {
            return AIWorkspaceEndpoints.Error(
                StatusCodes.Status400BadRequest,
                "INVALID_ACTIVITY_ORIGIN",
                "Activity origins must be interactive, integration, automation, or development.");
        }

        var result = await service.QueryRunsAsync(
            scopeId,
            new AIWorkspaceRunsQuery(
                status,
                parsedOrigins,
                workflowId,
                q,
                from,
                to,
                take ?? DefaultPageSize,
                cursor,
                includeTotalCount),
            ct).ConfigureAwait(false);
        if (result.Value is { Availability: AIWorkspaceSourceAvailability.Unavailable })
        {
            return SourceUnavailable(
                "WORKFLOW_RUNS_UNAVAILABLE",
                "Workflow run activity is temporarily unavailable.");
        }
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

    private static bool TryParseOrigins(
        string? value,
        out IReadOnlyList<AIWorkspaceRunOriginFilter> origins)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            origins = [];
            return true;
        }

        var parsed = new List<AIWorkspaceRunOriginFilter>();
        foreach (var item in value.Split(
                     ',',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var origin = item.ToLowerInvariant() switch
            {
                "interactive" => AIWorkspaceRunOriginFilter.Interactive,
                "integration" => AIWorkspaceRunOriginFilter.Integration,
                "automation" => AIWorkspaceRunOriginFilter.Automation,
                "development" => AIWorkspaceRunOriginFilter.Development,
                _ => (AIWorkspaceRunOriginFilter?)null,
            };
            if (origin is null)
            {
                origins = [];
                return false;
            }

            if (!parsed.Contains(origin.Value))
                parsed.Add(origin.Value);
        }

        origins = parsed;
        return true;
    }

    private static IResult SourceUnavailable(
        string code,
        string message) =>
        AIWorkspaceEndpoints.Error(
            StatusCodes.Status503ServiceUnavailable,
            code,
            message);
}
