using System.Security.Claims;
using System.Text.Json.Serialization;
using Aevatar.AIWorkspace.Application.Abstractions;
using Aevatar.Audit;
using Aevatar.Audit.Hosting.EndpointAudit;
using Aevatar.Capabilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Aevatar.Mainnet.Host.Api.AI;

internal static class AIWorkspaceEndpoints
{
    private const string IndependentReadModels = "independent_read_models";
    private const int DefaultPageSize = 50;
    private const int DefaultOverviewPageSize = 5;

    public static IEndpointRouteBuilder MapAIWorkspaceEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var api = app.MapGroup("/api/ai")
            .WithTags("AIWorkspace")
            .RequireAuthorization();
        Audit(api.MapGet("/context", GetContext), "context");
        Audit(api.MapGet("/overview", GetOverviewAsync), "overview");
        Audit(api.MapGet("/agents", GetAgentsAsync), "agents");
        Audit(api.MapGet("/models", GetModelsAsync), "models");
        api.MapAIWorkspaceModelsManagementEndpoints();
        api.MapAIWorkspaceActivityEndpoints();
        return app;
    }

    private static IResult GetContext(HttpContext http)
    {
        if (!TryGetScopeId(http, out _, out var error))
            return error;
        if (!TryGetSubject(http, out var subject))
        {
            return Error(
                StatusCodes.Status403Forbidden,
                "AI_SUBJECT_REQUIRED",
                "Authenticated caller subject is required.");
        }

        return Results.Ok(new AIWorkspaceContextResponse(
            new AIWorkspaceAccountResponse(subject, DisplayName(http, subject)),
            IndependentReadModels,
            new AIWorkspacePageLinks(
                "/ai#/overview",
                "/ai#/agents",
                "/ai#/models",
                "/ai#/activity"),
            new AIWorkspaceApiLinks(
                "/api/ai/overview",
                "/api/ai/agents",
                "/api/ai/models",
                "/api/ai/models/personal-default",
                "/api/ai/models/catalog",
                "/api/ai/models/catalog/candidates",
                "/api/ai/activity",
                "/api/ai/activity/conversations",
                "/api/ai/activity/runs"),
            new AIWorkspaceCapabilitiesResponse(
                new AIWorkspaceCapabilityResponse("/ai#/overview", "/api/ai/overview"),
                new AIWorkspaceCapabilityResponse("/ai#/agents", "/api/ai/agents"),
                new AIWorkspaceCapabilityResponse("/ai#/models", "/api/ai/models"),
                new AIWorkspaceCapabilityResponse("/ai#/activity", "/api/ai/activity"))));
    }

    private static async Task<IResult> GetOverviewAsync(
        HttpContext http,
        [FromServices] IAIWorkspaceOverviewQueryService service,
        int? take,
        CancellationToken ct)
    {
        if (!TryGetScopeId(http, out var scopeId, out var error))
            return error;

        return ToResult(await service.QueryAsync(
            scopeId,
            take ?? DefaultOverviewPageSize,
            ct).ConfigureAwait(false));
    }

    private static async Task<IResult> GetAgentsAsync(
        HttpContext http,
        [FromServices] IAIWorkspaceAgentsQueryService service,
        string? ownedCursor,
        string? systemCursor,
        int? take,
        CancellationToken ct)
    {
        if (!TryGetScopeId(http, out var scopeId, out var error))
            return error;

        return ToResult(await service.QueryAsync(
            scopeId,
            new AIWorkspaceAgentsQuery(ownedCursor, systemCursor, take ?? DefaultPageSize),
            ct).ConfigureAwait(false));
    }

    private static async Task<IResult> GetModelsAsync(
        HttpContext http,
        [FromServices] IAIWorkspaceModelsQueryService service,
        CancellationToken ct)
    {
        if (!TryGetScopeId(http, out var scopeId, out var error))
            return error;

        return Results.Ok(await service.QueryAsync(scopeId, BearerToken(http), ct).ConfigureAwait(false));
    }

    internal static bool TryGetScopeId(HttpContext http, out string scopeId, out IResult error)
    {
        if (AevatarScopeAccessGuard.TryGetCallerScopeId(http, out scopeId))
        {
            error = Results.Empty;
            return true;
        }

        error = Error(
            http.User.Identity?.IsAuthenticated == true
                ? StatusCodes.Status403Forbidden
                : StatusCodes.Status401Unauthorized,
            "AI_ACCESS_CONTEXT_REQUIRED",
            "Authenticated caller access context is required.");
        return false;
    }

    internal static IResult ToResult<T>(AIWorkspaceQueryResult<T> result)
    {
        if (result.Value is not null)
            return Results.Ok(result.Value);

        return (result.Failure?.Kind ?? AIWorkspaceQueryFailureKind.Unavailable) switch
        {
            AIWorkspaceQueryFailureKind.InvalidInput => Error(
                StatusCodes.Status400BadRequest,
                "AI_REQUEST_INVALID",
                "AI workspace request is invalid."),
            AIWorkspaceQueryFailureKind.InvalidCursor => Error(
                StatusCodes.Status400BadRequest,
                "AI_CURSOR_INVALID",
                "AI workspace cursor is invalid."),
            AIWorkspaceQueryFailureKind.NotFound => Error(
                StatusCodes.Status404NotFound,
                "AI_RESOURCE_NOT_FOUND",
                "AI workspace resource was not found."),
            _ => Error(
                StatusCodes.Status503ServiceUnavailable,
                "AI_WORKSPACE_UNAVAILABLE",
                "AI workspace data is temporarily unavailable."),
        };
    }

    internal static IResult Error(int statusCode, string code, string message) =>
        Results.Json(new AIWorkspaceErrorResponse(code, message), statusCode: statusCode);

    internal static void Audit(RouteHandlerBuilder builder, string operation) =>
        builder.WithEndpointAudit(
            $"ai-workspace.{operation}",
            AuditSensitivityLevel.Confidential,
            "ai-workspace",
            EndpointAuditTargetResolvers.Static("ai-workspace", "caller-access"));

    private static string? BearerToken(HttpContext http)
    {
        var value = http.Request.Headers.Authorization.ToString().Trim();
        const string prefix = "Bearer ";
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;

        var token = value[prefix.Length..].Trim();
        return token.Length == 0 ? null : token;
    }

    private static bool TryGetSubject(HttpContext http, out string subject)
    {
        subject = FirstClaimValue(http, "uid", "sub", ClaimTypes.NameIdentifier) ?? string.Empty;
        return subject.Length > 0;
    }

    private static string DisplayName(HttpContext http, string subject) =>
        FirstClaimValue(http, "preferred_username", ClaimTypes.Name, ClaimTypes.Email) ?? subject;

    private static string? FirstClaimValue(HttpContext http, params string[] claimTypes) =>
        claimTypes
            .Select(type => http.User.FindFirst(type)?.Value?.Trim())
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
}

internal sealed record AIWorkspaceContextResponse(
    AIWorkspaceAccountResponse Account,
    string Consistency,
    AIWorkspacePageLinks Pages,
    [property: JsonPropertyName("apis")] AIWorkspaceApiLinks APIs,
    AIWorkspaceCapabilitiesResponse Capabilities);

internal sealed record AIWorkspaceAccountResponse(string Subject, string DisplayName);

internal sealed record AIWorkspacePageLinks(
    string Overview,
    string Agents,
    string Models,
    string Activity);

internal sealed record AIWorkspaceApiLinks(
    string Overview,
    string Agents,
    string Models,
    string PersonalModelDefault,
    string ModelCatalog,
    string ModelCandidates,
    string Activity,
    string Conversations,
    string Runs);

internal sealed record AIWorkspaceCapabilitiesResponse(
    AIWorkspaceCapabilityResponse Overview,
    AIWorkspaceCapabilityResponse Agents,
    AIWorkspaceCapabilityResponse Models,
    AIWorkspaceCapabilityResponse Activity);

internal sealed record AIWorkspaceCapabilityResponse(
    string Page,
    [property: JsonPropertyName("api")] string API);

internal sealed record AIWorkspaceErrorResponse(string Code, string Message);
