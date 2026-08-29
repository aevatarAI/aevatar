using System.Security.Cryptography;
using System.Text;
using Aevatar.Audit;
using Aevatar.Audit.Hosting.EndpointAudit;
using Aevatar.Authentication.Abstractions;
using Aevatar.Capabilities;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Microsoft.AspNetCore.Mvc;

namespace Aevatar.Mainnet.Host.Api.Cqrs;

internal static class CqrsProjectionFailureReplayAdminEndpoints
{
    internal const string Route = "/api/cqrs/scopes/{scopeActorId}/failures:replay-exhausted";

    public static IEndpointRouteBuilder MapCqrsProjectionFailureRepairAdminEndpoints(
        this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost(Route, HandleRouteAsync)
            .WithTags("CqrsProjectionRepairAdmin")
            .WithName("ReplayCqrsProjectionRetryExhaustedFailures")
            .WithSummary(
                "Dispatch a manifest-fenced retry for exhausted failures in one projection scope. Aevatar admin only.")
            .WithEndpointAudit(
                "cqrs.projection-failures.replay-exhausted",
                AuditSensitivityLevel.Restricted,
                "cqrs-projection-scope",
                EndpointAuditTargetResolvers.FromRouteValue("cqrs-projection-scope", "scopeActorId"),
                requestSanitizer: SanitizeRequest)
            .RequireAuthorization();
        return app;
    }

