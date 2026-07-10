using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aevatar.Audit;
using Aevatar.Audit.Hosting.EndpointAudit;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

internal static class WorkflowExternalApprovalCallbackEndpoints
{
    private const string ReplayRouteKey = "external-approval";
    private const int Status425TooEarly = 425;

    public static void Map(IEndpointRouteBuilder group)
    {
        group.MapPost("/workflow-external-approvals/{routeKey}", HandleAsync)
            .WithName("PostWorkflowExternalApprovalCallback")
            .WithEndpointAudit(
                "workflow.external-approval.callback",
                AuditSensitivityLevel.Confidential,
                "workflow_run",
                // Static target: {routeKey} is an opaque continuation key, never recorded.
                EndpointAuditTargetResolvers.Static("workflow_run", "external-approval"),
                captureUnauthenticated: true);
    }

    internal static async Task<IResult> HandleAsync(
        HttpContext http,
        string routeKey,
        IWorkflowExternalApprovalContinuationLookupPort lookupPort,
        IWorkflowWebhookReplayAdmissionPort replayAdmissionPort,
        ICommandDispatchService<WorkflowSignalCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError> signalService,
        IOptions<WorkflowExternalApprovalCallbackOptions> options,
        ILoggerFactory loggerFactory,
        CancellationToken ct = default)
    {
        using var scope = ApiRequestScope.BeginHttp();
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(lookupPort);
        ArgumentNullException.ThrowIfNull(replayAdmissionPort);
        ArgumentNullException.ThrowIfNull(signalService);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        var logger = loggerFactory.CreateLogger("Aevatar.Workflow.Host.Api.ExternalApprovalCallback");
        var settings = options.Value;
        if (!settings.Enabled)
            return CallbackDisabled(scope);

        var body = await TryReadBodyAsync(http.Request, ct);
        if (body.Result != null)
            return body.Result;

        var receivedAt = DateTimeOffset.UtcNow;
        var build = BuildRequest(http.Request, routeKey, body.RawBody, receivedAt, settings);
        if (!build.Succeeded)
            return BuildRequestFailure(scope, build);

        var lookup = await LookupContinuationAsync(http, build, lookupPort, settings, logger, scope, ct);
        if (lookup.Result != null)
            return lookup.Result;

        var replayUnavailable = EnsureReplayStoreAvailable(http, replayAdmissionPort, settings, scope);
        if (replayUnavailable != null)
            return replayUnavailable;

        var admission = BuildReplayAdmission(build, receivedAt);
        var replay = await AdmitReplayAsync(http, admission, replayAdmissionPort, settings, logger, scope, ct);
        if (replay.Result != null)
            return replay.Result;

        return await MapReplayAdmissionAsync(
            http,
            build,
            lookup.Continuation!,
            admission,
            replay.Admission!,
            replayAdmissionPort,
            signalService,
            logger,
            scope,
            settings,
            ct);
    }

