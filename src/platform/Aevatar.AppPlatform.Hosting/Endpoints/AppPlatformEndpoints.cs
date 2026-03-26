using Aevatar.AppPlatform.Abstractions;
using Aevatar.AppPlatform.Abstractions.Access;
using Aevatar.AppPlatform.Abstractions.Ports;
using Aevatar.AppPlatform.Application.Services;
using Aevatar.Authentication.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace Aevatar.AppPlatform.Hosting.Endpoints;

public static class AppPlatformEndpoints
{
    public static IEndpointRouteBuilder MapAppPlatformEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/apps");
        group.MapGet(string.Empty, HandleListAppsAsync);
        group.MapGet("/resolve", HandleResolveRouteAsync);
        group.MapGet("/{appId}", HandleGetAppAsync);
        group.MapGet("/{appId}/releases", HandleListReleasesAsync);
        group.MapGet("/{appId}/releases/{releaseId}", HandleGetReleaseAsync);
        group.MapGet("/{appId}/routes", HandleListRoutesAsync);
        return app;
    }

    private static Task<IReadOnlyList<AppDefinitionSnapshot>> HandleListAppsAsync(
        HttpContext http,
        [AsParameters] AppPlatformEndpointModels.AppListQuery query,
        [FromServices] IAppDefinitionQueryPort queryPort,
        [FromServices] IAppAccessAuthorizer authorizer,
        CancellationToken ct) =>
        HandleListAppsAsyncCore(http, query, queryPort, authorizer, ct);

    private static async Task<IResult> HandleGetAppAsync(
        HttpContext http,
        string appId,
        [FromServices] IAppDefinitionQueryPort queryPort,
        [FromServices] IAppAccessAuthorizer authorizer,
        CancellationToken ct)
    {
        var snapshot = await queryPort.GetAsync(appId, ct);
        if (snapshot == null)
            return Results.NotFound();

        var decision = await authorizer.AuthorizeAsync(
            new AppAccessRequest(
                ResolveSubjectScopeId(http.User),
                AppAccessActions.Read,
                AppDefinitionQueryApplicationService.BuildAccessResource(snapshot)),
            ct);
        return decision.Allowed
            ? Results.Ok(snapshot)
            : ToDeniedResult(http.User, decision);
    }

    private static Task<IResult> HandleListReleasesAsync(
        HttpContext http,
        string appId,
        [FromServices] IAppReleaseQueryPort queryPort,
        [FromServices] IAppDefinitionQueryPort appQueryPort,
        [FromServices] IAppAccessAuthorizer authorizer,
        CancellationToken ct) =>
        HandleListReleasesAsyncCore(http, appId, queryPort, appQueryPort, authorizer, ct);

    private static async Task<IResult> HandleGetReleaseAsync(
        HttpContext http,
        string appId,
        string releaseId,
        [FromServices] IAppReleaseQueryPort queryPort,
        [FromServices] IAppDefinitionQueryPort appQueryPort,
        [FromServices] IAppAccessAuthorizer authorizer,
        CancellationToken ct)
    {
        var app = await appQueryPort.GetAsync(appId, ct);
        if (app == null)
            return Results.NotFound();

        var decision = await authorizer.AuthorizeAsync(
            new AppAccessRequest(
                ResolveSubjectScopeId(http.User),
                AppAccessActions.Read,
                AppDefinitionQueryApplicationService.BuildAccessResource(app)),
            ct);
        if (!decision.Allowed)
            return ToDeniedResult(http.User, decision);

        var snapshot = await queryPort.GetAsync(appId, releaseId, ct);
        return snapshot == null ? Results.NotFound() : Results.Ok(snapshot);
    }

    private static Task<IResult> HandleListRoutesAsync(
        HttpContext http,
        string appId,
        [FromServices] IAppRouteQueryPort queryPort,
        [FromServices] IAppDefinitionQueryPort appQueryPort,
        [FromServices] IAppAccessAuthorizer authorizer,
        CancellationToken ct) =>
        HandleListRoutesAsyncCore(http, appId, queryPort, appQueryPort, authorizer, ct);

    private static async Task<IResult> HandleResolveRouteAsync(
        HttpContext http,
        [AsParameters] AppPlatformEndpointModels.ResolveRouteQuery query,
        [FromServices] IAppRouteQueryPort queryPort,
        [FromServices] IAppAccessAuthorizer authorizer,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query.RoutePath))
            return Results.BadRequest(new { error = "routePath is required." });

        var resolution = await queryPort.ResolveAsync(query.RoutePath, ct);
        if (resolution == null)
            return Results.NotFound();

        var decision = await authorizer.AuthorizeAsync(
            new AppAccessRequest(
                ResolveSubjectScopeId(http.User),
                AppAccessActions.Read,
                AppDefinitionQueryApplicationService.BuildAccessResource(resolution.App)),
            ct);
        return decision.Allowed
            ? Results.Ok(resolution)
            : ToDeniedResult(http.User, decision);
    }

    private static async Task<IReadOnlyList<AppDefinitionSnapshot>> HandleListAppsAsyncCore(
        HttpContext http,
        AppPlatformEndpointModels.AppListQuery query,
        IAppDefinitionQueryPort queryPort,
        IAppAccessAuthorizer authorizer,
        CancellationToken ct)
    {
        var subjectScopeId = ResolveSubjectScopeId(http.User);
        var apps = await queryPort.ListAsync(query.OwnerScopeId, ct);

        var results = new List<AppDefinitionSnapshot>(apps.Count);
        foreach (var app in apps)
        {
            var decision = await authorizer.AuthorizeAsync(
                new AppAccessRequest(
                    subjectScopeId,
                    AppAccessActions.Read,
                    AppDefinitionQueryApplicationService.BuildAccessResource(app)),
                ct);
            if (decision.Allowed)
                results.Add(app);
        }

        return results;
    }

    private static async Task<IResult> HandleListRoutesAsyncCore(
        HttpContext http,
        string appId,
        IAppRouteQueryPort queryPort,
        IAppDefinitionQueryPort appQueryPort,
        IAppAccessAuthorizer authorizer,
        CancellationToken ct)
    {
        var app = await appQueryPort.GetAsync(appId, ct);
        if (app == null)
            return Results.NotFound();

        var decision = await authorizer.AuthorizeAsync(
            new AppAccessRequest(
                ResolveSubjectScopeId(http.User),
                AppAccessActions.Read,
                AppDefinitionQueryApplicationService.BuildAccessResource(app)),
            ct);
        if (!decision.Allowed)
            return ToDeniedResult(http.User, decision);

        var routes = await queryPort.ListAsync(appId, ct);
        return Results.Ok(routes);
    }

    private static async Task<IResult> HandleListReleasesAsyncCore(
        HttpContext http,
        string appId,
        IAppReleaseQueryPort queryPort,
        IAppDefinitionQueryPort appQueryPort,
        IAppAccessAuthorizer authorizer,
        CancellationToken ct)
    {
        var app = await appQueryPort.GetAsync(appId, ct);
        if (app == null)
            return Results.NotFound();

        var decision = await authorizer.AuthorizeAsync(
            new AppAccessRequest(
                ResolveSubjectScopeId(http.User),
                AppAccessActions.Read,
                AppDefinitionQueryApplicationService.BuildAccessResource(app)),
            ct);
        if (!decision.Allowed)
            return ToDeniedResult(http.User, decision);

        var releases = await queryPort.ListAsync(appId, ct);
        return Results.Ok(releases);
    }

    private static IResult ToDeniedResult(ClaimsPrincipal principal, AppAccessDecision decision)
    {
        if (principal.Identity?.IsAuthenticated != true)
        {
            return Results.Json(new
            {
                code = "APP_ACCESS_UNAUTHORIZED",
                message = decision.Reason,
            }, statusCode: StatusCodes.Status401Unauthorized);
        }

        return Results.Json(new
        {
            code = "APP_ACCESS_DENIED",
            message = decision.Reason,
        }, statusCode: StatusCodes.Status403Forbidden);
    }

    private static string ResolveSubjectScopeId(ClaimsPrincipal principal)
    {
        var scopeId = principal.FindFirst(AevatarStandardClaimTypes.ScopeId)?.Value;
        if (!string.IsNullOrWhiteSpace(scopeId))
            return scopeId.Trim();

        return principal.FindFirst("uid")?.Value?.Trim()
               ?? principal.FindFirst("sub")?.Value?.Trim()
               ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value?.Trim()
               ?? string.Empty;
    }
}
