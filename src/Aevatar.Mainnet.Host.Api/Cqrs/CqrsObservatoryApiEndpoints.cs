using Aevatar.Authentication.Abstractions;
using Aevatar.Capabilities;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Aevatar.Mainnet.Host.Api.Cqrs;

// Read-only CQRS projection observatory data surface.
//   - GET-only + bearer (RequireAuthorization). There are no edit/run/stop controls.
//   - The projection-scope-status read-model is a PLATFORM-WIDE health view: its keys are projection
//     scope/actor ids (e.g. "workflow:execution:org-123"), not the caller's own tenant scope_id. There
//     is no per-caller projection scope to filter on, so this is inherently a cross-scope/platform read.
//     Following the run observatory's precedent (06-20-observatory-admin-cross-scope), it is gated on a
//     NyxID-verified platform admin/operator (IPlatformAdminAuthorizer -> /users/me). Admin status is
//     never self-asserted; a non-admin caller is denied (403) BEFORE any read-model query runs.
//   - Read-model only: the query port reads the materialized ProjectionScopeStatusDocument; it never
//     replays events or touches IEventStore (the platform-wide read-write-separation invariant).
public static class CqrsObservatoryApiEndpoints
{
    private const string DataRoutePrefix = "/api/cqrs";
    private const string AuditLoggerCategory = "Aevatar.Cqrs.Observatory.PlatformRead";

    public static IEndpointRouteBuilder MapCqrsObservatoryApiEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var data = app.MapGroup(DataRoutePrefix).WithTags("CqrsObservatory");

        data.MapGet("/scopes", ListScopes)
            .WithName("ListCqrsProjectionScopes")
            .WithSummary("List projection-scope statuses (version lag, active, failures). Platform admin only.")
            .RequireAuthorization();

        data.MapGet("/readmodels", ListReadModels)
            .WithName("ListCqrsReadModels")
            .WithSummary("List materialized read-models grouped by sink shape (version, freshness, count). Platform admin only.")
            .RequireAuthorization();

        return app;
    }

    internal static async Task<IResult> ListScopes(
        HttpContext http,
        [FromServices] IProjectionScopeStatusListQueryPort scopeStatuses,
        [FromServices] IPlatformAdminAuthorizer authorizer,
        [FromServices] ILoggerFactory loggerFactory,
        int take = 200,
        CancellationToken ct = default)
    {
        // Authenticated caller required (own scope claim must be unambiguous), then platform-admin
        // elevation for this platform-wide read. Fails closed at each step.
        if (!AevatarScopeAccessGuard.TryGetCallerScopeId(http, out var callerScopeId))
            return Results.Unauthorized();

        var logger = loggerFactory.CreateLogger(AuditLoggerCategory);

        if (!TryGetBearer(http, out var token))
        {
            Audit(logger, http, "denied", callerScopeId, PlatformCaller.NotElevated, "missing_bearer");
            return Results.Unauthorized();
        }

        var caller = await authorizer.ResolveCallerAsync(token, ct);
        if (!caller.IsElevated)
        {
            Audit(logger, http, "denied", callerScopeId, caller, "not_admin_or_disabled");
            return Results.Json(
                new { code = "SCOPE_ACCESS_DENIED", message = "Platform admin role required to view projection scopes." },
                statusCode: StatusCodes.Status403Forbidden);
        }

        Audit(logger, http, "allowed", callerScopeId, caller, reason: null);

        var snapshots = await scopeStatuses.ListAsync(new ProjectionScopeStatusListQuery { Take = take }, ct);
        return Results.Json(new
        {
            scopes = snapshots.Select(static snapshot => new
            {
                id = snapshot.ScopeActorId,
                active = snapshot.Active,
                observed = snapshot.LastObservedVersion,
                successful = snapshot.LastSuccessfulVersion,
                failures = snapshot.FailureCount,
            }),
        });
    }

    // Read-model inventory: materialized read-model types grouped by sink shape (doc/graph/mem) with each
    // one's freshness (max StateVersion), latest UpdatedAt, and total count. version/updated/count are
    // emitted as null when the backing store cannot cheaply provide them (never fabricated). Same auth as
    // /scopes: authenticated caller with an unambiguous scope, then platform-admin elevation, fail-closed.
    // The inventory port reads the materialized read-model stores only; it never touches IEventStore.
    internal static async Task<IResult> ListReadModels(
        HttpContext http,
        [FromServices] IProjectionReadModelInventoryQueryPort inventory,
        [FromServices] IPlatformAdminAuthorizer authorizer,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct = default)
    {
        if (!AevatarScopeAccessGuard.TryGetCallerScopeId(http, out var callerScopeId))
            return Results.Unauthorized();

        var logger = loggerFactory.CreateLogger(AuditLoggerCategory);

        if (!TryGetBearer(http, out var token))
        {
            Audit(logger, http, "denied", callerScopeId, PlatformCaller.NotElevated, "missing_bearer");
            return Results.Unauthorized();
        }

        var caller = await authorizer.ResolveCallerAsync(token, ct);
        if (!caller.IsElevated)
        {
            Audit(logger, http, "denied", callerScopeId, caller, "not_admin_or_disabled");
            return Results.Json(
                new { code = "SCOPE_ACCESS_DENIED", message = "Platform admin role required to view read-models." },
                statusCode: StatusCodes.Status403Forbidden);
        }

        Audit(logger, http, "allowed", callerScopeId, caller, reason: null);

        var result = await inventory.GetInventoryAsync(ct);
        return Results.Json(new
        {
            groups = result.Groups.Select(static group => new
            {
                shape = ToWireShape(group.Shape),
                engine = group.Engine,
                items = group.Items.Select(static item => new
                {
                    name = item.Name,
                    actor = item.Actor,
                    version = item.Version,
                    updated = item.Updated,
                    count = item.Count,
                }),
            }),
        });
    }

    private static string ToWireShape(ProjectionReadModelSinkShape shape) => shape switch
    {
        ProjectionReadModelSinkShape.Document => "doc",
        ProjectionReadModelSinkShape.Graph => "graph",
        ProjectionReadModelSinkShape.Memory => "mem",
        _ => "doc",
    };

    private static bool TryGetBearer(HttpContext http, out string token)
    {
        token = string.Empty;
        var header = http.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var value = header[prefix.Length..].Trim();
        if (value.Length == 0)
            return false;

        token = value;
        return true;
    }

    // Structured audit for platform-wide projection-scope reads. NEVER logs the bearer token.
    private static void Audit(
        ILogger logger,
        HttpContext http,
        string outcome,
        string callerScope,
        PlatformCaller admin,
        string? reason) =>
        logger.LogInformation(
            "cqrs_observatory_platform_read outcome={Outcome} adminUserId={AdminUserId} adminEmail={AdminEmail} " +
            "role={Role} callerScope={CallerScope} reason={Reason} correlationId={CorrelationId}",
            outcome, admin.UserId, admin.Email, admin.Role, callerScope, reason ?? string.Empty, http.TraceIdentifier);
}
