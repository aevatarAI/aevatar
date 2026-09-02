using System.Diagnostics;
using System.Security.Claims;
using Aevatar.Audit;
using Aevatar.Audit.Hosting.EndpointAudit;
using Aevatar.Audit.Abstractions.Identity;
using Aevatar.Audit.Abstractions.Models;
using Aevatar.Audit.Abstractions.Ports;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Aevatar.Bootstrap.Hosting;

public sealed class EndpointAuditCaptureMiddleware
{
    private const string AttemptedSuffix = ".attempted";
    private const string AnonymousCanonicalActorKey = "system:endpoint-audit-anonymous";
    private const string UnknownScopeId = "unknown";
    private static readonly TimeSpan TerminalAppendTimeout = TimeSpan.FromSeconds(5);

    private readonly RequestDelegate _next;
    private readonly IAuditTrailAppender? _appender;
    private readonly IAuditActorIdentityHasher? _identityHasher;
    private readonly ILogger<EndpointAuditCaptureMiddleware> _logger;
    private readonly TimeProvider _timeProvider;

    public EndpointAuditCaptureMiddleware(
        RequestDelegate next,
        IEnumerable<IAuditTrailAppender> appenders,
        IEnumerable<IAuditActorIdentityHasher> identityHashers,
        ILogger<EndpointAuditCaptureMiddleware> logger,
        TimeProvider? timeProvider = null)
    {
        _next = next;
        _appender = appenders.FirstOrDefault();
        _identityHasher = identityHashers.FirstOrDefault();
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

        // Unauthenticated callers are skipped by default so 401 challenges are
        // not recorded (no authenticated actor to hash). Endpoints that opt in
        // via CaptureUnauthenticated (explicit AllowAnonymous ingress: OAuth
        // callbacks, HMAC-signed webhooks, relay ingress) are still recorded,
        // hashing the fixed anonymous canonical actor key.
        if (context.User.Identity?.IsAuthenticated != true && !metadata.CaptureUnauthenticated)
        {
            await _next(context);
            return;
        }

        if (_appender is null || _identityHasher is null)
        {
            await _next(context);
            return;
        }

        await EnsureRequestCapturedBestEffortAsync(context, metadata);
        await AppendBestEffortAsync(
            _appender,
            () => BuildRecord(context, metadata, _identityHasher, metadata.OperationName + AttemptedSuffix, AuditOutcome.Accepted),
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
            await AppendTerminalBestEffortAsync(
                _appender,
                () => BuildRecord(context, metadata, _identityHasher, metadata.OperationName, outcome),
                metadata.OperationName);
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
        var recordedAt = _timeProvider.GetUtcNow();
        var scopeId = SanitizeRecordText(ResolveScopeId(context.User, context));
        var correlation = BuildCorrelation(context);
        var lifecyclePhase = isAttemptedRecord || outcome == AuditOutcome.Accepted
            ? AuditLifecyclePhase.Accepted
            : AuditLifecyclePhase.Terminal;
        var terminalOutcome = ResolveTerminalOutcome(context, lifecyclePhase, outcome);
        var failure = BuildFailure(context, terminalOutcome);

        var record = new AuditRecord
        {
            AuditId = BuildAuditId(context, operationName, outcome),
            OccurredAt = Timestamp.FromDateTimeOffset(recordedAt),
            RecordedAt = Timestamp.FromDateTimeOffset(recordedAt),
            EventKind = operationName,
            Subject = BuildSubject(target),
            SchemaVersion = AuditContractSemantics.CurrentSchemaVersion,
            Source = "urn:aevatar:audit:boundary-endpoint",
            ScopeId = scopeId,
            AuditActorId = auditIdentity.AuditActorId,
            IdentityKeyId = auditIdentity.IdentityKeyId,
            ActorKind = ResolveActorKind(context.User),
            CredentialSource = ResolveCredentialSource(context.User),
            OperationKind = metadata.OperationKind,
            OperationName = operationName,
            SensitivityLevel = metadata.SensitivityLevel,
            Outcome = outcome,
            LifecyclePhase = lifecyclePhase,
            TerminalOutcome = terminalOutcome,
            Target = new AuditTarget
            {
                Kind = SanitizeRecordText(target.Kind),
                Id = SanitizeRecordText(target.Id),
                DisplayName = SanitizeRecordText(target.DisplayName),
            },
            Correlation = correlation,
            RequestSummary = SanitizeRecordText(ResolveRequestSummary(context)),
            ResultSummary = isAttemptedRecord ? string.Empty : SanitizeRecordText(ResolveResultSummary(context, outcome)),
            ErrorCode = isAttemptedRecord
                ? string.Empty
                : failure?.Code ?? ResolveErrorCode(context, outcome),
            ErrorSummary = isAttemptedRecord
                ? string.Empty
                : failure?.SanitizedMessage ?? SanitizeRecordText(ResolveErrorSummary(context, outcome)),
            CapturePlane = AuditCapturePlane.BoundaryEndpoint,
            Provenance = new AuditExecutionProvenance
            {
                ScopeId = scopeId,
                TeamId = SanitizeRecordText(ReadRouteValue(context, "teamId") ?? string.Empty),
                MemberId = SanitizeRecordText(ReadRouteValue(context, "memberId") ?? string.Empty),
                WorkflowId = SanitizeRecordText(ReadRouteValue(context, "workflowId") ?? string.Empty),
                PublishedServiceId = SanitizeRecordText(ReadRouteValue(context, "publishedServiceId") ?? string.Empty),
                RunId = correlation.WorkflowRunId,
                CausationId = correlation.CausationId,
                CorrelationId = correlation.CorrelationId,
            },
            Redaction = new AuditRedaction
            {
                Policy = "aevatar.audit.endpoint-safe-fields.v1",
                ValuesSanitized = true,
            },
        };
        record.Redaction.OmittedFields.Add(["request.headers", "request.body", "response.body"]);
        if (failure is not null)
            record.Failure = failure;

        return record;
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

    private async Task AppendTerminalBestEffortAsync(
        IAuditTrailAppender appender,
        Func<AuditRecord> recordFactory,
        string operationName)
    {
        using var deadline = new CancellationTokenSource(TerminalAppendTimeout, _timeProvider);
        Task<AuditTrailAppendResult> appendTask;
        try
        {
            appendTask = appender.AppendAsync(recordFactory(), deadline.Token);
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogDebug(ex, "Endpoint audit append cancelled.");
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Endpoint audit append failed.");
            return;
        }

        try
        {
            await appendTask.WaitAsync(TerminalAppendTimeout, _timeProvider);
        }
        catch (TimeoutException ex)
        {
            ObserveLateTerminalAppendFault(appendTask, operationName);
            _logger.LogError(
                ex,
                "Endpoint terminal audit append timed out after {TimeoutSeconds}s. operation={OperationName}",
                TerminalAppendTimeout.TotalSeconds,
                operationName);
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogDebug(ex, "Endpoint audit append cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Endpoint audit append failed.");
        }
    }

    private void ObserveLateTerminalAppendFault(Task appendTask, string operationName)
    {
        _ = appendTask.ContinueWith(
            task => _logger.LogError(
                task.Exception,
                "Endpoint terminal audit append failed after its deadline. operation={OperationName}",
                operationName),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
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

    private static EndpointAuditTarget ResolveTarget(HttpContext context, EndpointAuditMetadata metadata)
    {
        return EndpointAuditHttpContextState.TryGetTarget(context, out var target)
            ? target
            : new EndpointAuditTarget(metadata.TargetKind, string.Empty);
    }

    private static AuditCorrelation BuildCorrelation(HttpContext context)
    {
        var activity = Activity.Current;
        var hasW3CContext = activity?.IdFormat == ActivityIdFormat.W3C;
        return new AuditCorrelation
        {
            TraceId = activity?.TraceId.ToString() ?? string.Empty,
            SpanId = activity?.SpanId.ToString() ?? string.Empty,
            Traceparent = hasW3CContext ? activity?.Id ?? string.Empty : string.Empty,
            Tracestate = hasW3CContext ? activity?.TraceStateString ?? string.Empty : string.Empty,
            RequestId = context.TraceIdentifier ?? string.Empty,
            CorrelationId = context.TraceIdentifier ?? string.Empty,
            CommandId = SanitizeRecordText(ReadRouteValue(context, "commandId") ?? string.Empty),
            CallId = SanitizeRecordText(ReadRouteValue(context, "callId") ?? string.Empty),
            SessionId = SanitizeRecordText(ReadRouteValue(context, "sessionId") ?? string.Empty),
            WorkflowRunId = SanitizeRecordText(ReadRouteValue(context, "runId") ?? string.Empty),
            ApprovalId = SanitizeRecordText(ReadRouteValue(context, "approvalId") ?? string.Empty),
        };
    }

    private static AuditTerminalOutcome ResolveTerminalOutcome(
        HttpContext context,
        AuditLifecyclePhase lifecyclePhase,
        AuditOutcome outcome)
    {
        if (lifecyclePhase != AuditLifecyclePhase.Terminal)
            return AuditTerminalOutcome.Unspecified;

        if (context.Response.StatusCode is StatusCodes.Status408RequestTimeout or StatusCodes.Status504GatewayTimeout)
            return AuditTerminalOutcome.TimedOut;

        return outcome switch
        {
            AuditOutcome.Success => AuditTerminalOutcome.Succeeded,
            AuditOutcome.Cancelled => AuditTerminalOutcome.Cancelled,
            _ => AuditTerminalOutcome.Failed,
        };
    }

    private static AuditFailure? BuildFailure(HttpContext context, AuditTerminalOutcome terminalOutcome)
    {
        if (terminalOutcome is not (AuditTerminalOutcome.Failed or AuditTerminalOutcome.TimedOut))
            return null;

        var statusCode = context.Response.StatusCode;
        var category = terminalOutcome == AuditTerminalOutcome.TimedOut
            ? AuditFailureCategory.Timeout
            : statusCode switch
            {
                StatusCodes.Status401Unauthorized or StatusCodes.Status403Forbidden => AuditFailureCategory.Authorization,
                StatusCodes.Status409Conflict => AuditFailureCategory.Conflict,
                >= 400 and < 500 => AuditFailureCategory.Validation,
                _ => AuditFailureCategory.Internal,
            };
        return new AuditFailure
        {
            Code = terminalOutcome == AuditTerminalOutcome.TimedOut
                ? "endpoint_timeout"
                : ResolveErrorCode(context, EndpointAuditOutcomeClassifier.Classify(context)),
            Category = category,
            Retryability = statusCode >= 500
                ? AuditRetryability.Retryable
                : AuditRetryability.NotRetryable,
            FailedPhase = category is AuditFailureCategory.Authorization or AuditFailureCategory.Validation
                ? AuditLifecyclePhase.Accepted
                : AuditLifecyclePhase.Running,
            SanitizedMessage = SanitizeRecordText(
                ResolveErrorSummary(context, EndpointAuditOutcomeClassifier.Classify(context))),
        };
    }

    private static string BuildSubject(EndpointAuditTarget target) =>
        $"{SanitizeRecordText(target.Kind)}/{SanitizeRecordText(target.Id)}";

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
        // Anonymous ingress (opt-in CaptureUnauthenticated): no authenticated
        // caller, so the record is a system-captured governance fact keyed by the
        // fixed anonymous canonical actor key.
        if (user.Identity?.IsAuthenticated != true)
        {
            return AuditActorKind.System;
        }

        return FirstClaimValue(user, "client_id", "app_id") is null
            ? AuditActorKind.NyxidUser
            : AuditActorKind.Service;
    }

    private static AuditCredentialSource ResolveCredentialSource(ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated != true)
        {
            return AuditCredentialSource.System;
        }

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
        return EndpointAuditSanitizers.SanitizeValue(value);
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
