using Aevatar.Audit;
using Aevatar.Audit.Abstractions.Identity;
using Aevatar.Audit.Abstractions.Models;
using Aevatar.Audit.Abstractions.Ports;
using Aevatar.Authentication.Abstractions;
using Aevatar.Capabilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Aevatar.Audit.Hosting;

public static class AuditTrailEndpoints
{
    private const string DataRoutePrefix = "/api/audit";
    private const string AuditLoggerCategory = "Aevatar.Audit.Reads";
    private const string AdminAccessLevel = "ADMIN";
    private const int DefaultTake = 100;
    private const int MaxTake = 500;
    private const int DefaultChatActivityTake = 50;
    private const int MaxChatActivityTake = 200;

    // Cross-scope wildcard, mirroring the run observatory's AllScopesToken convention. When the
    // caller passes this value the audit read becomes a platform-admin-only aggregate query across
    // every scope (the underlying store matches ScopeId == null as "any scope"); any other value is
    // treated as a literal scope id.
    private const string AllScopesToken = "__all__";

    public static IEndpointRouteBuilder MapAuditTrailEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var data = app.MapGroup(DataRoutePrefix).WithTags("AuditTrail");

        data.MapGet("/trail", QueryAuditTrail)
            .WithName("QueryAuditTrail")
            .WithSummary("Query audit trail records. Default scope is the caller scope; cross-scope reads require aevatar admin access.")
            .RequireAuthorization()
            .WithMetadata(new AuditTrailEndpointAuditMetadata("audit-trail", "query-cross-scope", AdminAccessLevel));

        data.MapGet("/chat-activity", QueryChatActivity)
            .WithName("QueryChatActivity")
            .WithSummary("Query the caller's chat tool and browser-action activity; all-user reads require explicit aevatar admin access.")
            .RequireAuthorization();

        data.MapGet("/trail/cloudevents", ExportAuditTrailCloudEvents)
            .WithName("ExportAuditTrailCloudEvents")
            .WithSummary("Export audit trail records as a CloudEvents 1.0 JSON batch.")
            .RequireAuthorization()
            .WithMetadata(new AuditTrailEndpointAuditMetadata("audit-trail", "export-cross-scope", AdminAccessLevel));

        data.MapPost("/actor-resolutions", ResolveAuditActor)
            .WithName("ResolveAuditActor")
            .WithSummary("Admin-only: resolve an external actor identity to its server-side audit actor id.")
            .RequireAuthorization()
            .WithMetadata(new AuditTrailEndpointAuditMetadata("audit-trail", "resolve-actor", AdminAccessLevel));

