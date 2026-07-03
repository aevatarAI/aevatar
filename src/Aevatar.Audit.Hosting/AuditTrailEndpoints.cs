using Aevatar.Audit;
using Aevatar.Audit.Abstractions.Identity;
using Aevatar.Audit.Abstractions.Models;
using Aevatar.Audit.Abstractions.Ports;
using Aevatar.Authentication.Abstractions;
using Aevatar.Capabilities;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.Audit.Hosting;

public static class AuditTrailEndpoints
{
    private const string DataRoutePrefix = "/api/audit";
    private const string AuditLoggerCategory = "Aevatar.Audit.Reads";
    private const string AdminAccessLevel = "ADMIN";
    private const int DefaultTake = 100;
    private const int MaxTake = 500;

    public static IEndpointRouteBuilder MapAuditTrailEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var data = app.MapGroup(DataRoutePrefix).WithTags("AuditTrail");

        data.MapGet("/trail", QueryAuditTrail)
            .WithName("QueryAuditTrail")
            .WithSummary("Query audit trail records. Default scope is the caller scope; cross-scope reads require platform admin.")
            .RequireAuthorization()
            .WithMetadata(new AuditTrailEndpointAuditMetadata("audit-trail", "query-cross-scope", AdminAccessLevel));

        data.MapPost("/actor-resolutions", ResolveAuditActor)
            .WithName("ResolveAuditActor")
            .WithSummary("Admin-only: resolve an external actor identity to its server-side audit actor id.")
            .RequireAuthorization()
            .WithMetadata(new AuditTrailEndpointAuditMetadata("audit-trail", "resolve-actor", AdminAccessLevel));