    internal static async Task<IResult> HandleAsync(
        HttpContext http,
        string scopeActorId,
        ReplayRetryExhaustedFailuresRequest? request,
        IPlatformAdminAuthorizer? authorizer,
        IProjectionRetryExhaustedFailureRepairService? repairService,
        CancellationToken ct,
        ILogger? logger = null)
    {
        var authorization = await AuthorizeAsync(http, authorizer, ct);
        if (authorization.Error != null)
            return authorization.Error;
        if (repairService == null)
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        if (request == null)
            return InvalidRequest();

        ProjectionRetryExhaustedFailureRepairResult result;
        try
        {
            result = await repairService.RepairAsync(
                new ProjectionRetryExhaustedFailureRepairRequest(
                    scopeActorId,
                    request.ExpectedScopeStateVersion,
                    request.ExpectedUnresolvedFailureCount,
                    request.ExpectedRetryExhaustedFailureCount,
                    request.MaxItems,
                    request.RequestId,
                    request.Reason,
                    authorization.Caller!.UserId),
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var requestIdDigest = IsValidRequestId(request.RequestId)
                ? BuildRequestIdDigest(request.RequestId)
                : "invalid";
            logger?.LogError(
                ex,
                "Projection retry-exhausted repair failed. scopeActorId={ScopeActorId} requestSha256={RequestSha256}",
                scopeActorId,
                requestIdDigest);
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        return result.Status switch
        {
            ProjectionRetryExhaustedFailureRepairStatus.AcceptedForDispatch =>
                Results.Json(
                    new AcceptedResponse(
                        "accepted_for_dispatch",
                        result.ScopeActorId,
                        result.RequestId,
                        result.CurrentScopeStateVersion,
                        result.MaxItems),
                    statusCode: StatusCodes.Status202Accepted),
            ProjectionRetryExhaustedFailureRepairStatus.InvalidRequest => InvalidRequest(),
            ProjectionRetryExhaustedFailureRepairStatus.ScopeNotFound => Results.NotFound(),
            ProjectionRetryExhaustedFailureRepairStatus.ScopeNotActive =>
                ManifestConflict("projection_scope_not_active", result),
            ProjectionRetryExhaustedFailureRepairStatus.ScopeIdentityInvalid =>
                ManifestConflict("projection_scope_identity_invalid", result),
            ProjectionRetryExhaustedFailureRepairStatus.ScopeIdentityMismatch =>
                ManifestConflict("projection_scope_identity_mismatch", result),
            ProjectionRetryExhaustedFailureRepairStatus.ManifestChanged =>
                ManifestConflict("projection_scope_manifest_changed", result),
            ProjectionRetryExhaustedFailureRepairStatus.RecoveryIdentityUnavailable =>
                ManifestConflict("projection_scope_recovery_identity_unavailable", result),
            _ => Results.StatusCode(StatusCodes.Status503ServiceUnavailable),
        };
    }

    private static Task<IResult> HandleRouteAsync(
        HttpContext http,
        string scopeActorId,
        [FromBody] ReplayRetryExhaustedFailuresRequest? request,
        CancellationToken ct) =>
        HandleAsync(
            http,
            scopeActorId,
            request,
            http.RequestServices.GetService<IPlatformAdminAuthorizer>(),
            http.RequestServices.GetService<IProjectionRetryExhaustedFailureRepairService>(),
            ct,
            http.RequestServices.GetService<ILoggerFactory>()?.CreateLogger(
                typeof(CqrsProjectionFailureReplayAdminEndpoints).FullName!));

    private static async Task<AuthorizationResult> AuthorizeAsync(
        HttpContext http,
        IPlatformAdminAuthorizer? authorizer,
        CancellationToken ct)
    {
        if (authorizer == null)
        {
            return new AuthorizationResult(
                null,
                Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
        }
        if (!AevatarScopeAccessGuard.TryGetCallerScopeId(http, out _))
            return new AuthorizationResult(null, Results.Unauthorized());

        var authorization = http.Request.Headers.Authorization.ToString();
        var bearer = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authorization[7..].Trim()
            : string.Empty;
        if (bearer.Length == 0)
            return new AuthorizationResult(null, Results.Unauthorized());

        PlatformCaller caller;
        try
        {
            caller = await authorizer.ResolveCallerAsync(bearer, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new AuthorizationResult(null, Results.Forbid());
        }

        return caller.IsElevated && !string.IsNullOrWhiteSpace(caller.UserId)
            ? new AuthorizationResult(caller, null)
            : new AuthorizationResult(null, Results.Forbid());
    }

    private static bool IsValidRequestId(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.Length is > 0 and <= 128 &&
               normalized.All(static character =>
                   char.IsAsciiLetterOrDigit(character) ||
                   character is '.' or '_' or ':' or '-');
    }

    private static bool IsValidReason(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.Length is > 0 and <= 256 &&
               normalized.All(static character =>
                   !char.IsControl(character) &&
                   character is not '\r' and not '\n' and not '\u2028' and not '\u2029');
    }

    private static ValueTask<string> SanitizeRequest(EndpointAuditSanitizationContext context)
    {
        var request = context.Arguments.OfType<ReplayRetryExhaustedFailuresRequest>().FirstOrDefault();
        if (request == null)
            return ValueTask.FromResult($"{context.HttpContext.Request.Method} cqrs-replay-exhausted invalid");

        var requestIdDigest = IsValidRequestId(request.RequestId)
            ? BuildRequestIdDigest(request.RequestId)
            : "invalid";
        var reason = IsValidReason(request.Reason)
            ? EndpointAuditSanitizers.SanitizeValue(request.Reason)
            : "invalid";
        return ValueTask.FromResult(
            $"{context.HttpContext.Request.Method} cqrs-replay-exhausted " +
            $"state_version={request.ExpectedScopeStateVersion} " +
            $"unresolved={request.ExpectedUnresolvedFailureCount} " +
            $"retry_exhausted={request.ExpectedRetryExhaustedFailureCount} " +
            $"max_items={request.MaxItems} request_sha256={requestIdDigest} reason={reason}");
    }

    private static string BuildRequestIdDigest(string requestId)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(requestId.Trim()));
        return Convert.ToHexString(digest.AsSpan(0, 16)).ToLowerInvariant();
    }

    private static IResult InvalidRequest() =>
        Results.BadRequest(new ErrorResponse("invalid_replay_request", 0, 0, 0));

    private static IResult ManifestConflict(
        string code,
        ProjectionRetryExhaustedFailureRepairResult result) =>
        Results.Json(
            new ErrorResponse(
                code,
                result.CurrentScopeStateVersion,
                result.CurrentUnresolvedFailureCount,
                result.CurrentRetryExhaustedFailureCount),
            statusCode: StatusCodes.Status409Conflict);

    internal sealed record ReplayRetryExhaustedFailuresRequest(
        long ExpectedScopeStateVersion,
        int ExpectedUnresolvedFailureCount,
        int ExpectedRetryExhaustedFailureCount,
        int MaxItems,
        string RequestId,
        string Reason);

    internal sealed record AcceptedResponse(
        string Status,
        string ScopeActorId,
        string RequestId,
        long ExpectedScopeStateVersion,
        int MaxItems);

    internal sealed record ErrorResponse(
        string Code,
        long CurrentScopeStateVersion,
        int CurrentUnresolvedFailureCount,
        int CurrentRetryExhaustedFailureCount);

    private sealed record AuthorizationResult(PlatformCaller? Caller, IResult? Error);
}