    private static async Task<IResult> MapReplayAdmissionAsync(
        HttpContext http,
        ExternalApprovalCallbackBuildResult build,
        WorkflowExternalApprovalContinuation continuation,
        WorkflowWebhookReplayAdmissionRequest admission,
        WorkflowWebhookReplayAdmission replayAdmission,
        IWorkflowWebhookReplayAdmissionPort replayAdmissionPort,
        ICommandDispatchService<WorkflowSignalCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError> signalService,
        ILogger logger,
        ApiRequestScope scope,
        WorkflowExternalApprovalCallbackOptions options,
        CancellationToken ct)
    {
        switch (replayAdmission.Status)
        {
            case WorkflowWebhookReplayAdmissionStatus.Admitted:
                return await DispatchAsync(
                    http,
                    build,
                    continuation,
                    admission,
                    replayAdmissionPort,
                    signalService,
                    logger,
                    scope,
                    ct);
            case WorkflowWebhookReplayAdmissionStatus.DuplicateCompleted:
            case WorkflowWebhookReplayAdmissionStatus.DuplicateInProgress:
                return DuplicateAdmission(build, replayAdmission, scope);
            case WorkflowWebhookReplayAdmissionStatus.PayloadConflict:
                return ConflictingAdmission(scope);
            default:
                scope.MarkResult(StatusCodes.Status503ServiceUnavailable);
                return RetryableJson(
                    http,
                    options,
                    "EXTERNAL_APPROVAL_REPLAY_ADMISSION_FAILED",
                    "Workflow external approval replay admission failed.",
                    StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static IResult CallbackDisabled(ApiRequestScope scope)
    {
        scope.MarkResult(StatusCodes.Status404NotFound);
        return Results.Json(
            new { code = "EXTERNAL_APPROVAL_CALLBACK_DISABLED", message = "Workflow external approval callbacks are disabled." },
            statusCode: StatusCodes.Status404NotFound);
    }

    private static IResult BuildRequestFailure(
        ApiRequestScope scope,
        ExternalApprovalCallbackBuildResult build)
    {
        scope.MarkResult(build.StatusCode);
        return Results.Json(
            new { code = build.ErrorCode, message = build.ErrorMessage },
            statusCode: build.StatusCode);
    }

    private static async Task<RequestBodyReadResult> TryReadBodyAsync(
        HttpRequest request,
        CancellationToken ct)
    {
        try
        {
            return new RequestBodyReadResult(await ReadBodyAsync(request, ct), null);
        }
        catch (OperationCanceledException)
        {
            return new RequestBodyReadResult([], Results.StatusCode(499));
        }
    }

    private static async Task<ContinuationLookupResult> LookupContinuationAsync(
        HttpContext http,
        ExternalApprovalCallbackBuildResult build,
        IWorkflowExternalApprovalContinuationLookupPort lookupPort,
        WorkflowExternalApprovalCallbackOptions options,
        ILogger logger,
        ApiRequestScope scope,
        CancellationToken ct)
    {
        try
        {
            var continuation = await lookupPort.FindActiveAsync(
                build.SourceId,
                build.ExternalIdKind,
                build.ExternalId,
                ct);
            return continuation == null
                ? new ContinuationLookupResult(null, ContinuationNotVisible(http, options, scope))
                : new ContinuationLookupResult(continuation, null);
        }
        catch (OperationCanceledException)
        {
            return new ContinuationLookupResult(null, Results.StatusCode(499));
        }
        catch (Exception ex)
        {
            scope.MarkResult(StatusCodes.Status503ServiceUnavailable);
            logger.LogError(ex, "Workflow external approval continuation lookup failed.");
            return new ContinuationLookupResult(
                null,
                RetryableJson(
                    http,
                    options,
                    "EXTERNAL_APPROVAL_LOOKUP_UNAVAILABLE",
                    "Workflow external approval continuation lookup is unavailable.",
                    StatusCodes.Status503ServiceUnavailable));
        }
    }

    private static IResult ContinuationNotVisible(
        HttpContext http,
        WorkflowExternalApprovalCallbackOptions options,
        ApiRequestScope scope)
    {
        scope.MarkResult(Status425TooEarly);
        return RetryableJson(
            http,
            options,
            "EXTERNAL_APPROVAL_CONTINUATION_NOT_VISIBLE",
            "Workflow external approval continuation is not visible yet.",
            Status425TooEarly);
    }

    private static IResult? EnsureReplayStoreAvailable(
        HttpContext http,
        IWorkflowWebhookReplayAdmissionPort replayAdmissionPort,
        WorkflowExternalApprovalCallbackOptions options,
        ApiRequestScope scope)
    {
        if (replayAdmissionPort.IsAvailable)
            return null;

        scope.MarkResult(StatusCodes.Status503ServiceUnavailable);
        return RetryableJson(
            http,
            options,
            "EXTERNAL_APPROVAL_REPLAY_STORE_UNAVAILABLE",
            "Workflow external approval replay store is unavailable.",
            StatusCodes.Status503ServiceUnavailable);
    }

    private static WorkflowWebhookReplayAdmissionRequest BuildReplayAdmission(
        ExternalApprovalCallbackBuildResult build,
        DateTimeOffset receivedAt)
    {
        var commandId = BuildSeed(
            "external-approval",
            build.SourceId,
            build.ExternalIdKind,
            build.ExternalId,
            build.TerminalStatus);
        var canonicalExternalApprovalId = BuildCanonicalExternalApprovalId(
            build.SourceId,
            build.ExternalIdKind,
            build.ExternalId);
        return new WorkflowWebhookReplayAdmissionRequest(
            ReplayRouteKey,
            build.SourceId,
            canonicalExternalApprovalId,
            build.TerminalStatus,
            receivedAt,
            commandId,
            commandId);
    }

    private static async Task<ReplayAdmissionResult> AdmitReplayAsync(
        HttpContext http,
        WorkflowWebhookReplayAdmissionRequest admission,
        IWorkflowWebhookReplayAdmissionPort replayAdmissionPort,
        WorkflowExternalApprovalCallbackOptions options,
        ILogger logger,
        ApiRequestScope scope,
        CancellationToken ct)
    {
        try
        {
            return new ReplayAdmissionResult(await replayAdmissionPort.AdmitAsync(admission, ct), null);
        }
        catch (OperationCanceledException)
        {
            return new ReplayAdmissionResult(null, Results.StatusCode(499));
        }
        catch (Exception ex)
        {
            scope.MarkResult(StatusCodes.Status503ServiceUnavailable);
            logger.LogError(ex, "Workflow external approval replay admission failed.");
            return new ReplayAdmissionResult(
                null,
                RetryableJson(
                    http,
                    options,
                    "EXTERNAL_APPROVAL_REPLAY_ADMISSION_FAILED",
                    "Workflow external approval replay admission failed.",
                    StatusCodes.Status503ServiceUnavailable));
        }
    }

    private static IResult DuplicateAdmission(
        ExternalApprovalCallbackBuildResult build,
        WorkflowWebhookReplayAdmission replayAdmission,
        ApiRequestScope scope)
    {
        scope.MarkResult(StatusCodes.Status202Accepted);
        return Results.Accepted(
            value: new
            {
                accepted = true,
                duplicate = true,
                sourceId = build.SourceId,
                externalIdKind = build.ExternalIdKind,
                externalId = build.ExternalId,
                terminalStatus = build.TerminalStatus,
                commandId = replayAdmission.ExistingCommandId,
                correlationId = replayAdmission.ExistingCorrelationId,
            });
    }

    private static IResult ConflictingAdmission(ApiRequestScope scope)
    {
        scope.MarkResult(StatusCodes.Status409Conflict);
        return Results.Json(
            new { code = "EXTERNAL_APPROVAL_TERMINAL_CONFLICT", message = "External approval terminal status conflicts with a previously admitted terminal status." },
            statusCode: StatusCodes.Status409Conflict);
    }

    private static async Task<IResult> DispatchAsync(
        HttpContext http,
        ExternalApprovalCallbackBuildResult build,
        WorkflowExternalApprovalContinuation continuation,
        WorkflowWebhookReplayAdmissionRequest admission,
        IWorkflowWebhookReplayAdmissionPort replayAdmissionPort,
        ICommandDispatchService<WorkflowSignalCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError> signalService,
        ILogger logger,
        ApiRequestScope scope,
        CancellationToken ct)
    {
        var terminalSignal = BuildTerminalSignal(build, continuation);
        try
        {
            var dispatch = await signalService.DispatchAsync(
                new WorkflowSignalCommand(
                    continuation.ActorId,
                    continuation.RunId,
                    continuation.SignalName,
                    admission.CommandId,
                    build.TerminalStatus,
                    continuation.StepId,
                    admission.CorrelationId,
                    terminalSignal),
                ct);

            if (!dispatch.Succeeded || dispatch.Receipt == null)
            {
                await ReleaseAdmissionAsync(admission, replayAdmissionPort, logger);
                return MapDispatchFailure(dispatch.Error, scope);
            }

            CapabilityTraceContext.ApplyCorrelationHeader(http.Response, dispatch.Receipt.CorrelationId);
            var statusUrl = BuildWorkflowRunStatusUrl(dispatch.Receipt.ActorId);
            scope.MarkResult(StatusCodes.Status202Accepted);
            return Results.Accepted(
                statusUrl,
                new
                {
                    accepted = true,
                    actorId = dispatch.Receipt.ActorId,
                    runId = dispatch.Receipt.RunId,
                    stepId = continuation.StepId,
                    signalName = continuation.SignalName,
                    terminalStatus = build.TerminalStatus,
                    commandId = dispatch.Receipt.CommandId,
                    correlationId = dispatch.Receipt.CorrelationId,
                    continuationVersion = continuation.SourceVersion,
                    statusUrl,
                });
        }
        catch (OperationCanceledException)
        {
            await ReleaseAdmissionAsync(admission, replayAdmissionPort, logger);
            return Results.StatusCode(499);
        }
        catch (Exception ex)
        {
            await ReleaseAdmissionAsync(admission, replayAdmissionPort, logger);
            scope.MarkError();
            logger.LogError(ex, "Workflow external approval signal dispatch failed.");
            return Results.Json(
                new { code = "EXTERNAL_APPROVAL_SIGNAL_DISPATCH_FAILED", message = "Workflow external approval signal dispatch failed." },
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static WorkflowExternalApprovalTerminalSignalCommand BuildTerminalSignal(
        ExternalApprovalCallbackBuildResult build,
        WorkflowExternalApprovalContinuation continuation)
    {
        return new WorkflowExternalApprovalTerminalSignalCommand(
            build.SourceId,
            build.ExternalIdKind,
            build.ExternalId,
            build.InstanceCode,
            string.IsNullOrWhiteSpace(build.RequestId) ? continuation.RequestId : build.RequestId,
            build.TerminalStatus,
            continuation.CallbackIdempotencyKey,
            build.ProviderDeliveryEvidence);
    }

    private static ExternalApprovalCallbackBuildResult BuildRequest(
        HttpRequest request,
        string routeKey,
        ReadOnlySpan<byte> rawBody,
        DateTimeOffset receivedAt,
        WorkflowExternalApprovalCallbackOptions options)
    {
        var binding = ResolveBinding(options, routeKey);
        if (binding == null)
            return ExternalApprovalCallbackBuildResult.Failure(
                "EXTERNAL_APPROVAL_ROUTE_NOT_FOUND",
                "External approval callback route was not found.",
                StatusCodes.Status404NotFound);

        if (Normalize(binding.HmacSecret) == null)
            return ExternalApprovalCallbackBuildResult.Failure(
                "EXTERNAL_APPROVAL_AUTH_CONFIG_REQUIRED",
                "External approval callback HMAC secret is required.",
                StatusCodes.Status500InternalServerError);

        var auth = Authenticate(request, binding, rawBody, receivedAt);
        if (!auth.Succeeded)
            return ExternalApprovalCallbackBuildResult.Failure(
                auth.ErrorCode ?? "EXTERNAL_APPROVAL_AUTH_INVALID",
                auth.ErrorMessage ?? "External approval callback authentication failed.",
                StatusCodes.Status401Unauthorized);

        var sourceId = Normalize(binding.SourceId);
        var externalIdKind = NormalizeIdentity(binding.ExternalIdKind);
        var externalId = ExtractJsonString(rawBody, Normalize(binding.ExternalIdJsonPath));
        var status = ExtractJsonString(rawBody, Normalize(binding.StatusJsonPath));
        var terminalStatus = NormalizeTerminalStatus(status);
        if (sourceId == null || externalIdKind == null || externalId == null || terminalStatus == null)
        {
            return ExternalApprovalCallbackBuildResult.Failure(
                "EXTERNAL_APPROVAL_TERMINAL_PAYLOAD_INVALID",
                "External approval callback terminal payload is invalid.",
                StatusCodes.Status400BadRequest);
        }

        var deliveryId = ResolveDeliveryId(request, binding, rawBody);
        var evidence = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["route_key"] = Normalize(routeKey) ?? string.Empty,
            ["auth_scheme"] = auth.AuthScheme,
            ["principal_subject"] = auth.PrincipalSubject,
            ["received_at_unix_ms"] = receivedAt.ToUnixTimeMilliseconds().ToString(),
        };
        if (!string.IsNullOrWhiteSpace(deliveryId))
            evidence["delivery_id"] = deliveryId;
        if (!string.IsNullOrWhiteSpace(request.ContentType))
            evidence["content_type"] = request.ContentType.Trim();

        return ExternalApprovalCallbackBuildResult.Success(
            sourceId,
            externalIdKind,
            NormalizeIdentity(externalId)!,
            terminalStatus,
            ExtractJsonString(rawBody, Normalize(binding.InstanceCodeJsonPath)) ?? string.Empty,
            ExtractJsonString(rawBody, Normalize(binding.RequestIdJsonPath)) ?? string.Empty,
            evidence);
    }

    private static WorkflowExternalApprovalCallbackBindingOptions? ResolveBinding(
        WorkflowExternalApprovalCallbackOptions options,
        string routeKey)
    {
        var normalizedRoute = Normalize(routeKey);
        if (normalizedRoute == null)
            return null;

        return options.Bindings.FirstOrDefault(binding =>
            string.Equals(Normalize(binding.RouteKey), normalizedRoute, StringComparison.Ordinal));
    }

    private static WorkflowWebhookAuthenticationResult Authenticate(
        HttpRequest request,
        WorkflowExternalApprovalCallbackBindingOptions binding,
        ReadOnlySpan<byte> rawBody,
        DateTimeOffset receivedAt)
    {
        var secret = Normalize(binding.HmacSecret);
        if (secret == null)
            return WorkflowWebhookAuthenticationResult.Failure(
                "EXTERNAL_APPROVAL_AUTH_CONFIG_REQUIRED",
                "External approval callback HMAC secret is required.");

        var signatureHeader = Normalize(binding.HmacSignatureHeader) ?? "X-Aevatar-Signature";
        var timestampHeader = Normalize(binding.HmacTimestampHeader) ?? "X-Aevatar-Timestamp";
        var signature = Normalize(request.Headers[signatureHeader].FirstOrDefault());
        var timestampValue = Normalize(request.Headers[timestampHeader].FirstOrDefault());
        if (signature == null || timestampValue == null)
            return WorkflowWebhookAuthenticationResult.Failure(
                "EXTERNAL_APPROVAL_AUTH_REQUIRED",
                "External approval callback signature and timestamp are required.");

        if (!long.TryParse(timestampValue, out var timestampUnixSeconds))
            return WorkflowWebhookAuthenticationResult.Failure(
                "EXTERNAL_APPROVAL_AUTH_INVALID",
                "External approval callback timestamp is invalid.");

        var timestamp = DateTimeOffset.FromUnixTimeSeconds(timestampUnixSeconds);
        var maxSkew = TimeSpan.FromSeconds(binding.MaxTimestampSkewSeconds <= 0
            ? 300
            : binding.MaxTimestampSkewSeconds);
        if (Duration(receivedAt - timestamp) > maxSkew)
            return WorkflowWebhookAuthenticationResult.Failure(
                "EXTERNAL_APPROVAL_AUTH_EXPIRED",
                "External approval callback timestamp is outside the accepted window.");

        var expected = ComputeSignature(secret, timestampValue, rawBody);
        if (!FixedTimeEquals(signature, expected))
            return WorkflowWebhookAuthenticationResult.Failure(
                "EXTERNAL_APPROVAL_AUTH_INVALID",
                "External approval callback signature is invalid.");

        return WorkflowWebhookAuthenticationResult.Success("hmac-sha256", Normalize(binding.SourceId) ?? string.Empty);
    }

    private static string? ResolveDeliveryId(
        HttpRequest request,
        WorkflowExternalApprovalCallbackBindingOptions binding,
        ReadOnlySpan<byte> rawBody)
    {
        var headerName = Normalize(binding.DeliveryIdHeader);
        if (headerName != null)
        {
            var headerValue = Normalize(request.Headers[headerName].FirstOrDefault());
            if (headerValue != null)
                return headerValue;
        }

        return ExtractJsonString(rawBody, Normalize(binding.DeliveryIdJsonPath));
    }

    private static string? ExtractJsonString(ReadOnlySpan<byte> rawBody, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            using var document = JsonDocument.Parse(rawBody.ToArray());
            var current = document.RootElement;
            foreach (var segment in NormalizeJsonPath(path))
            {
                if (current.ValueKind != JsonValueKind.Object ||
                    !current.TryGetProperty(segment, out current))
                {
                    return null;
                }
            }

            return current.ValueKind switch
            {
                JsonValueKind.String => Normalize(current.GetString()),
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => current.GetRawText(),
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IEnumerable<string> NormalizeJsonPath(string path)
    {
        var normalized = path.Trim();
        if (normalized.StartsWith("$.", StringComparison.Ordinal))
            normalized = normalized[2..];
        else if (normalized.StartsWith(".", StringComparison.Ordinal))
            normalized = normalized[1..];

        return normalized
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string? NormalizeTerminalStatus(string? status)
    {
        var normalized = NormalizeIdentity(status);
        return normalized switch
        {
            "approved" => "APPROVED",
            "rejected" => "REJECTED",
            "canceled" => "CANCELED",
            "cancelled" => "CANCELED",
            _ => null,
        };
    }

    private static IResult RetryableJson(
        HttpContext http,
        WorkflowExternalApprovalCallbackOptions options,
        string code,
        string message,
        int statusCode)
    {
        http.Response.Headers.RetryAfter = ResolveRetryAfter(options).ToString();
        return Results.Json(new { code, message }, statusCode: statusCode);
    }

    private static int ResolveRetryAfter(WorkflowExternalApprovalCallbackOptions options) =>
        Math.Clamp(options.RetryAfterSeconds <= 0 ? 5 : options.RetryAfterSeconds, 1, 3600);

    private static IResult MapDispatchFailure(
        WorkflowRunControlStartError error,
        ApiRequestScope scope)
    {
        var (statusCode, message) = error.Code switch
        {
            WorkflowRunControlStartErrorCode.InvalidActorId => (
                StatusCodes.Status400BadRequest,
                "actorId is required."),
            WorkflowRunControlStartErrorCode.InvalidRunId => (
                StatusCodes.Status400BadRequest,
                "runId is required."),
            WorkflowRunControlStartErrorCode.InvalidStepId => (
                StatusCodes.Status400BadRequest,
                "stepId is required."),
            WorkflowRunControlStartErrorCode.InvalidSignalName => (
                StatusCodes.Status400BadRequest,
                "signalName is required."),
            WorkflowRunControlStartErrorCode.ActorNotFound => (
                StatusCodes.Status404NotFound,
                $"Actor '{error.ActorId}' not found."),
            WorkflowRunControlStartErrorCode.ActorNotWorkflowRun => (
                StatusCodes.Status409Conflict,
                $"Actor '{error.ActorId}' is not a workflow run."),
            WorkflowRunControlStartErrorCode.RunBindingMissing => (
                StatusCodes.Status409Conflict,
                $"Actor '{error.ActorId}' is missing workflow run binding."),
            WorkflowRunControlStartErrorCode.RunBindingMismatch => (
                StatusCodes.Status409Conflict,
                $"Actor '{error.ActorId}' is bound to run '{error.BoundRunId}', not '{error.RequestedRunId}'."),
            _ => (StatusCodes.Status400BadRequest, "Workflow signal dispatch request is invalid."),
        };
        scope.MarkResult(statusCode);
        return Results.Json(
            new { code = error.Code.ToString(), message },
            statusCode: statusCode);
    }

    private static async Task ReleaseAdmissionAsync(
        WorkflowWebhookReplayAdmissionRequest admission,
        IWorkflowWebhookReplayAdmissionPort replayAdmissionPort,
        ILogger logger)
    {
        try
        {
            await replayAdmissionPort.ReleaseAsync(admission, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Workflow external approval replay admission release failed.");
        }
    }

    private static async Task<byte[]> ReadBodyAsync(HttpRequest request, CancellationToken ct)
    {
        using var memory = new MemoryStream();
        await request.Body.CopyToAsync(memory, ct);
        return memory.ToArray();
    }

    private static string BuildWorkflowRunStatusUrl(string actorId) =>
        $"/api/workflow-actors/{Uri.EscapeDataString(actorId)}/current-state";

    private static string BuildCanonicalExternalApprovalId(
        string sourceId,
        string externalIdKind,
        string externalId) =>
        $"external-approval:{sourceId}:{externalIdKind}:{externalId}";

    private static string ComputeSignature(
        string secret,
        string timestamp,
        ReadOnlySpan<byte> rawBody)
    {
        var prefix = Encoding.UTF8.GetBytes(timestamp + ".");
        var payload = new byte[prefix.Length + rawBody.Length];
        prefix.CopyTo(payload);
        rawBody.CopyTo(payload.AsSpan(prefix.Length));

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return "sha256=" + Convert.ToHexString(hmac.ComputeHash(payload)).ToLowerInvariant();
    }

    private static bool FixedTimeEquals(string actual, string expected)
    {
        var normalizedActual = StripPrefix(actual);
        var normalizedExpected = StripPrefix(expected);
        var actualBytes = Encoding.UTF8.GetBytes(normalizedActual);
        var expectedBytes = Encoding.UTF8.GetBytes(normalizedExpected);
        return actualBytes.Length == expectedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(actualBytes, expectedBytes);
    }

    private static string StripPrefix(string value) =>
        value.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase)
            ? value["sha256=".Length..]
            : value;

    private static TimeSpan Duration(TimeSpan value) =>
        value < TimeSpan.Zero ? value.Negate() : value;

    private static string BuildSeed(params string[] parts) =>
        string.Join(':', parts.Select(Normalize).Where(static part => part != null));

    private static string? NormalizeIdentity(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record ExternalApprovalCallbackBuildResult(
        bool Succeeded,
        string SourceId,
        string ExternalIdKind,
        string ExternalId,
        string TerminalStatus,
        string InstanceCode,
        string RequestId,
        IReadOnlyDictionary<string, string> ProviderDeliveryEvidence,
        string ErrorCode,
        string ErrorMessage,
        int StatusCode)
    {
        public static ExternalApprovalCallbackBuildResult Success(
            string sourceId,
            string externalIdKind,
            string externalId,
            string terminalStatus,
            string instanceCode,
            string requestId,
            IReadOnlyDictionary<string, string> providerDeliveryEvidence) =>
            new(
                true,
                sourceId,
                externalIdKind,
                externalId,
                terminalStatus,
                instanceCode,
                requestId,
                providerDeliveryEvidence,
                string.Empty,
                string.Empty,
                StatusCodes.Status200OK);

        public static ExternalApprovalCallbackBuildResult Failure(
            string code,
            string message,
            int statusCode) =>
            new(
                false,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                new Dictionary<string, string>(StringComparer.Ordinal),
                code,
                message,
                statusCode);
    }

    private sealed record RequestBodyReadResult(byte[] RawBody, IResult? Result);

    private sealed record ContinuationLookupResult(
        WorkflowExternalApprovalContinuation? Continuation,
        IResult? Result);

    private sealed record ReplayAdmissionResult(
        WorkflowWebhookReplayAdmission? Admission,
        IResult? Result);
}