        return app;
    }

    internal static async Task<IResult> QueryAuditTrail(
        HttpContext http,
        [FromServices] IServiceProvider services,
        [FromServices] ILoggerFactory loggerFactory,
        string? scope = null,
        string? auditActorId = null,
        string? identityKeyId = null,
        string? cursor = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        int take = DefaultTake,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(http);

        if (!AevatarScopeAccessGuard.TryGetCallerScopeId(http, out var callerScopeId))
            return Results.Unauthorized();

        var targetScope = NormalizeOptional(scope) ?? callerScopeId;
        var isCrossScope = !string.Equals(targetScope, callerScopeId, StringComparison.Ordinal);
        var logger = loggerFactory.CreateLogger(AuditLoggerCategory);
        if (isCrossScope)
        {
            if (services.GetService<IPlatformAdminAuthorizer>() is not { } authorizer)
                return AdminAuthorizationUnavailable();

            var denied = await AuthorizeAdminReadAsync(
                http,
                authorizer,
                logger,
                callerScopeId,
                targetScope,
                "query-cross-scope",
                ct);
            if (denied is not null)
                return denied;
        }

        if (services.GetService<IAuditTrailQueryPort>() is not { } queryPort)
            return QueryUnavailable();

        var query = new AuditTrailQuery
        {
            ScopeId = targetScope,
            AuditActorId = NormalizeOptional(auditActorId),
            IdentityKeyId = NormalizeOptional(identityKeyId),
            Cursor = NormalizeOptional(cursor),
            OccurredFrom = from,
            OccurredTo = to,
            Take = NormalizeTake(take),
        };
        var result = await queryPort.QueryAsync(query, ct);
        return Results.Json(ToResponse(result));
    }

    internal static async Task<IResult> ResolveAuditActor(
        HttpContext http,
        [FromServices] IServiceProvider services,
        [FromServices] ILoggerFactory loggerFactory,
        [FromBody] AuditActorResolutionRequest? request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(http);

        if (!AevatarScopeAccessGuard.TryGetCallerScopeId(http, out var callerScopeId))
            return Results.Unauthorized();

        var logger = loggerFactory.CreateLogger(AuditLoggerCategory);
        if (services.GetService<IPlatformAdminAuthorizer>() is not { } authorizer)
            return AdminAuthorizationUnavailable();

        var denied = await AuthorizeAdminReadAsync(
            http,
            authorizer,
            logger,
            callerScopeId,
            targetScope: callerScopeId,
            action: "resolve-actor",
            ct);
        if (denied is not null)
            return denied;

        var provider = NormalizeOptional(request?.Provider);
        var subject = NormalizeOptional(request?.Subject);
        if (provider is null || subject is null)
            return Results.Json(
                new { code = "AUDIT_ACTOR_IDENTITY_REQUIRED", message = "Provider and subject are required." },
                statusCode: StatusCodes.Status400BadRequest);

        if (services.GetService<IAuditActorIdentityHasher>() is not { } hasher)
            return HasherUnavailable();

        if (!TryBuildCanonicalActorKey(provider, subject, out var canonicalActorKey))
            return Results.Json(
                new
                {
                    code = "AUDIT_ACTOR_IDENTITY_INVALID",
                    message = "Provider and subject must be non-empty canonical key segments."
                },
                statusCode: StatusCodes.Status400BadRequest);

        var identity = hasher.Hash(canonicalActorKey);
        return Results.Json(new AuditActorResolutionResponse(
            identity.AuditActorId,
            identity.IdentityKeyId,
            DateTimeOffset.UtcNow));
    }

    private static async Task<IResult?> AuthorizeAdminReadAsync(
        HttpContext http,
        IPlatformAdminAuthorizer authorizer,
        ILogger logger,
        string callerScopeId,
        string targetScope,
        string action,
        CancellationToken ct)
    {
        if (!TryGetBearer(http, out var token))
        {
            Audit(logger, http, "denied", action, callerScopeId, targetScope, PlatformCaller.NotElevated, "missing_bearer");
            return Results.Unauthorized();
        }

        var caller = await authorizer.ResolveCallerAsync(token, ct);
        if (!caller.IsElevated)
        {
            Audit(logger, http, "denied", action, callerScopeId, targetScope, caller, "not_admin_or_disabled");
            return Results.Json(
                new { code = "SCOPE_ACCESS_DENIED", message = "Platform admin role required for audit reads." },
                statusCode: StatusCodes.Status403Forbidden);
        }

        Audit(logger, http, "allowed", action, callerScopeId, targetScope, caller, reason: null);
        return null;
    }

    private static AuditTrailReadResponse ToResponse(AuditTrailPage result) =>
        new(
            result.Records.Select(static record => new AuditTrailRecordResponse(
                    record.AuditId,
                    record.ScopeId,
                    record.AuditActorId,
                    record.IdentityKeyId,
                    record.OperationName,
                    record.Outcome.ToString(),
                    ToDateTimeOffset(record.OccurredAt),
                    NormalizeOptional(record.Target?.Kind),
                    NormalizeOptional(record.Target?.Id),
                    NormalizeOptional(record.Correlation?.RequestId) ??
                    NormalizeOptional(record.Correlation?.TraceId)))
                .ToArray(),
            result.ReadAt,
            result.Watermark,
            result.NextCursor);

    private static IResult QueryUnavailable() =>
        Results.Json(
            new { code = "AUDIT_QUERY_UNAVAILABLE", message = "Audit trail query port is not configured." },
            statusCode: StatusCodes.Status503ServiceUnavailable);

    private static IResult AdminAuthorizationUnavailable() =>
        Results.Json(
            new { code = "AUDIT_ADMIN_AUTH_UNAVAILABLE", message = "Audit admin authorization is not configured." },
            statusCode: StatusCodes.Status503ServiceUnavailable);

    private static IResult HasherUnavailable() =>
        Results.Json(
            new { code = "AUDIT_ACTOR_HASHER_UNAVAILABLE", message = "Audit actor identity hasher is not configured." },
            statusCode: StatusCodes.Status503ServiceUnavailable);

    private static int NormalizeTake(int take)
    {
        if (take <= 0)
            return DefaultTake;

        return Math.Min(take, MaxTake);
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static DateTimeOffset ToDateTimeOffset(Timestamp? timestamp) =>
        timestamp is null ? DateTimeOffset.UnixEpoch : timestamp.ToDateTimeOffset();

    private static bool TryBuildCanonicalActorKey(
        string provider,
        string subject,
        out string canonicalActorKey)
    {
        canonicalActorKey = string.Empty;

        var normalizedProvider = NormalizeCanonicalKeySegment(provider, lowerCase: true);
        var normalizedSubject = NormalizeCanonicalKeySegment(subject, lowerCase: false);
        if (normalizedProvider is null || normalizedSubject is null)
            return false;

        canonicalActorKey = $"{normalizedProvider}:{normalizedSubject}";
        return true;
    }

    private static string? NormalizeCanonicalKeySegment(string value, bool lowerCase)
    {
        var normalized = NormalizeOptional(value);
        if (normalized is null || normalized.Contains(':', StringComparison.Ordinal))
            return null;

        return lowerCase ? normalized.ToLowerInvariant() : normalized;
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

    private static void Audit(
        ILogger logger,
        HttpContext http,
        string outcome,
        string action,
        string callerScope,
        string targetScope,
        PlatformCaller admin,
        string? reason) =>
        logger.LogInformation(
            "audit_trail_admin_read outcome={Outcome} action={Action} adminUserId={AdminUserId} adminEmail={AdminEmail} " +
            "role={Role} callerScope={CallerScope} targetScope={TargetScope} reason={Reason} correlationId={CorrelationId}",
            outcome,
            action,
            admin.UserId,
            admin.Email,
            admin.Role,
            callerScope,
            targetScope,
            reason ?? string.Empty,
            http.TraceIdentifier);
}