        return app;
    }

    internal static async Task<IResult> QueryChatActivity(
        HttpContext http,
        [FromServices] AuditTrailEndpointDependencies dependencies,
        [FromServices] ILoggerFactory loggerFactory,
        string? cursor = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        int take = DefaultChatActivityTake,
        string? surface = null,
        string? conversationId = null,
        string? outcome = null,
        string? scope = null,
        string? auditActorId = null,
        string? identityKeyId = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(http);

        if (!AevatarScopeAccessGuard.TryGetCallerScopeId(http, out var callerScopeId) ||
            !AevatarPrincipalSubjectResolver.TryResolveNyxIdSubject(http.User, out var subject))
        {
            return Results.Unauthorized();
        }

        var normalizedScope = NormalizeOptional(scope);
        var allUsers = string.Equals(normalizedScope, AllScopesToken, StringComparison.Ordinal);
        if (scope is not null && !allUsers)
            return InvalidChatActivityFilter("scope is only accepted as '__all__'.");
        if (identityKeyId is not null)
            return InvalidChatActivityFilter("identityKeyId is not accepted.");
        if (!allUsers && auditActorId is not null)
            return InvalidChatActivityFilter("auditActorId is only accepted with scope=__all__.");
        if (!TryParseChatSurface(surface, out var chatSurface) ||
            !TryParseTerminalOutcome(outcome, out var terminalOutcome))
        {
            return InvalidChatActivityFilter("surface or outcome is invalid.");
        }

        var logger = loggerFactory.CreateLogger(AuditLoggerCategory);
        IReadOnlyList<string>? auditActorIds = null;
        if (allUsers)
        {
            if (dependencies.AdminAuthorizer is not { } authorizer)
                return AdminAuthorizationUnavailable();

            var denied = await AuthorizeAdminReadAsync(
                http,
                authorizer,
                logger,
                callerScopeId,
                AllScopesToken,
                "query-chat-activity-all-users",
                ct);
            if (denied is not null)
                return denied;
        }
        else
        {
            if (dependencies.ActorIdentityHasher is not { } hasher)
                return HasherUnavailable();
            if (!TryBuildCanonicalActorKey("nyxid", subject, out var canonicalActorKey))
                return Results.Unauthorized();

            try
            {
                auditActorIds = hasher.HashAll(canonicalActorKey)
                    .Select(static identity => identity.AuditActorId?.Trim())
                    .Where(static id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.Ordinal)
                    .Cast<string>()
                    .ToArray();
            }
            catch (Exception exception)
            {
                logger.LogError(
                    "Audit actor identity hashing unavailable. errorType={ErrorType} correlationId={CorrelationId}",
                    exception.GetType().Name,
                    http.TraceIdentifier);
                return HasherUnavailable();
            }

            if (auditActorIds.Count == 0)
                return HasherUnavailable();
        }

        if (dependencies.QueryPort is not { } queryPort)
            return QueryUnavailable();

        var query = new AuditTrailQuery
        {
            ScopeId = allUsers ? null : callerScopeId,
            AuditActorId = allUsers ? NormalizeOptional(auditActorId) : null,
            AuditActorIds = auditActorIds,
            RequireChatProvenance = true,
            ChatSurface = chatSurface,
            ChatConversationId = NormalizeOptional(conversationId),
            TerminalOutcome = terminalOutcome,
            Cursor = NormalizeOptional(cursor),
            OccurredFrom = from,
            OccurredTo = to,
            Take = NormalizeChatActivityTake(take),
        };

        try
        {
            var page = await queryPort.QueryAsync(query, ct);
            return Results.Json(AuditTrailResponseMapper.ToResponse(page));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(
                "Chat activity audit query unavailable. errorType={ErrorType} correlationId={CorrelationId}",
                exception.GetType().Name,
                http.TraceIdentifier);
            return QueryExecutionUnavailable();
        }
    }

    internal static async Task<IResult> QueryAuditTrail(
        HttpContext http,
        [FromServices] AuditTrailEndpointDependencies dependencies,
        [FromServices] ILoggerFactory loggerFactory,
        string? scope = null,
        string? auditActorId = null,
        string? identityKeyId = null,
        string? cursor = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        int take = DefaultTake,
        string? commandId = null,
        string? workflowRunId = null,
        AuditLifecyclePhase? lifecyclePhase = null,
        AuditTerminalOutcome? terminalOutcome = null,
        string? correlationId = null,
        CancellationToken ct = default)
    {
        return await QueryAuditTrailCore(
            http,
            dependencies,
            loggerFactory,
            scope,
            auditActorId,
            identityKeyId,
            cursor,
            from,
            to,
            take,
            commandId,
            workflowRunId,
            lifecyclePhase,
            terminalOutcome,
            correlationId,
            exportCloudEvents: false,
            ct: ct);
    }

    internal static async Task<IResult> ExportAuditTrailCloudEvents(
        HttpContext http,
        [FromServices] AuditTrailEndpointDependencies dependencies,
        [FromServices] ILoggerFactory loggerFactory,
        string? scope = null,
        string? auditActorId = null,
        string? identityKeyId = null,
        string? cursor = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        int take = DefaultTake,
        string? commandId = null,
        string? workflowRunId = null,
        AuditLifecyclePhase? lifecyclePhase = null,
        AuditTerminalOutcome? terminalOutcome = null,
        string? correlationId = null,
        CancellationToken ct = default)
    {
        return await QueryAuditTrailCore(
            http,
            dependencies,
            loggerFactory,
            scope,
            auditActorId,
            identityKeyId,
            cursor,
            from,
            to,
            take,
            commandId,
            workflowRunId,
            lifecyclePhase,
            terminalOutcome,
            correlationId,
            exportCloudEvents: true,
            ct: ct);
    }

    private static async Task<IResult> QueryAuditTrailCore(
        HttpContext http,
        AuditTrailEndpointDependencies dependencies,
        ILoggerFactory loggerFactory,
        string? scope,
        string? auditActorId,
        string? identityKeyId,
        string? cursor,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int take,
        string? commandId,
        string? workflowRunId,
        AuditLifecyclePhase? lifecyclePhase,
        AuditTerminalOutcome? terminalOutcome,
        string? correlationId,
        bool exportCloudEvents,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);

        if (!AevatarScopeAccessGuard.TryGetCallerScopeId(http, out var callerScopeId))
            return Results.Unauthorized();

        var normalizedScope = NormalizeOptional(scope);
        var isAllScopes = string.Equals(normalizedScope, AllScopesToken, StringComparison.Ordinal);
        var targetScope = isAllScopes ? AllScopesToken : (normalizedScope ?? callerScopeId);
        // "__all__" and any literal scope id other than the caller's own are both cross-scope reads
        // and require platform admin; the difference is only whether the store filters by one scope
        // or matches ScopeId == null as a wildcard across every scope.
        var isCrossScope = !string.Equals(targetScope, callerScopeId, StringComparison.Ordinal);
        var logger = loggerFactory.CreateLogger(AuditLoggerCategory);
        if (isCrossScope)
        {
            if (dependencies.AdminAuthorizer is not { } authorizer)
                return AdminAuthorizationUnavailable();

            var denied = await AuthorizeAdminReadAsync(
                http,
                authorizer,
                logger,
                callerScopeId,
                targetScope,
                exportCloudEvents ? "export-cross-scope" : "query-cross-scope",
                ct);
            if (denied is not null)
                return denied;
        }

        if (dependencies.QueryPort is not { } queryPort)
            return QueryUnavailable();

        var query = new AuditTrailQuery
        {
            // "__all__" collapses to a null scope so the store matches records from any scope.
            ScopeId = isAllScopes ? null : targetScope,
            AuditActorId = NormalizeOptional(auditActorId),
            IdentityKeyId = NormalizeOptional(identityKeyId),
            Cursor = NormalizeOptional(cursor),
            OccurredFrom = from,
            OccurredTo = to,
            CommandId = NormalizeOptional(commandId),
            WorkflowRunId = NormalizeOptional(workflowRunId),
            LifecyclePhase = lifecyclePhase,
            TerminalOutcome = terminalOutcome,
            CorrelationId = NormalizeOptional(correlationId),
            Take = NormalizeTake(take),
        };
        AuditTrailPage result;
        try
        {
            result = await queryPort.QueryAsync(query, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(
                "Audit trail query unavailable. errorType={ErrorType} correlationId={CorrelationId}",
                exception.GetType().Name,
                http.TraceIdentifier);
            return QueryExecutionUnavailable();
        }

        if (!exportCloudEvents)
            return Results.Json(AuditTrailResponseMapper.ToResponse(result));

        SetExportCoverageHeaders(http, result);
        return Results.Json(
            AuditTrailResponseMapper.ToCloudEvents(result),
            contentType: AuditTrailResponseMapper.CloudEventsBatchContentType);
    }

    internal static async Task<IResult> ResolveAuditActor(
        HttpContext http,
        [FromServices] AuditTrailEndpointDependencies dependencies,
        [FromServices] ILoggerFactory loggerFactory,
        [FromBody] AuditActorResolutionRequest? request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(http);

        if (!AevatarScopeAccessGuard.TryGetCallerScopeId(http, out var callerScopeId))
            return Results.Unauthorized();

        var logger = loggerFactory.CreateLogger(AuditLoggerCategory);
        if (dependencies.AdminAuthorizer is not { } authorizer)
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

        if (dependencies.ActorIdentityHasher is not { } hasher)
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
                new { code = "SCOPE_ACCESS_DENIED", message = "Aevatar admin access required for audit reads." },
                statusCode: StatusCodes.Status403Forbidden);
        }

        Audit(logger, http, "allowed", action, callerScopeId, targetScope, caller, reason: null);
        return null;
    }

    private static IResult QueryUnavailable() =>
        Results.Json(
            new { code = "AUDIT_QUERY_UNAVAILABLE", message = "Audit trail query port is not configured." },
            statusCode: StatusCodes.Status503ServiceUnavailable);

    private static IResult QueryExecutionUnavailable() =>
        Results.Json(
            new { code = "AUDIT_QUERY_UNAVAILABLE", message = "Audit trail query is temporarily unavailable." },
            statusCode: StatusCodes.Status503ServiceUnavailable);

    private static IResult AdminAuthorizationUnavailable() =>
        Results.Json(
            new { code = "AUDIT_ADMIN_AUTH_UNAVAILABLE", message = "Audit admin authorization is not configured." },
            statusCode: StatusCodes.Status503ServiceUnavailable);

    private static IResult HasherUnavailable() =>
        Results.Json(
            new { code = "AUDIT_ACTOR_HASHER_UNAVAILABLE", message = "Audit actor identity hasher is not configured." },
            statusCode: StatusCodes.Status503ServiceUnavailable);

    private static IResult InvalidChatActivityFilter(string message) =>
        Results.Json(
            new { code = "AUDIT_CHAT_ACTIVITY_FILTER_INVALID", message },
            statusCode: StatusCodes.Status400BadRequest);

    private static int NormalizeTake(int take)
    {
        if (take <= 0)
            return DefaultTake;

        return Math.Min(take, MaxTake);
    }

    private static int NormalizeChatActivityTake(int take)
    {
        return take <= 0 ? DefaultChatActivityTake : Math.Min(take, MaxChatActivityTake);
    }

    private static bool TryParseChatSurface(string? value, out AuditChatSurface? surface)
    {
        surface = NormalizeOptional(value)?.ToLowerInvariant() switch
        {
            null => null,
            "nyxid_assistant" => AuditChatSurface.NyxidAssistant,
            "workflow_chat" => AuditChatSurface.WorkflowChat,
            _ => (AuditChatSurface?)AuditChatSurface.Unspecified,
        };
        if (surface != AuditChatSurface.Unspecified)
            return true;

        surface = null;
        return false;
    }

    private static bool TryParseTerminalOutcome(string? value, out AuditTerminalOutcome? outcome)
    {
        outcome = NormalizeOptional(value)?.ToLowerInvariant() switch
        {
            null => null,
            "succeeded" => AuditTerminalOutcome.Succeeded,
            "failed" => AuditTerminalOutcome.Failed,
            "cancelled" => AuditTerminalOutcome.Cancelled,
            "timed_out" => AuditTerminalOutcome.TimedOut,
            _ => (AuditTerminalOutcome?)AuditTerminalOutcome.Unspecified,
        };
        if (outcome != AuditTerminalOutcome.Unspecified)
            return true;

        outcome = null;
        return false;
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static void SetExportCoverageHeaders(HttpContext http, AuditTrailPage page)
    {
        var coverage = AuditTrailResponseMapper.ToCoverageResponse(page);
        http.Response.Headers["Aevatar-Audit-Truncated"] = coverage.Truncated ? "true" : "false";
        http.Response.Headers["Aevatar-Audit-Window-Completeness"] = coverage.WindowCompleteness;
        http.Response.Headers["Aevatar-Audit-Schema-Compatibility"] = coverage.SchemaCompatibility;
        if (!string.IsNullOrWhiteSpace(page.NextCursor))
            http.Response.Headers["Aevatar-Audit-Continuation-Cursor"] = page.NextCursor;
        if (page.Coverage.IngestionWatermark.HasValue)
            http.Response.Headers["Aevatar-Audit-Ingestion-Watermark"] = page.Coverage.IngestionWatermark.Value.ToString("O");
        if (page.Coverage.CompleteThrough.HasValue)
            http.Response.Headers["Aevatar-Audit-Complete-Through"] = page.Coverage.CompleteThrough.Value.ToString("O");
    }

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
            "audit_trail_admin_read outcome={Outcome} action={Action} adminUserId={AdminUserId} " +
            "role={Role} grantSource={GrantSource} callerScope={CallerScope} targetScope={TargetScope} reason={Reason} correlationId={CorrelationId}",
            outcome,
            action,
            admin.UserId,
            admin.Role,
            admin.GrantSource,
            callerScope,
            targetScope,
            reason ?? string.Empty,
            http.TraceIdentifier);
}

internal sealed class AuditTrailEndpointDependencies
{
    public AuditTrailEndpointDependencies(
        IEnumerable<IAuditTrailQueryPort> queryPorts,
        IEnumerable<IPlatformAdminAuthorizer> adminAuthorizers,
        IEnumerable<IAuditActorIdentityHasher> actorIdentityHashers)
    {
        QueryPort = queryPorts.SingleOrDefault();
        AdminAuthorizer = adminAuthorizers.SingleOrDefault();
        ActorIdentityHasher = actorIdentityHashers.SingleOrDefault();
    }

    public IAuditTrailQueryPort? QueryPort { get; }

    public IPlatformAdminAuthorizer? AdminAuthorizer { get; }

    public IAuditActorIdentityHasher? ActorIdentityHasher { get; }
}
