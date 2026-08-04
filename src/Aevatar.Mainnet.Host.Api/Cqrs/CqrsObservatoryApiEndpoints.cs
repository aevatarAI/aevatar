using Aevatar.Audit;
using Aevatar.Audit.Hosting.EndpointAudit;
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
//     Following the run observatory's precedent, it is gated on
//     aevatar admin access. Admin status is
//     never self-asserted; a non-admin caller is denied (403) BEFORE any read-model query runs.
//   - Read-model only: the query port reads the materialized ProjectionScopeStatusDocument; it never
//     replays events or touches IEventStore (the platform-wide read-write-separation invariant).
public static class CqrsObservatoryApiEndpoints
{
    private const string DataRoutePrefix = "/api/cqrs";

    public static IEndpointRouteBuilder MapCqrsObservatoryApiEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var data = app.MapGroup(DataRoutePrefix).WithTags("CqrsObservatory");

        data.MapGet("/scopes", ListScopes)
            .WithName("ListCqrsProjectionScopes")
            .WithSummary("List projection-scope processing and unresolved-failure statuses. Aevatar admin only.")
            .WithEndpointAudit(
                "cqrs.observatory.list-scopes",
                AuditSensitivityLevel.Confidential,
                "cqrs-projection-scopes",
                EndpointAuditTargetResolvers.Static("cqrs-projection-scopes", "platform"))
            .RequireAuthorization();

        data.MapGet("/scopes/{scopeActorId}", GetScope)
            .WithName("GetCqrsProjectionScope")
            .WithSummary("Get one materialized projection-scope status. Aevatar admin only.")
            .WithEndpointAudit(
                "cqrs.observatory.get-scope",
                AuditSensitivityLevel.Confidential,
                "cqrs-projection-scope",
                EndpointAuditTargetResolvers.FromRouteValue("cqrs-projection-scope", "scopeActorId"))
            .RequireAuthorization();

        data.MapGet("/scopes/{scopeActorId}/recent-envelopes", ListRecentEnvelopes)
            .WithName("ListCqrsProjectionScopeRecentEnvelopes")
            .WithSummary("List payload-free committed-envelope metadata for one projection scope. Aevatar admin only.")
            .WithEndpointAudit(
                "cqrs.observatory.list-recent-envelopes",
                AuditSensitivityLevel.Confidential,
                "cqrs-projection-scope",
                EndpointAuditTargetResolvers.FromRouteValue("cqrs-projection-scope", "scopeActorId"))
            .RequireAuthorization();

        data.MapGet("/readmodels", ListReadModels)
            .WithName("ListCqrsReadModels")
            .WithSummary("List materialized read-models grouped by sink shape (version, freshness, count). Aevatar admin only.")
            .WithEndpointAudit(
                "cqrs.observatory.list-readmodels",
                AuditSensitivityLevel.Confidential,
                "cqrs-readmodels",
                EndpointAuditTargetResolvers.Static("cqrs-readmodels", "platform"))
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
        // Authenticated caller required (own scope claim must be unambiguous), then aevatar admin
        // elevation for this platform-wide read. Fails closed at each step.
        if (!AevatarScopeAccessGuard.TryGetCallerScopeId(http, out var callerScopeId))
            return Results.Unauthorized();

        if (!TryGetBearer(http, out var token))
        {
            return Results.Unauthorized();
        }

        var caller = await authorizer.ResolveCallerAsync(token, ct);
        if (!caller.IsElevated)
        {
            return Results.Json(
                new { code = "SCOPE_ACCESS_DENIED", message = "Aevatar admin access required to view projection scopes." },
                statusCode: StatusCodes.Status403Forbidden);
        }

