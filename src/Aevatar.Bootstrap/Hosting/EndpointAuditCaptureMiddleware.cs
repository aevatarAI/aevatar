using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Security.Claims;
using Aevatar.Audit;
using Aevatar.Audit.Abstractions.EndpointAudit;
using Aevatar.Audit.Abstractions.Identity;
using Aevatar.Audit.Abstractions.Ports;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.Bootstrap.Hosting;

public sealed class EndpointAuditCaptureMiddleware
{
    private const string AttemptedSuffix = ".attempted";
    private const string AnonymousCanonicalActorKey = "system:endpoint-audit-anonymous";
    private const string UnknownScopeId = "unknown";
    private const string Redacted = "redacted";
    private const string JwtSegmentPattern = "[A-Za-z0-9_-]{8,}";

    private static readonly string[] SensitiveSummaryFragments =
    [
        "authorization",
        "bearer",
        "token",
        "secret",
        "password",
        "cookie",
        "api_key",
        "apikey",
        "oauth",
        "credential",
        "private_key",
    ];

    private static readonly Regex JwtPattern = new(
        $@"\b{JwtSegmentPattern}\.{JwtSegmentPattern}\.{JwtSegmentPattern}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ApiKeyPattern = new(
        @"\b(?:sk|pk|ak|key)-[A-Za-z0-9_-]{16,}\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex EmailPattern = new(
        @"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex PhonePattern = new(
        @"(?<!\w)\+[0-9][0-9 .()-]{7,}[0-9](?!\w)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly RequestDelegate _next;
    private readonly ILogger<EndpointAuditCaptureMiddleware> _logger;
    private readonly TimeProvider _timeProvider;

    public EndpointAuditCaptureMiddleware(
        RequestDelegate next,
        ILogger<EndpointAuditCaptureMiddleware> logger,
        TimeProvider? timeProvider = null)
    {
        _next = next;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var metadata = context.GetEndpoint()?.Metadata.GetMetadata<EndpointAuditMetadata>();
        if (metadata is null)
        {
            await _next(context);
            return;
        }

        if (context.User.Identity?.IsAuthenticated != true)
        {
            await _next(context);
            return;
        }

        if (!TryResolveAuditPorts(context, out var appender, out var identityHasher))
        {
            await _next(context);
            return;
        }

        await EnsureRequestCapturedBestEffortAsync(context, metadata);
        await AppendBestEffortAsync(
            appender,
            () => BuildRecord(context, metadata, identityHasher, metadata.OperationName + AttemptedSuffix, AuditOutcome.Accepted),
            context.RequestAborted);

        Exception? capturedException = null;
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            capturedException = ex;
            EndpointAuditHttpContextState.CaptureException(context, ex);
            throw;
        }
        finally
        {
            if (EndpointAuditHttpContextState.HasSummaryFailure(context))
            {
                _logger.LogError(
                    "Endpoint audit summary capture failed for operation {OperationName}.",
                    metadata.OperationName);
            }

            var outcome = EndpointAuditOutcomeClassifier.Classify(context, capturedException);
            await AppendBestEffortAsync(
                appender,
                () => BuildRecord(context, metadata, identityHasher, metadata.OperationName, outcome),
                context.RequestAborted);
        }
    }

