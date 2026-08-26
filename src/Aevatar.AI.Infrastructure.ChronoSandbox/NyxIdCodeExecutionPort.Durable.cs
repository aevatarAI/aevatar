using System.Globalization;
using System.Net;
using System.Text.Json;
using Aevatar.AI.Abstractions.CodeExecution;
using Aevatar.AI.ToolProviders.NyxId;
using Microsoft.Extensions.Logging;

namespace Aevatar.AI.Infrastructure.ChronoSandbox;

internal sealed partial class NyxIdCodeExecutionPort
{
    private const string DurableSubmitPath = "/executions";
    private const string DurableCancelBody = "{}";
    private const string DurableGrantHeader = "X-NyxID-Durable-Grant-Id";
    private const string DurableOperationHeader = "X-NyxID-Operation-Id";
    private static readonly TimeSpan DurableCallDeadline = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DefaultDurableRetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxDurableRetryDelay = TimeSpan.FromSeconds(30);

    public async Task<DurableCodeExecutionSubmitOutcome> SubmitAsync(
        DurableCodeExecutionSubmitRequest request,
        CancellationToken ct = default)
    {
        var execution = request?.Execution;
        if (!IsValidExecutionRequest(execution) || !IsValidDurableOperationId(request!.IdempotencyKey))
        {
            return SubmitFailed(DurableFailure(
                DurableCodeExecutionFailureKind.AdmissionDenied,
                "durable_code_execution_request_invalid",
                "The durable code execution request is invalid."));
        }

        var caller = execution!.Caller!;
        var executionCredential = NormalizeCredential(caller.ExecutionNyxIdCredential);
        if (executionCredential is null)
        {
            return SubmitFailed(DurableFailure(
                DurableCodeExecutionFailureKind.AdmissionDenied,
                "code_execution_credential_unavailable",
                "A typed NyxID execution credential is required for code execution."));
        }

        var localDiagnosticId = CreateLocalDiagnosticId();
        if (!_clientFactory.CreateClient().HasPublicApiEndpoint)
        {
            return SubmitFailed(PublicApiNotConfigured(localDiagnosticId));
        }

        var routeResolution = await ResolveDurableRouteAsync(execution, localDiagnosticId, ct)
            .ConfigureAwait(false);
        if (routeResolution.Failure is not null)
            return SubmitFailed(routeResolution.Failure);
        var route = routeResolution.Route!;

        var body = JsonSerializer.Serialize(new
        {
            language = SerializeLanguage(execution.Language),
            script = execution.Source,
            timeout_secs = execution.TimeoutSeconds,
        });
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Idempotency-Key"] = request.IdempotencyKey,
        };
        if (caller.ExecutionCredentialKind == CodeExecutionNyxIdCredentialKind.AgentKey)
        {
            var grant = caller.DurableOperationGrant;
            if (!IsValidSubmitGrant(grant, route))
            {
                return SubmitFailed(DurableFailure(
                    DurableCodeExecutionFailureKind.AdmissionDenied,
                    "code_execution_durable_grant_rebind_required",
                    "The scheduled NyxID credential is not bound to this exact code execution operation."));
            }

            headers[DurableGrantHeader] = grant!.GrantId;
            headers[DurableOperationHeader] = request.IdempotencyKey;
        }

        NyxIdProxyTextResponse response;
        try
        {
            response = await SendDurableProxyAsync(
                    executionCredential,
                    route,
                    DurableSubmitPath,
                    HttpMethod.Post.Method,
                    body,
                    headers,
                    ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            LogDurableFailure("submit_timeout", localDiagnosticId);
            return SubmitFailed(SubmissionUncertain(
                "durable_code_execution_submit_timed_out",
                localDiagnosticId));
        }
        catch (Exception)
        {
            LogDurableFailure("submit_transport_exception", localDiagnosticId);
            return SubmitFailed(SubmissionUncertain(
                "durable_code_execution_submit_transport_unavailable",
                localDiagnosticId));
        }

        if (response.HttpStatus == (int)HttpStatusCode.Accepted && !response.Succeeded)
        {
            return SubmitFailed(SubmissionUncertain(
                "durable_code_execution_submit_accepted_response_unobserved",
                localDiagnosticId,
                response.RetryAfter));
        }

        if (response.HttpStatus != (int)HttpStatusCode.Accepted)
        {
            return SubmitFailed(ClassifyDurableHttpFailure(
                response,
                DurableExchange.Submit,
                localDiagnosticId));
        }

        if (!TryParseReceipt(response.Content, route, response.RetryAfter, out var receipt))
        {
            LogDurableFailure("submit_response_invalid", localDiagnosticId, response.HttpStatus);
            return SubmitFailed(SubmissionUncertain(
                "durable_code_execution_submit_accepted_receipt_unobserved",
                localDiagnosticId,
                response.RetryAfter));
        }

        return new DurableCodeExecutionSubmitOutcome(receipt, null);
    }

    public async Task<DurableCodeExecutionStatusOutcome> GetStatusAsync(
        DurableCodeExecutionOperationRequest request,
        CancellationToken ct = default)
    {
        if (!TryValidateOperationRequest(request, out var token, out var failure))
            return StatusFailed(failure!);

        var localDiagnosticId = CreateLocalDiagnosticId();
        var headers = request.ETag is null
            ? null
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["If-None-Match"] = request.ETag,
            };
        var response = await ExchangeKnownOperationAsync(
                token!,
                request.Route,
                StatusPath(request.ProviderOperationId),
                HttpMethod.Get.Method,
                headers,
                DurableExchange.Status,
                localDiagnosticId,
                ct)
            .ConfigureAwait(false);