        var snapshots = await scopeStatuses.ListAsync(new ProjectionScopeStatusListQuery { Take = take }, ct);
        return Results.Json(new
        {
            scopes = snapshots.Select(static snapshot => new
            {
                id = snapshot.ScopeActorId,
                active = snapshot.Active,
                receivedEnvelopeTotal = snapshot.ReceivedEnvelopeTotal,
                attemptedEnvelopeTotal = snapshot.AttemptedEnvelopeTotal,
                successfulMaterializationTotal = snapshot.SuccessfulMaterializationTotal,
                failedAttemptTotal = snapshot.FailedAttemptTotal,
                retryExhaustedTotal = snapshot.RetryExhaustedTotal,
                retryExhaustedFailureCount = snapshot.RetryExhaustedFailureCount,
                unresolvedFailureCount = snapshot.UnresolvedFailureCount,
                oldestUnresolvedFailureAt = snapshot.OldestUnresolvedFailureAt,
                failureDiagnosticDroppedTotal = snapshot.FailureDiagnosticDroppedTotal,
                sourceActorCount = snapshot.SourceActorCount,
                singleSourceVersionGap = snapshot.SingleSourceVersionGap,
            }),
        });
    }

    internal static async Task<IResult> GetScope(
        HttpContext http,
        string scopeActorId,
        [FromServices] IProjectionScopeIntrospectionQueryPort introspection,
        [FromServices] IPlatformAdminAuthorizer authorizer,
        CancellationToken ct = default)
    {
        var denied = await AuthorizeAdminAsync(http, authorizer, ct);
        if (denied != null)
            return denied;

        var snapshot = await introspection.GetAsync(scopeActorId, ct);
        return snapshot == null
            ? Results.NotFound()
            : Results.Json(new
            {
                snapshot.ScopeActorId,
                snapshot.RootActorId,
                snapshot.ProjectionKind,
                snapshot.SessionId,
                mode = snapshot.Mode.ToString(),
                snapshot.Active,
                snapshot.ObservationAttached,
                snapshot.Released,
                snapshot.StateVersion,
                snapshot.ReceivedEnvelopeTotal,
                snapshot.AttemptedEnvelopeTotal,
                snapshot.SuccessfulMaterializationTotal,
                snapshot.FailedAttemptTotal,
                snapshot.RetryExhaustedTotal,
                snapshot.RetryExhaustedFailureCount,
                snapshot.UnresolvedFailureCount,
                snapshot.OldestUnresolvedFailureAt,
                snapshot.FailureDiagnosticDroppedTotal,
                snapshot.SourceVersions,
                snapshot.UpdatedAt,
            });
    }

    internal static async Task<IResult> ListRecentEnvelopes(
        HttpContext http,
        string scopeActorId,
        [FromServices] IProjectionScopeIntrospectionQueryPort introspection,
        [FromServices] IPlatformAdminAuthorizer authorizer,
        int take = 20,
        CancellationToken ct = default)
    {
        var denied = await AuthorizeAdminAsync(http, authorizer, ct);
        if (denied != null)
            return denied;

        var envelopes = await introspection.ListRecentEnvelopesAsync(scopeActorId, take, ct);
        return Results.Json(new
        {
            scopeActorId,
            envelopes = envelopes.Select(static envelope => new
            {
                envelope.EventId,
                envelope.TypeUrl,
                envelope.StateVersion,
                envelope.TimestampUtc,
            }),
        });
    }

    // Read-model inventory: materialized read-model types grouped by sink shape (doc/graph/mem) with each
    // one's freshness (max StateVersion), latest UpdatedAt, and total count. version/updated/count are
    // emitted as null when the backing store cannot cheaply provide them (never fabricated). Same auth as
    // /scopes: authenticated caller with an unambiguous scope, then aevatar admin elevation, fail-closed.
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

        if (!TryGetBearer(http, out var token))
        {
            return Results.Unauthorized();
        }

        var caller = await authorizer.ResolveCallerAsync(token, ct);
        if (!caller.IsElevated)
        {
            return Results.Json(
                new { code = "SCOPE_ACCESS_DENIED", message = "Aevatar admin access required to view read-models." },
                statusCode: StatusCodes.Status403Forbidden);
        }

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

    private static async Task<IResult?> AuthorizeAdminAsync(
        HttpContext http,
        IPlatformAdminAuthorizer authorizer,
        CancellationToken ct)
    {
        if (!AevatarScopeAccessGuard.TryGetCallerScopeId(http, out _) || !TryGetBearer(http, out var token))
            return Results.Unauthorized();

        var caller = await authorizer.ResolveCallerAsync(token, ct);
        return caller.IsElevated
            ? null
            : Results.Json(
                new
                {
                    code = "SCOPE_ACCESS_DENIED",
                    message = "Aevatar admin access required to inspect projection scopes.",
                },
                statusCode: StatusCodes.Status403Forbidden);
    }

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

}