    private AuditRecord BuildRecord(
        HttpContext context,
        EndpointAuditMetadata metadata,
        IAuditActorIdentityHasher identityHasher,
        string operationName,
        AuditOutcome outcome)
    {
        var auditIdentity = identityHasher.Hash(ResolveCanonicalActorKey(context.User));
        var target = ResolveTarget(context, metadata);
        var isAttemptedRecord = operationName.EndsWith(AttemptedSuffix, StringComparison.Ordinal);

        return new AuditRecord
        {
            AuditId = BuildAuditId(context, operationName, outcome),
            OccurredAt = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
            ScopeId = SanitizeRecordText(ResolveScopeId(context.User, context)),
            AuditActorId = auditIdentity.AuditActorId,
            IdentityKeyId = auditIdentity.IdentityKeyId,
            ActorKind = ResolveActorKind(context.User),
            CredentialSource = ResolveCredentialSource(context.User),
            OperationKind = metadata.OperationKind,
            OperationName = operationName,
            SensitivityLevel = metadata.SensitivityLevel,
            Outcome = outcome,
            Target = new AuditTarget
            {
                Kind = SanitizeRecordText(target.Kind),
                Id = SanitizeRecordText(target.Id),
                DisplayName = SanitizeRecordText(target.DisplayName),
            },
            Correlation = BuildCorrelation(context),
            RequestSummary = SanitizeRecordText(ResolveRequestSummary(context)),
            ResultSummary = isAttemptedRecord ? string.Empty : SanitizeRecordText(ResolveResultSummary(context, outcome)),
            ErrorCode = isAttemptedRecord ? string.Empty : ResolveErrorCode(context, outcome),
            ErrorSummary = isAttemptedRecord ? string.Empty : SanitizeRecordText(ResolveErrorSummary(context, outcome)),
            CapturePlane = AuditCapturePlane.BoundaryEndpoint,
        };
    }

    private async Task AppendBestEffortAsync(
        IAuditTrailAppender appender,
        Func<AuditRecord> recordFactory,
        CancellationToken cancellationToken)
    {
        try
        {
            var record = recordFactory();
            await appender.AppendAsync(record, cancellationToken);
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogDebug(
                ex,
                "Endpoint audit append cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Endpoint audit append failed.");
        }
    }

