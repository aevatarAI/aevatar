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

        app.MapMethods("/ai-assets/{**path}", [HttpMethods.Get, HttpMethods.Head], GetAsset).AllowAnonymous();
        app.MapMethods("/ai", [HttpMethods.Get, HttpMethods.Head], GetPage).AllowAnonymous();
        app.MapMethods("/ai/{**path}", [HttpMethods.Get, HttpMethods.Head], GetPage).AllowAnonymous();
        app.MapMethods("/chat", [HttpMethods.Get, HttpMethods.Head], GetPage).AllowAnonymous();
        app.MapMethods("/login", [HttpMethods.Get, HttpMethods.Head], GetPage).AllowAnonymous();
        app.MapMethods("/auth/callback", [HttpMethods.Get, HttpMethods.Head], GetPage).AllowAnonymous();
        app.MapMethods("/scopes", [HttpMethods.Get, HttpMethods.Head], GetPage).AllowAnonymous();
        app.MapMethods("/scopes/{**path}", [HttpMethods.Get, HttpMethods.Head], GetPage).AllowAnonymous();
        app.MapMethods("/settings", [HttpMethods.Get, HttpMethods.Head], GetPage).AllowAnonymous();

        var api = app.MapGroup("/api/ai")
            .WithTags("AIWorkspace")
            .RequireAuthorization();
        Audit(api.MapGet("/context", GetContext), "context");
        Audit(api.MapGet("/overview", GetOverviewAsync), "overview");
        Audit(api.MapGet("/agents", GetAgentsAsync), "agents");
        Audit(api.MapGet("/models", GetModelsAsync), "models");
        api.MapAIWorkspaceActivityEndpoints();
        return app;
    }

    private static IResult GetPage(
        HttpContext http,
        [FromServices] IAIWorkspaceWebAssetService assets) =>
        assets.ServePage(http);

    private static IResult GetAsset(
        HttpContext http,
        [FromRoute] string? path,
        [FromServices] IAIWorkspaceWebAssetService assets) =>
        assets.ServeAsset(http, path);

    private static IResult GetContext(HttpContext http)
    {
        if (!TryGetScopeId(http, out var scopeId, out var error))
            return error;

        var encodedScopeId = Uri.EscapeDataString(scopeId);
        return Results.Ok(new AIWorkspaceContextResponse(
            scopeId,
            IndependentReadModels,
            new AIWorkspacePageLinks(
                "/ai",
                "/ai/chat",
                "/ai/agents",
                "/ai/models"),
            new AIWorkspaceApiLinks(
                "/api/ai/overview",
                "/api/chat",
                "/api/ai/agents",
                $"/api/scopes/{encodedScopeId}/agent-profiles",
                "/api/agent-profiles/system",
                "/api/ai/models",
                "/api/user-config/llm",
                $"/api/scopes/{encodedScopeId}/llm-model-catalog",
                "/api/ai/activity",
                "/api/ai/activity/conversations",
                "/api/ai/activity/runs"),
            new AIWorkspaceFeaturesResponse(
                new AIWorkspaceFeatureResponse("available", "/ai", "/api/ai/overview"),
                new AIWorkspaceFeatureResponse("available", "/ai/chat", "/api/chat"),
                new AIWorkspaceFeatureResponse("available", "/ai/agents", "/api/ai/agents"),
                new AIWorkspaceFeatureResponse("available", "/ai/models", "/api/ai/models"))));
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
            "AI_SCOPE_REQUIRED",
            "A single authenticated scope is required.");
        return false;
    }

    internal static IResult ToResult<T>(AIWorkspaceQueryResult<T> result)
    {
        if (result.Value is not null)
            return Results.Ok(result.Value);

        var failure = result.Failure ?? new AIWorkspaceQueryFailure(
            AIWorkspaceQueryFailureKind.Unavailable,
            "AI_WORKSPACE_UNAVAILABLE",
            "AI workspace data is temporarily unavailable.");
        var statusCode = failure.Kind switch
        {
            AIWorkspaceQueryFailureKind.InvalidInput => StatusCodes.Status400BadRequest,
            AIWorkspaceQueryFailureKind.InvalidCursor => StatusCodes.Status400BadRequest,
            AIWorkspaceQueryFailureKind.NotFound => StatusCodes.Status404NotFound,
            _ => StatusCodes.Status503ServiceUnavailable,
        };
        return Error(statusCode, failure.Code, failure.Message);
    }

    internal static IResult Error(int statusCode, string code, string message) =>
        Results.Json(new AIWorkspaceErrorResponse(code, message), statusCode: statusCode);

    internal static void Audit(RouteHandlerBuilder builder, string operation) =>
        builder.WithEndpointAudit(
            $"ai-workspace.{operation}",
            AuditSensitivityLevel.Confidential,
            "ai-workspace",
            EndpointAuditTargetResolvers.Static("ai-workspace", "caller-scope"));

    private static string? BearerToken(HttpContext http)
    {
        var value = http.Request.Headers.Authorization.ToString().Trim();
        const string prefix = "Bearer ";
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;

        var token = value[prefix.Length..].Trim();
        return token.Length == 0 ? null : token;
    }
}

internal sealed record AIWorkspaceContextResponse(
    string ScopeId,
    string Consistency,
    AIWorkspacePageLinks Pages,
    [property: JsonPropertyName("apis")] AIWorkspaceApiLinks APIs,
    AIWorkspaceFeaturesResponse Features);

internal sealed record AIWorkspacePageLinks(
    string Overview,
    string Chat,
    string Agents,
    string Models);

internal sealed record AIWorkspaceApiLinks(
    string Overview,
    string Chat,
    string Agents,
    string OwnedAgentProfiles,
    string SystemAgentProfiles,
    string Models,
    string PersonalModelSettings,
    string ScopeModelCatalog,
    string Activity,
    string Conversations,
    string Runs);

internal sealed record AIWorkspaceFeaturesResponse(
    AIWorkspaceFeatureResponse Overview,
    AIWorkspaceFeatureResponse Chat,
    AIWorkspaceFeatureResponse Agents,
    AIWorkspaceFeatureResponse Models);

internal sealed record AIWorkspaceFeatureResponse(
    string Availability,
    string Page,
    string? API);

internal sealed record AIWorkspaceErrorResponse(string Code, string Message);