        if (response.Failure is not null)
            return StatusFailed(response.Failure);
        if (response.Response!.HttpStatus == (int)HttpStatusCode.NotModified)
        {
            if (!IsValidETag(response.Response.ETag))
            {
                return StatusFailed(InvalidDurableResponse(
                    "durable_code_execution_status_etag_missing",
                    localDiagnosticId));
            }

            return new DurableCodeExecutionStatusOutcome(
                null,
                true,
                response.Response.ETag,
                NormalizeRetryAfter(response.Response.RetryAfter),
                null);
        }

        if (response.Response.HttpStatus != (int)HttpStatusCode.OK ||
            !response.Response.Succeeded)
        {
            return StatusFailed(ClassifyDurableHttpFailure(
                response.Response,
                DurableExchange.Status,
                localDiagnosticId));
        }

        if (!TryParseSnapshot(
                response.Response.Content,
                request.ProviderOperationId,
                request.Route,
                response.Response.ETag,
                response.Response.RetryAfter,
                out var snapshot))
        {
            return StatusFailed(InvalidDurableResponse(
                "durable_code_execution_status_invalid",
                localDiagnosticId));
        }

        return new DurableCodeExecutionStatusOutcome(
            snapshot,
            false,
            snapshot.ETag,
            snapshot.RetryAfter,
            null);
    }

    public async Task<DurableCodeExecutionResultOutcome> GetResultAsync(
        DurableCodeExecutionOperationRequest request,
        CancellationToken ct = default)
    {
        if (!TryValidateOperationRequest(request, out var token, out var failure))
            return ResultFailed(failure!);

        var localDiagnosticId = CreateLocalDiagnosticId();
        var exchange = await ExchangeKnownOperationAsync(
                token!,
                request.Route,
                ResultPath(request.ProviderOperationId),
                HttpMethod.Get.Method,
                extraHeaders: null,
                exchange: DurableExchange.Result,
                localDiagnosticId: localDiagnosticId,
                ct: ct)
            .ConfigureAwait(false);
        if (exchange.Failure is not null)
            return ResultFailed(exchange.Failure);
        var response = exchange.Response!;

        if (response.HttpStatus == (int)HttpStatusCode.Conflict &&
            string.Equals(ReadProviderErrorCode(response.Content), "OPERATION_NOT_TERMINAL", StringComparison.Ordinal))
        {
            return new DurableCodeExecutionResultOutcome(
                null,
                true,
                NormalizeRetryAfter(response.RetryAfter) ?? DefaultDurableRetryDelay,
                null);
        }

        if (response.HttpStatus != (int)HttpStatusCode.OK || !response.Succeeded)
        {
            return ResultFailed(ClassifyDurableHttpFailure(
                response,
                DurableExchange.Result,
                localDiagnosticId));
        }

        if (TryParseTerminalResultWithoutOutput(
                response.Content,
                localDiagnosticId,
                out var terminalFailure))
        {
            return ResultFailed(terminalFailure!);
        }

        var outcome = ParseResponse(response.Content, request.Route, localDiagnosticId);
        if (outcome.Result is null &&
            outcome.Failure?.Kind == CodeExecutionFailureKind.MalformedOutput)
        {
            return ResultFailed(InvalidDurableResponse(
                "durable_code_execution_result_invalid",
                localDiagnosticId));
        }

        return new DurableCodeExecutionResultOutcome(outcome, false, null, null);
    }

    public async Task<DurableCodeExecutionCancelOutcome> CancelAsync(
        DurableCodeExecutionOperationRequest request,
        CancellationToken ct = default)
    {
        if (!TryValidateOperationRequest(request, out var token, out var failure))
            return CancelFailed(failure!);

        var localDiagnosticId = CreateLocalDiagnosticId();
        var exchange = await ExchangeKnownOperationAsync(
                token!,
                request.Route,
                CancelPath(request.ProviderOperationId),
                HttpMethod.Post.Method,
                extraHeaders: null,
                exchange: DurableExchange.Cancel,
                localDiagnosticId: localDiagnosticId,
                ct: ct,
                body: DurableCancelBody)
            .ConfigureAwait(false);
        if (exchange.Failure is not null)
            return CancelFailed(exchange.Failure);
        var response = exchange.Response!;

        if (response.HttpStatus is not ((int)HttpStatusCode.OK or (int)HttpStatusCode.Accepted) ||
            !response.Succeeded)
        {
            return CancelFailed(ClassifyDurableHttpFailure(
                response,
                DurableExchange.Cancel,
                localDiagnosticId));
        }

        if (!TryParseSnapshot(
                response.Content,
                request.ProviderOperationId,
                request.Route,
                response.ETag,
                response.RetryAfter,
                out var snapshot))
        {
            return CancelFailed(InvalidDurableResponse(
                "durable_code_execution_cancel_response_invalid",
                localDiagnosticId));
        }

        return new DurableCodeExecutionCancelOutcome(snapshot, null);
    }

    private async Task<DurableRouteResolution> ResolveDurableRouteAsync(
        CodeExecutionRequest request,
        string localDiagnosticId,
        CancellationToken ct)
    {
        if (TryResolveExactAdmittedRoute(request.Route, out _))
        {
            return new DurableRouteResolution(
                request.Route,
                null);
        }

        var sourceReadableBearerToken = NormalizeCredential(
            request.Caller?.SourceReadableNyxIdAccessToken);
        if (sourceReadableBearerToken is null)
        {
            return new DurableRouteResolution(null, DurableFailure(
                DurableCodeExecutionFailureKind.AdmissionDenied,
                "code_execution_credential_unavailable",
                "A source-readable NyxID credential or an exact workflow admission is required for code execution.",
                diagnosticId: localDiagnosticId));
        }

        NyxIdCodeExecutionRouteResolution resolution;
        try
        {
            resolution = await NyxIdCodeExecutionRouteResolver.ResolveAsync(
                    _clientFactory,
                    sourceReadableBearerToken,
                    request.Route.UserServiceId,
                    ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "Durable code execution route resolution failed. diagnosticId={DiagnosticId} exceptionType={ExceptionType}",
                localDiagnosticId,
                exception.GetType().Name);
            return new DurableRouteResolution(null, DurableFailure(
                DurableCodeExecutionFailureKind.TransportUnavailable,
                "code_execution_route_resolution_failed",
                "The code execution route could not be resolved.",
                retryable: true,
                retryAfter: DefaultDurableRetryDelay,
                diagnosticId: localDiagnosticId));
        }

        if (!resolution.IsReady)
        {
            var legacyFailure = RouteResolutionFailed(resolution, localDiagnosticId).Failure!;
            return new DurableRouteResolution(null, FromLegacyFailure(legacyFailure));
        }

        var service = resolution.Service!;
        return new DurableRouteResolution(
            new CodeExecutionRouteIdentity(
                service.Slug,
                service.Id,
                CodeExecutionRouteIdentitySource.NyxIdUserServiceCatalog),
            null);
    }

    private async Task<DurableExchangeOutcome> ExchangeKnownOperationAsync(
        string token,
        CodeExecutionRouteIdentity route,
        string path,
        string method,
        Dictionary<string, string>? extraHeaders,
        DurableExchange exchange,
        string localDiagnosticId,
        CancellationToken ct,
        string? body = null)
    {
        try
        {
            var response = await SendDurableProxyAsync(
                    token,
                    route,
                    path,
                    method,
                    body,
                    extraHeaders: extraHeaders,
                    ct: ct)
                .ConfigureAwait(false);
            return new DurableExchangeOutcome(response, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (DurablePublicApiNotConfiguredException)
        {
            return new DurableExchangeOutcome(null, PublicApiNotConfigured(localDiagnosticId));
        }
        catch (OperationCanceledException)
        {
            LogDurableFailure($"{ExchangeName(exchange)}_timeout", localDiagnosticId);
            return new DurableExchangeOutcome(null, DurableFailure(
                DurableCodeExecutionFailureKind.TimedOut,
                "durable_code_execution_request_timed_out",
                "The durable code execution request timed out.",
                retryable: true,
                retryAfter: DefaultDurableRetryDelay,
                diagnosticId: localDiagnosticId));
        }
        catch (Exception)
        {
            LogDurableFailure($"{ExchangeName(exchange)}_transport_exception", localDiagnosticId);
            return new DurableExchangeOutcome(null, DurableFailure(
                DurableCodeExecutionFailureKind.TransportUnavailable,
                "durable_code_execution_transport_unavailable",
                "The durable code execution transport is unavailable.",
                retryable: true,
                retryAfter: DefaultDurableRetryDelay,
                diagnosticId: localDiagnosticId));
        }
    }

    private async Task<NyxIdProxyTextResponse> SendDurableProxyAsync(
        string token,
        CodeExecutionRouteIdentity route,
        string path,
        string method,
        string? body,
        Dictionary<string, string>? extraHeaders,
        CancellationToken ct)
    {
        using var callCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        callCts.CancelAfter(DurableCallDeadline);
        var client = _clientFactory.CreateClient();
        if (!client.HasPublicApiEndpoint)
            throw new DurablePublicApiNotConfiguredException();

        // Preserve Agent Keys as bearer caller credentials so NyxID can forward them to
        // Chrono; X-API-Key authentication terminates the credential at the proxy boundary.
        return await client.ProxyPublicRequestBoundedAsync(
                token,
                route.ServiceSlug,
                route.UserServiceId!,
                path,
                method,
                body,
                extraHeaders,
                MaxResponseBytes,
                callCts.Token)
            .ConfigureAwait(false);
    }

    private DurableCodeExecutionFailure ClassifyDurableHttpFailure(
        NyxIdProxyTextResponse response,
        DurableExchange exchange,
        string localDiagnosticId)
    {
        if (response.Detail is "content_length_exceeds_max_bytes" or "content_exceeds_max_bytes")
        {
            if (exchange == DurableExchange.Submit &&
                IsAmbiguousSubmitStatus(response.HttpStatus))
            {
                LogDurableFailure("submit_ambiguous_response_unreadable", localDiagnosticId, response.HttpStatus);
                return SubmissionUncertain(
                    "durable_code_execution_submit_outcome_uncertain",
                    localDiagnosticId,
                    response.RetryAfter);
            }

            LogDurableFailure("response_too_large", localDiagnosticId, response.HttpStatus);
            return DurableFailure(
                DurableCodeExecutionFailureKind.ResponseTooLarge,
                "durable_code_execution_response_too_large",
                "Durable code execution returned an oversized response.",
                diagnosticId: localDiagnosticId);
        }

        var providerCode = ReadProviderErrorCode(response.Content);
        var diagnosticId = ReadProviderDiagnosticId(response.Content) ?? localDiagnosticId;
        var retryAfter = NormalizeRetryAfter(response.RetryAfter);
        LogDurableFailure(
            $"{ExchangeName(exchange)}_http_failure",
            diagnosticId,
            response.HttpStatus);

        if (response.HttpStatus == 0)
        {
            return exchange == DurableExchange.Submit
                ? SubmissionUncertain(
                    "durable_code_execution_submit_outcome_uncertain",
                    diagnosticId,
                    retryAfter)
                : DurableFailure(
                    DurableCodeExecutionFailureKind.TransportUnavailable,
                    "durable_code_execution_transport_unavailable",
                    "The durable code execution transport is unavailable.",
                    retryable: true,
                    retryAfter: retryAfter ?? DefaultDurableRetryDelay,
                    diagnosticId: diagnosticId);
        }

        return response.HttpStatus switch
        {
            (int)HttpStatusCode.Unauthorized or (int)HttpStatusCode.Forbidden => DurableFailure(
                DurableCodeExecutionFailureKind.AdmissionDenied,
                response.HttpStatus == (int)HttpStatusCode.Unauthorized
                    ? "NYXID_PROXY_UNAUTHORIZED"
                    : "NYXID_PROXY_FORBIDDEN",
                "The NyxID proxy denied durable code execution.",
                diagnosticId: diagnosticId),
            (int)HttpStatusCode.NotFound => DurableFailure(
                exchange == DurableExchange.Submit
                    ? DurableCodeExecutionFailureKind.TargetNotConfigured
                    : DurableCodeExecutionFailureKind.OperationNotFound,
                exchange == DurableExchange.Submit
                    ? "durable_code_execution_target_not_found"
                    : "durable_code_execution_operation_not_found",
                exchange == DurableExchange.Submit
                    ? "The durable code execution target is unavailable."
                    : "The durable code execution operation is unavailable or is not owned by this caller.",
                diagnosticId: diagnosticId),
            (int)HttpStatusCode.RequestTimeout or (int)HttpStatusCode.GatewayTimeout
                when exchange == DurableExchange.Submit => SubmissionUncertain(
                    "durable_code_execution_submit_outcome_uncertain",
                    diagnosticId,
                    retryAfter),
            (int)HttpStatusCode.RequestTimeout or (int)HttpStatusCode.GatewayTimeout => DurableFailure(
                DurableCodeExecutionFailureKind.TimedOut,
                "durable_code_execution_request_timed_out",
                "The durable code execution request timed out.",
                retryable: true,
                retryAfter: retryAfter ?? DefaultDurableRetryDelay,
                diagnosticId: diagnosticId),
            (int)HttpStatusCode.Conflict when
                exchange == DurableExchange.Submit &&
                string.Equals(providerCode, "IDEMPOTENCY_KEY_REUSE", StringComparison.Ordinal) => DurableFailure(
                    DurableCodeExecutionFailureKind.IdempotencyConflict,
                    "IDEMPOTENCY_KEY_REUSE",
                    "The durable execution key was already used for different work.",
                    diagnosticId: diagnosticId),
            (int)HttpStatusCode.Gone => DurableFailure(
                DurableCodeExecutionFailureKind.Expired,
                "OPERATION_EXPIRED",
                "The durable code execution operation has expired.",
                diagnosticId: diagnosticId),
            (int)HttpStatusCode.TooManyRequests => DurableFailure(
                DurableCodeExecutionFailureKind.RateLimited,
                providerCode ?? "EXECUTION_CAPACITY_EXCEEDED",
                "Durable code execution capacity is temporarily exhausted.",
                retryable: true,
                retryAfter: retryAfter ?? DefaultDurableRetryDelay,
                diagnosticId: diagnosticId),
            (int)HttpStatusCode.ServiceUnavailable => DurableFailure(
                DurableCodeExecutionFailureKind.ServiceUnavailable,
                providerCode ?? "ASYNC_EXECUTION_UNAVAILABLE",
                "Durable code execution is temporarily unavailable.",
                retryable: true,
                retryAfter: retryAfter ?? DefaultDurableRetryDelay,
                diagnosticId: diagnosticId),
            >= (int)HttpStatusCode.InternalServerError when exchange == DurableExchange.Submit =>
                SubmissionUncertain(
                    providerCode ?? "durable_code_execution_submit_outcome_uncertain",
                    diagnosticId,
                    retryAfter),
            >= (int)HttpStatusCode.InternalServerError => DurableFailure(
                DurableCodeExecutionFailureKind.ServiceUnavailable,
                providerCode ?? $"NYXID_PROXY_HTTP_{response.HttpStatus}",
                "Durable code execution is temporarily unavailable.",
                retryable: true,
                retryAfter: retryAfter ?? DefaultDurableRetryDelay,
                diagnosticId: diagnosticId),
            (int)HttpStatusCode.RequestEntityTooLarge => DurableFailure(
                DurableCodeExecutionFailureKind.ResponseTooLarge,
                providerCode ?? "EXECUTION_PAYLOAD_TOO_LARGE",
                "The durable code execution request is too large.",
                diagnosticId: diagnosticId),
            _ => DurableFailure(
                DurableCodeExecutionFailureKind.ProviderRejected,
                providerCode ?? $"NYXID_PROXY_HTTP_{response.HttpStatus}",
                "The durable code execution request was rejected.",
                diagnosticId: diagnosticId),
        };
    }

    private static bool IsAmbiguousSubmitStatus(int httpStatus) =>
        httpStatus == 0 ||
        httpStatus is (int)HttpStatusCode.RequestTimeout or (int)HttpStatusCode.TooManyRequests ||
        httpStatus >= (int)HttpStatusCode.InternalServerError;

    private static bool TryParseReceipt(
        string content,
        CodeExecutionRouteIdentity route,
        TimeSpan? retryAfter,
        out DurableCodeExecutionReceipt receipt)
    {
        receipt = null!;
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;
            var operationId = ReadString(root, "operation_id");
            if (!IsValidProviderOperationId(operationId) ||
                !TryReadState(root, out var state) ||
                !TryReadDateTimeOffset(root, "created_at", out var createdAt) ||
                !TryReadDateTimeOffset(root, "expires_at", out var expiresAt) ||
                expiresAt < createdAt)
            {
                return false;
            }

            receipt = new DurableCodeExecutionReceipt(
                operationId!,
                StatusPath(operationId!),
                ResultPath(operationId!),
                CancelPath(operationId!),
                state,
                route,
                createdAt,
                expiresAt,
                NormalizeRetryAfter(retryAfter) ?? DefaultDurableRetryDelay);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryParseSnapshot(
        string content,
        string expectedOperationId,
        CodeExecutionRouteIdentity route,
        string? etag,
        TimeSpan? retryAfter,
        out DurableCodeExecutionSnapshot snapshot)
    {
        snapshot = null!;
        if (!IsValidETag(etag))
            return false;

        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;
            var operationId = ReadString(root, "operation_id");
            if (!string.Equals(operationId, expectedOperationId, StringComparison.Ordinal) ||
                !TryReadState(root, out var state) ||
                !TryReadPhase(root, out var phase) ||
                !TryReadCleanupState(root, out var cleanupState) ||
                !TryReadInt64(root, "version", out var version) || version < 0 ||
                !TryReadBoolean(root, "cancel_requested", out var cancelRequested) ||
                !TryReadBoolean(root, "result_available", out var resultAvailable) ||
                !TryReadDateTimeOffset(root, "created_at", out var createdAt) ||
                !TryReadDateTimeOffset(root, "updated_at", out var updatedAt) ||
                !TryReadDateTimeOffset(root, "expires_at", out var expiresAt) ||
                expiresAt < createdAt)
            {
                return false;
            }

            DateTimeOffset? terminalAt = null;
            if (root.TryGetProperty("terminal_at", out var terminalAtElement) &&
                terminalAtElement.ValueKind != JsonValueKind.Null)
            {
                if (!TryReadDateTimeOffset(root, "terminal_at", out var parsedTerminalAt))
                    return false;
                terminalAt = parsedTerminalAt;
            }

            DurableCodeExecutionProviderFailure? providerFailure = null;
            if (root.TryGetProperty("failure", out var failureElement) &&
                failureElement.ValueKind != JsonValueKind.Null)
            {
                if (failureElement.ValueKind != JsonValueKind.Object)
                    return false;
                var code = ReadProviderErrorCode(failureElement);
                if (code is null)
                    return false;
                providerFailure = new DurableCodeExecutionProviderFailure(
                    code,
                    "The provider reported an execution failure.");
            }

            var terminal = IsTerminal(state);
            snapshot = new DurableCodeExecutionSnapshot(
                expectedOperationId,
                state,
                phase,
                cleanupState,
                version,
                cancelRequested,
                resultAvailable,
                route,
                etag!,
                createdAt,
                updatedAt,
                expiresAt,
                terminalAt,
                terminal ? null : NormalizeRetryAfter(retryAfter) ?? DefaultDurableRetryDelay,
                providerFailure);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryParseTerminalResultWithoutOutput(
        string content,
        string localDiagnosticId,
        out DurableCodeExecutionFailure? failure)
    {
        failure = null;
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("success", out var success) ||
                success.ValueKind != JsonValueKind.False ||
                root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Object)
            {
                return false;
            }

            var code = ReadProviderErrorCode(root);
            if (code is null)
            {
                failure = InvalidDurableResponse(
                    "durable_code_execution_result_invalid",
                    localDiagnosticId);
                return true;
            }

            var diagnosticId = ReadProviderDiagnosticId(root) ?? localDiagnosticId;
            var providerPhase = ReadProviderFailurePhase(root);
            failure = code switch
            {
                "EXECUTION_CANCELLED" => DurableFailure(
                    DurableCodeExecutionFailureKind.Cancelled,
                    code,
                    "Durable code execution was cancelled.",
                    diagnosticId: diagnosticId,
                    providerPhase: providerPhase),
                "OUTCOME_UNCERTAIN" => DurableFailure(
                    DurableCodeExecutionFailureKind.OutcomeUncertain,
                    code,
                    "The durable code execution outcome could not be determined safely.",
                    diagnosticId: diagnosticId,
                    providerPhase: providerPhase),
                _ => DurableFailure(
                    DurableCodeExecutionFailureKind.ExecutionFailed,
                    code,
                    "Durable code execution failed before producing a result.",
                    diagnosticId: diagnosticId,
                    providerPhase: providerPhase),
            };
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryValidateOperationRequest(
        DurableCodeExecutionOperationRequest? request,
        out string? executionCredential,
        out DurableCodeExecutionFailure? failure)
    {
        var caller = request?.Caller;
        executionCredential = NormalizeCredential(
            caller?.ExecutionNyxIdCredential);
        if (request is null ||
            caller is null ||
            !IsValidProviderOperationId(request.ProviderOperationId) ||
            !IsValidResolvedRoute(request.Route) ||
            !IsValidExecutionCredentialKind(caller.ExecutionCredentialKind) ||
            executionCredential is null ||
            request.ETag is not null && !IsValidETag(request.ETag))
        {
            failure = DurableFailure(
                DurableCodeExecutionFailureKind.AdmissionDenied,
                "durable_code_execution_operation_request_invalid",
                "The durable code execution operation request is invalid.");
            return false;
        }

        if (caller.ExecutionCredentialKind == CodeExecutionNyxIdCredentialKind.AgentKey)
        {
            failure = DurableFailure(
                DurableCodeExecutionFailureKind.AdmissionDenied,
                "code_execution_durable_lifecycle_authority_unavailable",
                "The scheduled NyxID credential does not carry producer-issued status, result, and cancel authority.");
            return false;
        }

        failure = null;
        return true;
    }

    private static bool IsValidExecutionRequest(CodeExecutionRequest? request) =>
        request is not null &&
        Enum.IsDefined(request.Language) &&
        request.Language != CodeExecutionLanguage.Unspecified &&
        !string.IsNullOrWhiteSpace(request.Source) &&
        CodeExecutionContract.IsValidTimeoutSeconds(request.TimeoutSeconds) &&
        request.Route is not null &&
        IsValidExecutionCredentialKind(request.Caller?.ExecutionCredentialKind) &&
        IsValidRequestedRoute(request.Route);

    private bool IsValidSubmitGrant(
        Aevatar.Foundation.Abstractions.Credentials.NyxIdDurableOperationGrantRef? grant,
        CodeExecutionRouteIdentity route)
    {
        var nowUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        return
        grant is not null &&
        IsNormalizedGrantValue(grant.GrantId) &&
        IsNormalizedGrantValue(grant.ApiKeyId) &&
        string.Equals(grant.UserServiceId, route.UserServiceId, StringComparison.Ordinal) &&
        grant.HttpMethod == Aevatar.Foundation.Abstractions.Credentials.NyxIdDurableOperationHttpMethod.Post &&
        string.Equals(grant.NormalizedPathTemplate, DurableSubmitPath, StringComparison.Ordinal) &&
        IsNormalizedGrantValue(grant.EndpointId) &&
        IsValidContractDigest(grant.ContractDigest) &&
        grant.ReplayPolicy ==
            Aevatar.Foundation.Abstractions.Credentials.NyxIdDurableOperationReplayPolicy.DownstreamIdempotencyKey &&
        grant.ValidFromUnixMs > 0 &&
        grant.ExpiresAtUnixMs > grant.ValidFromUnixMs &&
        grant.ValidFromUnixMs <= nowUnixMs &&
        grant.ExpiresAtUnixMs > nowUnixMs &&
        IsValidAuditBinding(grant.ClientAuditBinding);
    }

    private static bool IsNormalizedGrantValue(string? value) =>
        value is { Length: > 0 and <= 256 } &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
        value.All(static character => character is >= '!' and <= '~');

    private static bool IsValidContractDigest(string? value) =>
        value is { Length: 71 } &&
        value.StartsWith("sha256:", StringComparison.Ordinal) &&
        value.AsSpan(7).ToArray().All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsValidAuditBinding(
        Aevatar.Foundation.Abstractions.Credentials.NyxIdDurableOperationClientAuditBinding? binding) =>
        binding is null ||
        IsValidOptionalAuditValue(binding.Platform) &&
        IsValidOptionalAuditValue(binding.ScheduleId) &&
        IsValidOptionalAuditValue(binding.WorkflowRevision) &&
        IsValidOptionalAuditValue(binding.CallSite);

    private static bool IsValidOptionalAuditValue(string? value) =>
        string.IsNullOrEmpty(value) || IsNormalizedGrantValue(value);

    private static bool IsValidResolvedRoute(CodeExecutionRouteIdentity? route) =>
        route is not null &&
        CodeExecutionContract.IsSupportedServiceSlug(route.ServiceSlug) &&
        !string.IsNullOrWhiteSpace(route.UserServiceId) &&
        string.Equals(route.UserServiceId, route.UserServiceId.Trim(), StringComparison.Ordinal) &&
        !route.UserServiceId.Any(char.IsControl) &&
        route.Source is CodeExecutionRouteIdentitySource.NyxIdUserServiceCatalog or
            CodeExecutionRouteIdentitySource.WorkflowCapabilityAdmission;

    private static bool IsValidIdempotencyKey(string? value) =>
        value is { Length: > 0 and <= 128 } &&
        !value.Contains(',') &&
        value.All(static character => character is >= '!' and <= '~');

    private static bool IsValidDurableOperationId(string? value)
    {
        const string prefix = "tool:v1:operation:";
        return IsValidIdempotencyKey(value) &&
               value!.Length == prefix.Length + 64 &&
               value.StartsWith(prefix, StringComparison.Ordinal) &&
               value.AsSpan(prefix.Length).ToArray().All(static character =>
                   character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    private static bool IsValidProviderOperationId(string? value) =>
        value is { Length: 35 } &&
        value.StartsWith("op_", StringComparison.Ordinal) &&
        IsValidProviderOperationIdCharacters(value);

    private static bool IsValidProviderOperationIdCharacters(string value) =>
        value.AsSpan(3).ToArray().All(static character =>
            char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    private static bool IsValidETag(string? value) =>
        value is { Length: > 0 and <= 512 } &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
        !value.Any(char.IsControl);

    private static string StatusPath(string operationId) =>
        $"/executions/{Uri.EscapeDataString(operationId)}";

    private static string ResultPath(string operationId) =>
        $"{StatusPath(operationId)}/result";

    private static string CancelPath(string operationId) =>
        $"{StatusPath(operationId)}/cancel";

    private static bool TryReadState(JsonElement root, out DurableCodeExecutionState state)
    {
        state = ReadString(root, "status") switch
        {
            "queued" => DurableCodeExecutionState.Queued,
            "provisioning" => DurableCodeExecutionState.Provisioning,
            "preparing" => DurableCodeExecutionState.Preparing,
            "running" => DurableCodeExecutionState.Running,
            "collecting" => DurableCodeExecutionState.Collecting,
            "succeeded" => DurableCodeExecutionState.Succeeded,
            "failed" => DurableCodeExecutionState.Failed,
            "cancelled" => DurableCodeExecutionState.Cancelled,
            "outcome_uncertain" => DurableCodeExecutionState.OutcomeUncertain,
            _ => DurableCodeExecutionState.Unspecified,
        };
        return state != DurableCodeExecutionState.Unspecified;
    }

    private static bool TryReadPhase(JsonElement root, out DurableCodeExecutionPhase phase)
    {
        phase = ReadString(root, "phase") switch
        {
            "queued" => DurableCodeExecutionPhase.Queued,
            "sandbox_create" => DurableCodeExecutionPhase.SandboxCreate,
            "sandbox_ready" => DurableCodeExecutionPhase.SandboxReady,
            "input_write" => DurableCodeExecutionPhase.InputWrite,
            "dependency_install" => DurableCodeExecutionPhase.DependencyInstall,
            "execute" => DurableCodeExecutionPhase.Execute,
            "collect" => DurableCodeExecutionPhase.Collect,
            "cleaning_up" => DurableCodeExecutionPhase.CleaningUp,
            "complete" => DurableCodeExecutionPhase.Complete,
            _ => DurableCodeExecutionPhase.Unspecified,
        };
        return phase != DurableCodeExecutionPhase.Unspecified;
    }

    private static DurableCodeExecutionPhase ReadProviderFailurePhase(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("error", out var error) ||
            error.ValueKind != JsonValueKind.Object ||
            !TryReadPhase(error, out var phase))
        {
            return DurableCodeExecutionPhase.Unspecified;
        }

        return phase is DurableCodeExecutionPhase.SandboxCreate or
            DurableCodeExecutionPhase.SandboxReady or
            DurableCodeExecutionPhase.InputWrite or
            DurableCodeExecutionPhase.DependencyInstall or
            DurableCodeExecutionPhase.Execute or
            DurableCodeExecutionPhase.Collect or
            DurableCodeExecutionPhase.CleaningUp
                ? phase
                : DurableCodeExecutionPhase.Unspecified;
    }

    private static bool TryReadCleanupState(
        JsonElement root,
        out DurableCodeExecutionCleanupState cleanupState)
    {
        cleanupState = ReadString(root, "cleanup_status") switch
        {
            "not_started" => DurableCodeExecutionCleanupState.NotStarted,
            "pending" => DurableCodeExecutionCleanupState.Pending,
            "running" => DurableCodeExecutionCleanupState.Running,
            "retry" => DurableCodeExecutionCleanupState.Retry,
            "complete" => DurableCodeExecutionCleanupState.Complete,
            _ => DurableCodeExecutionCleanupState.Unspecified,
        };
        return cleanupState != DurableCodeExecutionCleanupState.Unspecified;
    }

    private static bool TryReadDateTimeOffset(
        JsonElement root,
        string name,
        out DateTimeOffset value)
    {
        value = default;
        var text = ReadString(root, name);
        return text is not null && DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out value);
    }

    private static bool TryReadInt64(JsonElement root, string name, out long value)
    {
        value = 0;
        return root.TryGetProperty(name, out var element) && element.TryGetInt64(out value);
    }

    private static bool TryReadBoolean(JsonElement root, string name, out bool value)
    {
        value = false;
        if (!root.TryGetProperty(name, out var element) ||
            element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        value = element.GetBoolean();
        return true;
    }

    private static bool IsTerminal(DurableCodeExecutionState state) =>
        state is DurableCodeExecutionState.Succeeded or
            DurableCodeExecutionState.Failed or
            DurableCodeExecutionState.Cancelled or
            DurableCodeExecutionState.OutcomeUncertain;

    private static string? ReadProviderErrorCode(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return null;
        try
        {
            using var document = JsonDocument.Parse(content);
            return ReadProviderErrorCode(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadProviderErrorCode(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;
        var owner = root;
        if (root.TryGetProperty("error", out var error) &&
            error.ValueKind == JsonValueKind.Object)
        {
            owner = error;
        }

        return SanitizeProviderCode(ReadString(owner, "code"));
    }

    private static string? ReadProviderDiagnosticId(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return null;
        try
        {
            using var document = JsonDocument.Parse(content);
            return ReadProviderDiagnosticId(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadProviderDiagnosticId(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object
            ? ChronoProxyFailureInspector.SanitizeDiagnosticId(ReadString(root, "diagnostic_id"))
            : null;

    private static string? SanitizeProviderCode(string? value) =>
        value is { Length: > 0 and <= 64 } &&
        value.All(static character =>
            char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.')
            ? value
            : null;

    private static TimeSpan? NormalizeRetryAfter(TimeSpan? value)
    {
        if (value is null)
            return null;
        if (value <= TimeSpan.Zero)
            return TimeSpan.Zero;
        return value > MaxDurableRetryDelay ? MaxDurableRetryDelay : value;
    }

    private static DurableCodeExecutionFailure FromLegacyFailure(CodeExecutionFailure failure) =>
        DurableFailure(
            failure.Kind switch
            {
                CodeExecutionFailureKind.AdmissionDenied => DurableCodeExecutionFailureKind.AdmissionDenied,
                CodeExecutionFailureKind.TargetNotConfigured => DurableCodeExecutionFailureKind.TargetNotConfigured,
                CodeExecutionFailureKind.TimedOut => DurableCodeExecutionFailureKind.TimedOut,
                CodeExecutionFailureKind.ResponseTooLarge => DurableCodeExecutionFailureKind.ResponseTooLarge,
                CodeExecutionFailureKind.MalformedOutput => DurableCodeExecutionFailureKind.MalformedOutput,
                CodeExecutionFailureKind.ExecutionFailed => DurableCodeExecutionFailureKind.ExecutionFailed,
                _ => DurableCodeExecutionFailureKind.TransportUnavailable,
            },
            failure.Code,
            failure.Message,
            retryable: failure.Kind is CodeExecutionFailureKind.TransportUnavailable or CodeExecutionFailureKind.TimedOut,
            retryAfter: failure.Kind is CodeExecutionFailureKind.TransportUnavailable or CodeExecutionFailureKind.TimedOut
                ? DefaultDurableRetryDelay
                : null,
            diagnosticId: failure.DiagnosticId,
            providerPhase: failure.ProviderPhase);

    private static DurableCodeExecutionFailure SubmissionUncertain(
        string code,
        string diagnosticId,
        TimeSpan? retryAfter = null) =>
        DurableFailure(
            DurableCodeExecutionFailureKind.SubmissionUncertain,
            code,
            "The durable submission response was not observed; retry the same request only with the same idempotency key.",
            retryable: true,
            retryAfter: NormalizeRetryAfter(retryAfter) ?? DefaultDurableRetryDelay,
            diagnosticId: diagnosticId);

    private static DurableCodeExecutionFailure InvalidDurableResponse(
        string code,
        string diagnosticId) =>
        DurableFailure(
            DurableCodeExecutionFailureKind.MalformedOutput,
            code,
            "Durable code execution returned an invalid response.",
            diagnosticId: diagnosticId);

    private static DurableCodeExecutionFailure PublicApiNotConfigured(string diagnosticId) =>
        DurableFailure(
            DurableCodeExecutionFailureKind.TargetNotConfigured,
            "durable_code_execution_public_api_not_configured",
            "The public NyxID API endpoint is not configured for durable code execution.",
            diagnosticId: diagnosticId);

    private static DurableCodeExecutionFailure DurableFailure(
        DurableCodeExecutionFailureKind kind,
        string code,
        string message,
        bool retryable = false,
        TimeSpan? retryAfter = null,
        string? diagnosticId = null,
        DurableCodeExecutionPhase providerPhase = DurableCodeExecutionPhase.Unspecified) =>
        new(kind, code, message, retryable, retryAfter, diagnosticId, providerPhase);

    private static DurableCodeExecutionSubmitOutcome SubmitFailed(
        DurableCodeExecutionFailure failure) => new(null, failure);

    private static DurableCodeExecutionStatusOutcome StatusFailed(
        DurableCodeExecutionFailure failure) => new(null, false, null, failure.RetryAfter, failure);

    private static DurableCodeExecutionResultOutcome ResultFailed(
        DurableCodeExecutionFailure failure) => new(null, false, failure.RetryAfter, failure);

    private static DurableCodeExecutionCancelOutcome CancelFailed(
        DurableCodeExecutionFailure failure) => new(null, failure);

    private void LogDurableFailure(
        string failureKind,
        string diagnosticId,
        int status = 0) =>
        _logger.LogWarning(
            "Durable code execution proxy failure. status={Status} diagnosticId={DiagnosticId} failureKind={FailureKind}",
            status,
            diagnosticId,
            failureKind);

    private static string ExchangeName(DurableExchange exchange) => exchange switch
    {
        DurableExchange.Submit => "submit",
        DurableExchange.Status => "status",
        DurableExchange.Result => "result",
        DurableExchange.Cancel => "cancel",
        _ => "unknown",
    };

    private enum DurableExchange
    {
        Submit,
        Status,
        Result,
        Cancel,
    }

    private sealed record DurableRouteResolution(
        CodeExecutionRouteIdentity? Route,
        DurableCodeExecutionFailure? Failure);

    private sealed record DurableExchangeOutcome(
        NyxIdProxyTextResponse? Response,
        DurableCodeExecutionFailure? Failure);

    private sealed class DurablePublicApiNotConfiguredException : InvalidOperationException;
}