    private async Task EnsureRequestCapturedBestEffortAsync(
        HttpContext context,
        EndpointAuditMetadata metadata)
    {
        if (EndpointAuditHttpContextState.TryGetRequestSummary(context, out _) &&
            EndpointAuditHttpContextState.TryGetTarget(context, out _))
        {
            return;
        }

        try
        {
            await EndpointAuditHttpContextState.CaptureRequestAsync(context, metadata);
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogDebug(
                ex,
                "Endpoint audit request summary capture cancelled for operation {OperationName}.",
                metadata.OperationName);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Endpoint audit request summary capture failed for operation {OperationName}.",
                metadata.OperationName);
        }
    }

    private bool TryResolveAuditPorts(
        HttpContext context,
        out IAuditTrailAppender appender,
        out IAuditActorIdentityHasher identityHasher)
    {
        appender = null!;
        identityHasher = null!;

        try
        {
            var resolvedAppender = context.RequestServices.GetService<IAuditTrailAppender>();
            var resolvedHasher = context.RequestServices.GetService<IAuditActorIdentityHasher>();
            if (resolvedAppender is null || resolvedHasher is null)
            {
                return false;
            }

            appender = resolvedAppender;
            identityHasher = resolvedHasher;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Endpoint audit port resolution failed.");
            return false;
        }
    }

    private static EndpointAuditTarget ResolveTarget(HttpContext context, EndpointAuditMetadata metadata)
    {
        return EndpointAuditHttpContextState.TryGetTarget(context, out var target)
            ? target
            : new EndpointAuditTarget(metadata.TargetKind, string.Empty);
    }

    private static AuditCorrelation BuildCorrelation(HttpContext context)
    {
        return new AuditCorrelation
        {
            TraceId = Activity.Current?.TraceId.ToString() ?? string.Empty,
            RequestId = context.TraceIdentifier ?? string.Empty,
            CommandId = SanitizeRecordText(ReadRouteValue(context, "commandId") ?? string.Empty),
            CallId = SanitizeRecordText(ReadRouteValue(context, "callId") ?? string.Empty),
            SessionId = SanitizeRecordText(ReadRouteValue(context, "sessionId") ?? string.Empty),
            WorkflowRunId = SanitizeRecordText(ReadRouteValue(context, "runId") ?? string.Empty),
            ApprovalId = SanitizeRecordText(ReadRouteValue(context, "approvalId") ?? string.Empty),
        };
    }

    private static string ResolveScopeId(ClaimsPrincipal user, HttpContext context)
    {
        return FirstClaimValue(user, "scope_id", "tenant_id") ??
               ReadRouteValue(context, "scopeId") ??
               UnknownScopeId;
    }

    private static string ResolveCanonicalActorKey(ClaimsPrincipal user)
    {
        var subject = FirstClaimValue(
            user,
            "sub",
            "uid",
            ClaimTypes.NameIdentifier,
            "client_id",
            "app_id");
        if (!string.IsNullOrWhiteSpace(subject))
        {
            return $"nyxid:{NormalizeCanonicalSegment(subject)}";
        }

        var scopeId = FirstClaimValue(user, "scope_id");
        return string.IsNullOrWhiteSpace(scopeId)
            ? AnonymousCanonicalActorKey
            : $"nyxid:{NormalizeCanonicalSegment(scopeId)}";
    }

    private static AuditActorKind ResolveActorKind(ClaimsPrincipal user)
    {
        return FirstClaimValue(user, "client_id", "app_id") is null
            ? AuditActorKind.NyxidUser
            : AuditActorKind.Service;
    }

    private static AuditCredentialSource ResolveCredentialSource(ClaimsPrincipal user)
    {
        return FirstClaimValue(user, "client_id", "app_id") is null
            ? AuditCredentialSource.BearerToken
            : AuditCredentialSource.ServiceAccount;
    }

    private static string ResolveRequestSummary(HttpContext context)
    {
        return EndpointAuditHttpContextState.TryGetRequestSummary(context, out var summary)
            ? summary
            : $"{context.Request.Method} {ResolveRoutePattern(context)}";
    }

    private static string ResolveResultSummary(HttpContext context, AuditOutcome outcome)
    {
        if (EndpointAuditHttpContextState.TryGetResultSummary(context, out var summary))
        {
            return summary;
        }

        return $"status={context.Response.StatusCode} outcome={outcome}";
    }

    private static string ResolveErrorCode(HttpContext context, AuditOutcome outcome)
    {
        if (outcome == AuditOutcome.Denied)
        {
            return "authorization_denied";
        }

        return outcome == AuditOutcome.Error ? "endpoint_error" : string.Empty;
    }

    private static string ResolveErrorSummary(HttpContext context, AuditOutcome outcome)
    {
        if (EndpointAuditHttpContextState.TryGetException(context, out var exception))
        {
            return exception.GetType().Name;
        }

        return outcome switch
        {
            AuditOutcome.Denied => $"status={context.Response.StatusCode}",
            AuditOutcome.Error => $"status={context.Response.StatusCode}",
            _ => string.Empty,
        };
    }

    private static string BuildAuditId(HttpContext context, string operationName, AuditOutcome outcome)
    {
        var requestId = string.IsNullOrWhiteSpace(context.TraceIdentifier)
            ? Guid.NewGuid().ToString("N")
            : context.TraceIdentifier;
        var suffix = operationName.EndsWith(AttemptedSuffix, StringComparison.Ordinal)
            ? "attempted"
            : outcome.ToString().ToLowerInvariant();
        return $"{requestId}:{operationName}:{suffix}";
    }

    private static string SanitizeRecordText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var summary = value.Trim();
        if (SensitiveSummaryFragments.Any(fragment => summary.Contains(fragment, StringComparison.OrdinalIgnoreCase)) ||
            JwtPattern.IsMatch(summary) ||
            ApiKeyPattern.IsMatch(summary) ||
            EmailPattern.IsMatch(summary) ||
            PhonePattern.IsMatch(summary))
        {
            return Redacted;
        }

        return summary;
    }

    private static string ResolveRoutePattern(HttpContext context)
    {
        return EndpointAuditSanitizers.ResolveRoutePattern(context);
    }

    private static string? ReadRouteValue(HttpContext context, string name)
    {
        var value = context.Request.RouteValues[name]?.ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string? FirstClaimValue(ClaimsPrincipal user, params string[] claimTypes)
    {
        foreach (var claimType in claimTypes)
        {
            var value = user.FindFirst(claimType)?.Value;
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static string NormalizeCanonicalSegment(string value)
    {
        return value.Trim().Replace(':', '_');
    }
}
