using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.CodeExecution;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions.Credentials;

namespace Aevatar.AI.ToolProviders.NyxId.Tools;

/// <summary>
/// Executes caller-provided source through the runtime-neutral code execution boundary.
/// </summary>
public sealed class NyxIdCodeExecuteTool(
    ICodeExecutionPort executionPort,
    IDurableCodeExecutionPort? durableExecutionPort = null,
    TimeProvider? timeProvider = null) :
    INyxIdBuiltInTool,
    IAgentToolDurableOperation
{
    private static readonly TimeSpan SubmitRecoveryWindow = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromSeconds(1);

    private static readonly HashSet<string> CompletedFailureCodes = new(StringComparer.Ordinal)
    {
        "code_execution_failed",
        "DEPENDENCY_INSTALL_FAILED",
        "EXECUTION_FAILED",
    };

    private static readonly HashSet<string> PreExecutionFailureCodes = new(StringComparer.Ordinal)
    {
        "code_execution_credential_unavailable",
        "code_execution_admission_invalid",
        "code_execution_cancel_outcome_uncertain",
        "code_execution_cancelled",
        "code_execution_durable_context_invalid",
        "code_execution_durable_lifecycle_authority_unavailable",
        "code_execution_durable_transport_unavailable",
        "code_execution_outcome_uncertain",
        "code_execution_outcome_invalid",
        "code_execution_request_invalid",
        "code_execution_response_invalid",
        "code_execution_response_too_large",
        "code_execution_route_access_denied",
        "code_execution_route_ambiguous",
        "code_execution_route_inactive",
        "code_execution_route_missing",
        "code_execution_route_policy_mismatch",
        "code_execution_route_resolution_failed",
        "code_execution_submit_recovery_expired",
        "code_execution_timed_out",
        "code_execution_transport_unavailable",
        "FORBIDDEN",
        "INTERNAL_ERROR",
        "INVALID_REQUEST",
        "NYXID_PROXY_FORBIDDEN",
        "NYXID_PROXY_HTTP_404",
        "NYXID_PROXY_HTTP_429",
        "NYXID_PROXY_HTTP_502",
        "NYXID_PROXY_UNAUTHORIZED",
        "OPERATION_EXPIRED",
        "SANDBOX_CREATION_FAILED",
        "SANDBOX_TIMEOUT",
        "SANDBOX_UNREACHABLE",
        "UNAUTHENTICATED",
    };

    private static readonly HashSet<string> OutcomeUncertainFailureCodes = new(StringComparer.Ordinal)
    {
        "code_execution_cancel_outcome_uncertain",
        "code_execution_outcome_uncertain",
        "code_execution_submit_recovery_expired",
        "OPERATION_EXPIRED",
    };

    private readonly ICodeExecutionPort _executionPort =
        executionPort ?? throw new ArgumentNullException(nameof(executionPort));
    private readonly IDurableCodeExecutionPort? _durableExecutionPort =
        durableExecutionPort ?? executionPort as IDurableCodeExecutionPort;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public string Name => "code_execute";

    public string Description =>
        "Execute caller-provided exact source code in a one-shot remote code runtime. " +
        "Supports Python, JavaScript, TypeScript, and Bash. " +
        "Returns stdout, stderr, and exit code. " +
        "Use it when the caller supplied an explicit program; use codex_exec to delegate a natural-language task to an agent.";

    public ToolApprovalMode ApprovalMode => ToolApprovalMode.NeverRequire;

    public bool IsReadOnly => true;

    // Direct sessions preserve the legacy one-shot contract. Workflow tool calls use
    // provider idempotency plus actor-owned reconciliation and never fall back to /execute.
    public AgentToolReplayPolicy ResolveReplayPolicy(string argumentsJson) =>
        AgentToolRequestContext.Current?.InvocationSurface == AgentToolInvocationSurface.WorkflowToolCall
            ? AgentToolReplayPolicy.Reconcilable
            : AgentToolReplayPolicy.NonReplayable;

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "language": {
              "type": "string",
              "enum": ["python", "javascript", "typescript", "bash"],
              "description": "Programming language to execute"
            },
            "code": {
              "type": "string",
              "description": "Code to execute"
            },
            "timeout_secs": {
              "type": "integer",
              "minimum": 1,
              "maximum": 600,
              "default": 180,
              "description": "Maximum script execution time in seconds"
            }
          },
          "required": ["language", "code"]
        }
        """;

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
        (await ExecuteCoreAsync(string.Empty, Name, argumentsJson, ct).ConfigureAwait(false)).ResultJson;

    public Task<AgentToolTerminalOutcome> ExecuteWithOutcomeAsync(
        string callId,
        string toolName,
        string argumentsJson,
        CancellationToken ct = default) =>
        ExecuteCoreAsync(callId, toolName, argumentsJson, ct);

    public async Task<AgentToolOperationStartResult> StartOperationAsync(
        AgentToolOperationStartRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (AgentToolRequestContext.Current?.InvocationSurface !=
            AgentToolInvocationSurface.WorkflowToolCall)
        {
            return AgentToolOperationStartResult.Completed(TerminalFailure(
                request.CallId,
                request.ToolName,
                new CodeExecutionFailure(
                    CodeExecutionFailureKind.AdmissionDenied,
                    "code_execution_durable_context_invalid",
                    "Durable code execution requires an actor-owned workflow tool call.")));
        }

        if (!IsOpaqueOperationId(request.OperationId))
        {
            return AgentToolOperationStartResult.Completed(TerminalFailure(
                request.CallId,
                request.ToolName,
                new CodeExecutionFailure(
                    CodeExecutionFailureKind.AdmissionDenied,
                    "code_execution_admission_invalid",
                    "The workflow code execution operation identity is invalid.")));
        }

        var preparation = PrepareExecution(request.ArgumentsJson);
        if (preparation.Failure is not null)
        {
            return AgentToolOperationStartResult.Completed(
                TerminalFailure(request.CallId, request.ToolName, preparation.Failure));
        }

        if (preparation.Request!.Caller.ExecutionCredentialKind ==
            CodeExecutionNyxIdCredentialKind.AgentKey)
        {
            return AgentToolOperationStartResult.Completed(TerminalFailure(
                request.CallId,
                request.ToolName,
                new CodeExecutionFailure(
                    CodeExecutionFailureKind.AdmissionDenied,
                    "code_execution_durable_lifecycle_authority_unavailable",
                    "The scheduled NyxID credential does not carry producer-issued status, result, and cancel authority.")));
        }

        if (_durableExecutionPort is null)
        {
            return AgentToolOperationStartResult.Completed(TerminalFailure(
                request.CallId,
                request.ToolName,
                new CodeExecutionFailure(
                    CodeExecutionFailureKind.TargetNotConfigured,
                    "code_execution_durable_transport_unavailable",
                    "Durable code execution transport is not configured.")));
        }

        var outcome = await _durableExecutionPort.SubmitAsync(
                new DurableCodeExecutionSubmitRequest(preparation.Request, request.OperationId),
                ct)
            .ConfigureAwait(false);
        if (outcome.Receipt is { } receipt && outcome.Failure is null)
        {
            return AgentToolOperationStartResult.Pending(
                ToPendingOperation(request.OperationId, receipt));
        }

        if (outcome.Receipt is null && IsRecoverableSubmitFailure(outcome.Failure))
        {
            var now = _timeProvider.GetUtcNow();
            return AgentToolOperationStartResult.Pending(new AgentToolPendingOperation(
                request.OperationId,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                AgentToolPendingOperationStatus.SubmissionUncertain,
                null,
                RetryAfterMilliseconds(outcome.Failure!.RetryAfter, DefaultRetryAfter),
                now.Add(SubmitRecoveryWindow).ToUnixTimeMilliseconds(),
                preparation.Request!.Route.ServiceSlug,
                preparation.Request.Route.UserServiceId,
                preparation.Request.Route.Source));
        }

        return AgentToolOperationStartResult.Completed(TerminalFailure(
            request.CallId,
            request.ToolName,
            outcome.Failure is null
                ? InvalidDurableOutcomeFailure()
                : ToCodeExecutionFailure(outcome.Failure)));
    }

    public async Task<AgentToolOperationReconciliationResult> ReconcileOperationAsync(
        AgentToolOperationReconciliationRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var pending = request.PendingOperation;
        var callId = request.ExecutionContext.Request.CallId ?? string.Empty;
        if (!IsOpaqueOperationId(request.OperationId))
            return AgentToolOperationReconciliationResult.Unknown();

        if (pending is not null &&
            !string.Equals(pending.OperationId, request.OperationId, StringComparison.Ordinal))
        {
            return AgentToolOperationReconciliationResult.Unknown();
        }

        var preparation = PrepareExecution(request.ArgumentsJson);
        if (preparation.Failure is not null)
        {
            return AgentToolOperationReconciliationResult.Completed(
                TerminalFailure(callId, Name, preparation.Failure));
        }

        var executionRequest = preparation.Request!;
        CodeExecutionRouteIdentity? pendingRoute = null;
        if (pending is not null &&
            (!TryResolvePendingRoute(pending, out pendingRoute) ||
             !IsCompatiblePendingRoute(pendingRoute, executionRequest.Route)))
        {
            return AgentToolOperationReconciliationResult.Completed(TerminalFailure(
                callId,
                Name,
                new CodeExecutionFailure(
                    CodeExecutionFailureKind.AdmissionDenied,
                    "code_execution_admission_invalid",
                    "The pending code execution route does not match its current admission proof.")));
        }

        if (_durableExecutionPort is null)
        {
            return AgentToolOperationReconciliationResult.Completed(TerminalFailure(
                callId,
                Name,
                new CodeExecutionFailure(
                    CodeExecutionFailureKind.TargetNotConfigured,
                    "code_execution_durable_transport_unavailable",
                    "Durable code execution transport is not configured.")));
        }

        var route = pendingRoute ?? executionRequest.Route;
        if (pending is null || string.IsNullOrWhiteSpace(pending.ProviderOperationId))
        {
            return await RecoverSubmissionAsync(request, pending, executionRequest, callId, ct)
                .ConfigureAwait(false);
        }

        var operationRequest = new DurableCodeExecutionOperationRequest(
            pending.ProviderOperationId,
            route,
            executionRequest.Caller,
            pending.ETag);
        if (IsExpired(pending))
        {
            return await ExpireKnownOperationAsync(callId, operationRequest, ct)
                .ConfigureAwait(false);
        }

        return await ReconcileKnownOperationAsync(callId, pending, operationRequest, route, ct)
            .ConfigureAwait(false);
    }

    public async Task<AgentToolOperationCancellationResult> CancelOperationAsync(
        AgentToolOperationCancellationRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var pending = request.PendingOperation;
        var callId = request.ExecutionContext.Request.CallId ?? string.Empty;
        if (request.Reason != AgentToolOperationCancellationReason.WorkflowStopped ||
            AgentToolRequestContext.Current?.InvocationSurface != AgentToolInvocationSurface.WorkflowToolCall ||
            !IsOpaqueOperationId(request.OperationId) ||
            !string.Equals(pending.OperationId, request.OperationId, StringComparison.Ordinal))
        {
            return CancellationPendingOrUncertain(request, pending, callId);
        }

        var preparation = PrepareExecution(request.ArgumentsJson);
        if (preparation.Failure is not null)
            return CancellationPendingOrUncertain(request, pending, callId);
        if (!TryResolvePendingRoute(pending, out var route) ||
            !IsCompatiblePendingRoute(route, preparation.Request!.Route))
        {
            return CancellationPendingOrUncertain(request, pending, callId);
        }

        if (_durableExecutionPort is null || string.IsNullOrWhiteSpace(pending.ProviderOperationId))
            return CancellationPendingOrUncertain(request, pending, callId);

        var operationRequest = new DurableCodeExecutionOperationRequest(
            pending.ProviderOperationId,
            route,
            preparation.Request.Caller);
        var cancelled = await _durableExecutionPort.CancelAsync(operationRequest, ct)
            .ConfigureAwait(false);
        if (cancelled.Failure is { } cancellationFailure)
        {
            return CancellationPendingOrUncertain(
                request,
                UpdatePending(
                    pending,
                    retryAfter: cancellationFailure.RetryAfter),
                callId);
        }

        if (cancelled.Snapshot is not { } snapshot ||
            !string.Equals(snapshot.ProviderOperationId, pending.ProviderOperationId, StringComparison.Ordinal) ||
            !Equals(snapshot.ResolvedRoute, route))
        {
            return CancellationPendingOrUncertain(request, pending, callId);
        }

        var refreshed = UpdatePending(
            pending,
            status: ToPendingOperationStatus(snapshot.State),
            etag: snapshot.ETag,
            retryAfter: snapshot.RetryAfter,
            expiresAt: snapshot.ExpiresAt,
            route: snapshot.ResolvedRoute);
        var terminal = await ResolveCancellationTerminalAsync(
                callId,
                snapshot,
                operationRequest,
                route,
                ct)
            .ConfigureAwait(false);
        if (terminal.CompletedOutcome is not null)
            return AgentToolOperationCancellationResult.Completed(terminal.CompletedOutcome);

        return CancellationPendingOrUncertain(
            request,
            UpdatePending(refreshed, retryAfter: terminal.RetryAfter),
            callId);
    }

    private async Task<AgentToolOperationReconciliationResult> RecoverSubmissionAsync(
        AgentToolOperationReconciliationRequest request,
        AgentToolPendingOperation? pending,
        CodeExecutionRequest executionRequest,
        string callId,
        CancellationToken ct)
    {
        if (pending is not null && IsExpired(pending))
            return SubmitRecoveryExpired(callId);

        var restarted = await StartOperationAsync(
                new AgentToolOperationStartRequest(
                    request.OperationId,
                    callId,
                    Name,
                    request.ArgumentsJson,
                    request.ExecutionContext),
                ct)
            .ConfigureAwait(false);
        var reconciliation = ToReconciliationResult(restarted, pending?.ExpiresAtUnixMs);
        if (reconciliation.PendingOperation is not { } recoveredPending ||
            !IsExpired(recoveredPending))
        {
            return reconciliation;
        }

        if (string.IsNullOrWhiteSpace(recoveredPending.ProviderOperationId))
            return SubmitRecoveryExpired(callId);

        if (!TryResolvePendingRoute(recoveredPending, out var recoveredRoute) ||
            !IsCompatiblePendingRoute(recoveredRoute, executionRequest.Route))
        {
            return AgentToolOperationReconciliationResult.Completed(TerminalFailure(
                callId,
                Name,
                new CodeExecutionFailure(
                    CodeExecutionFailureKind.AdmissionDenied,
                    "code_execution_admission_invalid",
                    "The recovered code execution route does not match its current admission proof.")));
        }

        return await ExpireKnownOperationAsync(
                callId,
                new DurableCodeExecutionOperationRequest(
                    recoveredPending.ProviderOperationId,
                    recoveredRoute,
                    executionRequest.Caller),
                ct)
            .ConfigureAwait(false);
    }

    private async Task<AgentToolOperationReconciliationResult> ReconcileKnownOperationAsync(
        string callId,
        AgentToolPendingOperation pending,
        DurableCodeExecutionOperationRequest operationRequest,
        CodeExecutionRouteIdentity route,
        CancellationToken ct)
    {
        var status = await _durableExecutionPort!.GetStatusAsync(operationRequest, ct)
            .ConfigureAwait(false);
        if (status.Failure is not null)
        {
            if (!status.Failure.Retryable)
            {
                return AgentToolOperationReconciliationResult.Completed(
                    TerminalFailure(callId, Name, ToCodeExecutionFailure(status.Failure)));
            }

            return await PendingKnownOperationOrExpireAsync(
                    callId,
                    UpdatePending(
                        pending,
                        retryAfter: status.Failure.RetryAfter ?? status.RetryAfter),
                    operationRequest,
                    ct)
                .ConfigureAwait(false);
        }

        if (status.NotModified)
        {
            return await PendingKnownOperationOrExpireAsync(
                    callId,
                    UpdatePending(pending, etag: status.ETag, retryAfter: status.RetryAfter),
                    operationRequest,
                    ct)
                .ConfigureAwait(false);
        }

        if (status.Snapshot is not { } snapshot)
            return AgentToolOperationReconciliationResult.Unknown();

        if (!string.Equals(
                snapshot.ProviderOperationId,
                pending.ProviderOperationId,
                StringComparison.Ordinal) ||
            !Equals(snapshot.ResolvedRoute, route))
        {
            return AgentToolOperationReconciliationResult.Completed(TerminalFailure(
                callId,
                Name,
                new CodeExecutionFailure(
                    CodeExecutionFailureKind.AdmissionDenied,
                    "code_execution_admission_invalid",
                    "The provider operation identity does not match its admitted route.")));
        }

        var refreshed = UpdatePending(
            pending,
            status: ToPendingOperationStatus(snapshot.State),
            etag: snapshot.ETag,
            retryAfter: snapshot.RetryAfter ?? status.RetryAfter,
            expiresAt: snapshot.ExpiresAt,
            route: snapshot.ResolvedRoute);
        if (snapshot.State is DurableCodeExecutionState.Cancelled or
            DurableCodeExecutionState.OutcomeUncertain)
        {
            return AgentToolOperationReconciliationResult.Completed(TerminalFailure(
                callId,
                Name,
                new CodeExecutionFailure(
                    snapshot.State == DurableCodeExecutionState.OutcomeUncertain
                        ? CodeExecutionFailureKind.OutcomeUncertain
                        : CodeExecutionFailureKind.ExecutionFailed,
                    snapshot.State == DurableCodeExecutionState.Cancelled
                        ? "code_execution_cancelled"
                        : "code_execution_outcome_uncertain",
                    snapshot.State == DurableCodeExecutionState.Cancelled
                        ? "Code execution was cancelled."
                        : "The code execution outcome is uncertain.")));
        }

        if (snapshot.State is not (DurableCodeExecutionState.Succeeded or
            DurableCodeExecutionState.Failed))
        {
            return snapshot.State == DurableCodeExecutionState.Unspecified
                ? AgentToolOperationReconciliationResult.Unknown()
                : await PendingKnownOperationOrExpireAsync(
                        callId,
                        refreshed,
                        operationRequest,
                        ct)
                    .ConfigureAwait(false);
        }

        return await ReconcileKnownResultAsync(callId, refreshed, operationRequest, route, ct)
            .ConfigureAwait(false);
    }

    private async Task<AgentToolOperationReconciliationResult> ReconcileKnownResultAsync(
        string callId,
        AgentToolPendingOperation refreshed,
        DurableCodeExecutionOperationRequest operationRequest,
        CodeExecutionRouteIdentity route,
        CancellationToken ct)
    {
        var result = await _durableExecutionPort!.GetResultAsync(
                operationRequest with { ETag = null },
                ct)
            .ConfigureAwait(false);
        if (result.Outcome is not null && result.Failure is null && !result.Pending)
        {
            if (result.Outcome.ResolvedRoute is { } resultRoute && !Equals(resultRoute, route))
            {
                return AgentToolOperationReconciliationResult.Completed(TerminalFailure(
                    callId,
                    Name,
                    new CodeExecutionFailure(
                        CodeExecutionFailureKind.AdmissionDenied,
                        "code_execution_admission_invalid",
                        "The provider result route does not match its admitted operation.")));
            }

            return AgentToolOperationReconciliationResult.Completed(
                Terminal(callId, Name, result.Outcome));
        }

        if (result.Pending || result.Failure?.Retryable == true)
        {
            return await PendingKnownOperationOrExpireAsync(
                    callId,
                    UpdatePending(
                        refreshed,
                        retryAfter: result.Failure?.RetryAfter ?? result.RetryAfter),
                    operationRequest,
                    ct)
                .ConfigureAwait(false);
        }

        return AgentToolOperationReconciliationResult.Completed(TerminalFailure(
            callId,
            Name,
            result.Failure is null
                ? InvalidDurableOutcomeFailure()
                : ToCodeExecutionFailure(result.Failure)));
    }

    private async Task<AgentToolOperationReconciliationResult> PendingKnownOperationOrExpireAsync(
        string callId,
        AgentToolPendingOperation pending,
        DurableCodeExecutionOperationRequest operationRequest,
        CancellationToken ct) =>
        IsExpired(pending)
            ? await ExpireKnownOperationAsync(callId, operationRequest, ct).ConfigureAwait(false)
            : AgentToolOperationReconciliationResult.Pending(pending);

    private async Task<AgentToolOperationReconciliationResult> ExpireKnownOperationAsync(
        string callId,
        DurableCodeExecutionOperationRequest operationRequest,
        CancellationToken ct)
    {
        var cancelled = await _durableExecutionPort!.CancelAsync(
                operationRequest with { ETag = null },
                ct)
            .ConfigureAwait(false);
        if (cancelled.Failure is null &&
            cancelled.Snapshot is { } snapshot &&
            string.Equals(
                snapshot.ProviderOperationId,
                operationRequest.ProviderOperationId,
                StringComparison.Ordinal) &&
            Equals(snapshot.ResolvedRoute, operationRequest.Route))
        {
            var terminal = await ResolveCancellationTerminalAsync(
                    callId,
                    snapshot,
                    operationRequest,
                    operationRequest.Route,
                    ct)
                .ConfigureAwait(false);
            if (terminal.CompletedOutcome is not null)
            {
                return AgentToolOperationReconciliationResult.Completed(
                    terminal.CompletedOutcome);
            }
        }

        return AgentToolOperationReconciliationResult.Completed(TerminalFailure(
            callId,
            Name,
            new CodeExecutionFailure(
                CodeExecutionFailureKind.OutcomeUncertain,
                "OPERATION_EXPIRED",
                "The durable code execution deadline expired; cancellation was requested.")));
    }

    private async Task<CancellationTerminalResolution> ResolveCancellationTerminalAsync(
        string callId,
        DurableCodeExecutionSnapshot snapshot,
        DurableCodeExecutionOperationRequest operationRequest,
        CodeExecutionRouteIdentity route,
        CancellationToken ct)
    {
        if (snapshot.State == DurableCodeExecutionState.Cancelled)
        {
            return CancellationTerminalResolution.Completed(TerminalFailure(
                callId,
                Name,
                new CodeExecutionFailure(
                    CodeExecutionFailureKind.ExecutionFailed,
                    "code_execution_cancelled",
                    "Code execution cancellation was confirmed.")));
        }

        if (snapshot.State == DurableCodeExecutionState.OutcomeUncertain)
        {
            return CancellationTerminalResolution.Completed(TerminalFailure(
                callId,
                Name,
                new CodeExecutionFailure(
                    CodeExecutionFailureKind.OutcomeUncertain,
                    "code_execution_outcome_uncertain",
                    "The code execution outcome is uncertain.")));
        }

        if (snapshot.State is not (DurableCodeExecutionState.Succeeded or
            DurableCodeExecutionState.Failed))
        {
            return CancellationTerminalResolution.Pending();
        }

        var result = await _durableExecutionPort!.GetResultAsync(
                operationRequest with { ETag = null },
                ct)
            .ConfigureAwait(false);
        if (result.Outcome is not null && result.Failure is null && !result.Pending)
        {
            if (result.Outcome.ResolvedRoute is { } resultRoute && !Equals(resultRoute, route))
                return CancellationTerminalResolution.Pending();

            return CancellationTerminalResolution.Completed(
                Terminal(callId, Name, result.Outcome));
        }

        if (result.Pending || result.Failure?.Retryable == true)
        {
            return CancellationTerminalResolution.Pending(
                result.Failure?.RetryAfter ?? result.RetryAfter);
        }

        return CancellationTerminalResolution.Completed(TerminalFailure(
            callId,
            Name,
            result.Failure is null
                ? InvalidDurableOutcomeFailure()
                : ToCodeExecutionFailure(result.Failure)));
    }

    private AgentToolOperationReconciliationResult SubmitRecoveryExpired(string callId) =>
        AgentToolOperationReconciliationResult.Completed(TerminalFailure(
            callId,
            Name,
            new CodeExecutionFailure(
                CodeExecutionFailureKind.OutcomeUncertain,
                "code_execution_submit_recovery_expired",
                "The durable code execution recovery window expired.")));

    private AgentToolOperationCancellationResult CancellationPendingOrUncertain(
        AgentToolOperationCancellationRequest request,
        AgentToolPendingOperation pending,
        string callId) =>
        HasCancellationDeadlineElapsed(request.DeadlineUnixMs)
            ? AgentToolOperationCancellationResult.Completed(TerminalFailure(
                callId,
                "code_execute",
                new CodeExecutionFailure(
                    CodeExecutionFailureKind.OutcomeUncertain,
                    "code_execution_cancel_outcome_uncertain",
                    "The provider terminal outcome could not be confirmed before the workflow stop deadline.")))
            : AgentToolOperationCancellationResult.Pending(pending);

    private bool HasCancellationDeadlineElapsed(long deadlineUnixMs) =>
        deadlineUnixMs > 0 &&
        deadlineUnixMs <= _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

    private bool IsExpired(AgentToolPendingOperation pending) =>
        pending.ExpiresAtUnixMs > 0 &&
        pending.ExpiresAtUnixMs <= _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

    private static AgentToolOperationReconciliationResult ToReconciliationResult(
        AgentToolOperationStartResult started,
        long? existingSubmitDeadlineUnixMs = null) =>
        started.Disposition switch
        {
            AgentToolOperationStartDisposition.Pending when started.PendingOperation is not null =>
                AgentToolOperationReconciliationResult.Pending(
                    existingSubmitDeadlineUnixMs.HasValue &&
                    string.IsNullOrWhiteSpace(started.PendingOperation.ProviderOperationId)
                        ? started.PendingOperation with
                        {
                            ExpiresAtUnixMs = EarlierDeadline(
                                existingSubmitDeadlineUnixMs.Value,
                                started.PendingOperation.ExpiresAtUnixMs),
                        }
                        : started.PendingOperation),
            AgentToolOperationStartDisposition.Completed when started.CompletedOutcome is not null =>
                AgentToolOperationReconciliationResult.Completed(started.CompletedOutcome),
            _ => AgentToolOperationReconciliationResult.Unknown(),
        };

    public AgentToolReceipt? CreateResultReceipt(
        string callId,
        string toolName,
        string argumentsJson,
        string resultJson)
    {
        try
        {
            using var document = JsonDocument.Parse(resultJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("success", out var success) ||
                success.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return null;
            }

            if (success.GetBoolean())
            {
                return TryReadResult(root, out var result) && result.ExitCode == 0
                    ? SuccessReceipt(callId, toolName, resultJson, userServiceId: null)
                    : null;
            }

            if (!TryReadNonEmptyString(root, "error", out var error) ||
                !TryReadNonEmptyString(root, "code", out var code) ||
                !TryReadNonEmptyString(root, "message", out var message) ||
                !string.Equals(error, code, StringComparison.Ordinal))
            {
                return null;
            }

            if (CompletedFailureCodes.Contains(code))
            {
                if (!TryReadResult(root, out var failedResult) || failedResult.ExitCode == 0)
                    return null;
            }
            else if (!PreExecutionFailureCodes.Contains(code) || root.TryGetProperty("output", out _))
            {
                return null;
            }

            return FailureReceipt(
                callId,
                toolName,
                resultJson,
                new CodeExecutionFailure(
                    CompletedFailureCodes.Contains(code)
                        ? CodeExecutionFailureKind.ExecutionFailed
                        : OutcomeUncertainFailureCodes.Contains(code)
                            ? CodeExecutionFailureKind.OutcomeUncertain
                            : CodeExecutionFailureKind.AdmissionDenied,
                    code,
                    message),
                userServiceId: null);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<AgentToolTerminalOutcome> ExecuteCoreAsync(
        string callId,
        string toolName,
        string argumentsJson,
        CancellationToken ct)
    {
        if (AgentToolRequestContext.Current?.InvocationSurface ==
            AgentToolInvocationSurface.WorkflowToolCall)
        {
            return TerminalFailure(
                callId,
                toolName,
                new CodeExecutionFailure(
                    CodeExecutionFailureKind.AdmissionDenied,
                    "code_execution_durable_context_invalid",
                    "Workflow code execution must use the durable operation contract."));
        }

        var preparation = PrepareExecution(argumentsJson);
        if (preparation.Failure is not null)
            return TerminalFailure(callId, toolName, preparation.Failure);
        if (preparation.Request!.Caller.ExecutionCredentialKind ==
                CodeExecutionNyxIdCredentialKind.AgentKey &&
            preparation.Request.Caller.DurableOperationGrant is not null)
        {
            return TerminalFailure(
                callId,
                toolName,
                new CodeExecutionFailure(
                    CodeExecutionFailureKind.AdmissionDenied,
                    "durable_operation_required",
                    "Scheduled Agent Key code execution must use the durable operation contract."));
        }

        var outcome = await _executionPort.ExecuteAsync(preparation.Request, ct)
            .ConfigureAwait(false);
        return Terminal(callId, toolName, outcome);
    }

    private CodeExecutionPreparation PrepareExecution(string argumentsJson)
    {
        var args = ToolArgs.Parse(argumentsJson);
        var language = args.Str("language");
        var source = args.Str("code");
        if (string.IsNullOrWhiteSpace(language) || string.IsNullOrWhiteSpace(source))
        {
            return CodeExecutionPreparation.Failed(new CodeExecutionFailure(
                    CodeExecutionFailureKind.AdmissionDenied,
                    "code_execution_request_invalid",
                    "Both 'language' and 'code' are required."));
        }

        if (!TryParseLanguage(language, out var codeLanguage))
        {
            return CodeExecutionPreparation.Failed(new CodeExecutionFailure(
                    CodeExecutionFailureKind.AdmissionDenied,
                    "code_execution_request_invalid",
                    "Language must be one of: python, javascript, typescript, bash."));
        }

        var timeoutSeconds = CodeExecutionContract.DefaultTimeoutSeconds;
        if (args.Element("timeout_secs") is { } timeoutElement &&
            (timeoutElement.ValueKind != JsonValueKind.Number ||
             !timeoutElement.TryGetInt32(out timeoutSeconds)))
        {
            return CodeExecutionPreparation.Failed(new CodeExecutionFailure(
                    CodeExecutionFailureKind.AdmissionDenied,
                    "code_execution_request_invalid",
                    "'timeout_secs' must be an integer between 1 and 600."));
        }

        if (!CodeExecutionContract.IsValidTimeoutSeconds(timeoutSeconds))
        {
            return CodeExecutionPreparation.Failed(new CodeExecutionFailure(
                    CodeExecutionFailureKind.AdmissionDenied,
                    "code_execution_request_invalid",
                    "'timeout_secs' must be an integer between 1 and 600."));
        }

        var credentials = AgentToolRequestContext.Current?.Credentials;
        var durableCredential = AgentToolRequestContext.Current?.DurableNyxIdCredential;
        var executionCredential = ResolveExecutionCredential(credentials, durableCredential);
        if (executionCredential.Token is null ||
            executionCredential.Kind == CodeExecutionNyxIdCredentialKind.Unspecified)
        {
            return CodeExecutionPreparation.Failed(new CodeExecutionFailure(
                    CodeExecutionFailureKind.AdmissionDenied,
                    "code_execution_credential_unavailable",
                    "A typed NyxID execution credential is required for code execution."));
        }

        if (!TryResolveAdmittedRoute(out var admittedServiceSlug, out var admittedUserServiceId))
        {
            return CodeExecutionPreparation.Failed(new CodeExecutionFailure(
                    CodeExecutionFailureKind.AdmissionDenied,
                    "code_execution_admission_invalid",
                    "The workflow code execution admission proof is invalid."));
        }

        var durableGrant = ResolveDurableOperationGrant(
            credentials,
            admittedUserServiceId,
            durableCredential,
            _timeProvider.GetUtcNow());
        if (durableGrant.Failure is not null)
            return CodeExecutionPreparation.Failed(durableGrant.Failure);

        var sourceReadableBearerToken = AgentToolSourceReadableNyxIdCredential.ResolveBearerToken(credentials);
        if (sourceReadableBearerToken is null && admittedUserServiceId is null)
        {
            return CodeExecutionPreparation.Failed(new CodeExecutionFailure(
                    CodeExecutionFailureKind.AdmissionDenied,
                    "code_execution_credential_unavailable",
                    "A source-readable NyxID credential is required to resolve the code execution route."));
        }

        return CodeExecutionPreparation.Succeeded(new CodeExecutionRequest(
            codeLanguage,
            source,
            timeoutSeconds,
            new CodeExecutionRouteIdentity(
                admittedServiceSlug,
                admittedUserServiceId,
                admittedUserServiceId is null
                    ? CodeExecutionRouteIdentitySource.CodeExecutionContract
                    : CodeExecutionRouteIdentitySource.WorkflowCapabilityAdmission),
            new CodeExecutionCallerContext(
                executionCredential.Token,
                sourceReadableBearerToken,
                executionCredential.Kind,
                durableGrant.Grant)));
    }

    private static DurableGrantResolution ResolveDurableOperationGrant(
        AgentToolCredentials? credentials,
        string? admittedUserServiceId,
        DurableCallerCredentialRef? durableCredential,
        DateTimeOffset now)
    {
        if (credentials?.NyxIdCredentialKind != AgentToolNyxIdCredentialKind.AgentKey ||
            durableCredential is null)
            return DurableGrantResolution.Succeeded(null);

        var providerCredentialId = NormalizeCredential(durableCredential?.ProviderCredentialId);
        if (providerCredentialId is null || admittedUserServiceId is null)
            return DurableGrantResolution.RebindRequired();

        var matches = durableCredential!.NyxIdDurableOperationGrants
            .Where(grant =>
                string.Equals(grant.ApiKeyId, providerCredentialId, StringComparison.Ordinal) &&
                string.Equals(grant.UserServiceId, admittedUserServiceId, StringComparison.Ordinal) &&
                grant.HttpMethod == NyxIdDurableOperationHttpMethod.Post &&
                string.Equals(grant.NormalizedPathTemplate, "/executions", StringComparison.Ordinal) &&
                IsNormalized(grant.GrantId) &&
                IsNormalized(grant.EndpointId) &&
                IsValidContractDigest(grant.ContractDigest) &&
                grant.ReplayPolicy == NyxIdDurableOperationReplayPolicy.DownstreamIdempotencyKey &&
                IsValidAuditBinding(grant.ClientAuditBinding) &&
                grant.ValidFromUnixMs > 0 &&
                grant.ExpiresAtUnixMs > grant.ValidFromUnixMs &&
                grant.ValidFromUnixMs <= now.ToUnixTimeMilliseconds() &&
                grant.ExpiresAtUnixMs > now.ToUnixTimeMilliseconds())
            .Take(2)
            .ToArray();

        return matches.Length == 1
            ? DurableGrantResolution.Succeeded(matches[0].Clone())
            : DurableGrantResolution.RebindRequired();
    }

    private static bool IsNormalized(string? value) =>
        value is { Length: > 0 and <= 256 } &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
        value.All(static character => character is >= '!' and <= '~');

    private static bool IsValidContractDigest(string? value) =>
        value is { Length: 71 } &&
        value.StartsWith("sha256:", StringComparison.Ordinal) &&
        value.AsSpan(7).ToArray().All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsValidAuditBinding(NyxIdDurableOperationClientAuditBinding? binding) =>
        binding is null ||
        IsValidOptionalAuditValue(binding.Platform) &&
        IsValidOptionalAuditValue(binding.ScheduleId) &&
        IsValidOptionalAuditValue(binding.WorkflowRevision) &&
        IsValidOptionalAuditValue(binding.CallSite);

    private static bool IsValidOptionalAuditValue(string? value) =>
        string.IsNullOrEmpty(value) || IsNormalized(value);

    private static (string? Token, CodeExecutionNyxIdCredentialKind Kind)
        ResolveExecutionCredential(
            AgentToolCredentials? credentials,
            DurableCallerCredentialRef? durableCredential)
    {
        var kind = credentials?.NyxIdCredentialKind switch
        {
            AgentToolNyxIdCredentialKind.SourceReadableUserBearer or
                AgentToolNyxIdCredentialKind.ProxyDelegation =>
                CodeExecutionNyxIdCredentialKind.Bearer,
            AgentToolNyxIdCredentialKind.AgentKey when durableCredential is null =>
                CodeExecutionNyxIdCredentialKind.InteractiveAgentKey,
            AgentToolNyxIdCredentialKind.AgentKey =>
                CodeExecutionNyxIdCredentialKind.AgentKey,
            _ => CodeExecutionNyxIdCredentialKind.Unspecified,
        };

        return (NormalizeCredential(credentials?.NyxIdAccessToken), kind);
    }

    private static string? NormalizeCredential(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;

        var normalized = token.Trim();
        if (string.Equals(normalized, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
            normalized.Any(char.IsWhiteSpace))
        {
            return null;
        }

        return normalized;
    }

    private static AgentToolPendingOperation ToPendingOperation(
        string operationId,
        DurableCodeExecutionReceipt receipt) =>
        new(
            operationId,
            receipt.ProviderOperationId,
            receipt.StatusPath,
            receipt.ResultPath,
            receipt.CancelPath,
            ToPendingOperationStatus(receipt.State),
            null,
            RetryAfterMilliseconds(receipt.RetryAfter, DefaultRetryAfter),
            receipt.ExpiresAt.ToUnixTimeMilliseconds(),
            receipt.ResolvedRoute.ServiceSlug,
            receipt.ResolvedRoute.UserServiceId,
            receipt.ResolvedRoute.Source);

    private static AgentToolPendingOperation UpdatePending(
        AgentToolPendingOperation pending,
        AgentToolPendingOperationStatus? status = null,
        string? etag = null,
        TimeSpan? retryAfter = null,
        DateTimeOffset? expiresAt = null,
        CodeExecutionRouteIdentity? route = null) =>
        pending with
        {
            Status = status ?? pending.Status,
            ETag = string.IsNullOrWhiteSpace(etag) ? pending.ETag : etag,
            RetryAfterMilliseconds = retryAfter is null
                ? pending.RetryAfterMilliseconds
                : RetryAfterMilliseconds(retryAfter, DefaultRetryAfter),
            ExpiresAtUnixMs = expiresAt is null
                ? pending.ExpiresAtUnixMs
                : EarlierDeadline(
                    pending.ExpiresAtUnixMs,
                    expiresAt.Value.ToUnixTimeMilliseconds()),
            ServiceSlug = route?.ServiceSlug ?? pending.ServiceSlug,
            UserServiceId = route?.UserServiceId ?? pending.UserServiceId,
            RouteIdentitySource = route?.Source ?? pending.RouteIdentitySource,
        };

    private static long EarlierDeadline(long existingUnixMs, long incomingUnixMs)
    {
        if (existingUnixMs <= 0)
            return incomingUnixMs;
        if (incomingUnixMs <= 0)
            return existingUnixMs;

        return Math.Min(existingUnixMs, incomingUnixMs);
    }

    private static bool IsRecoverableSubmitFailure(DurableCodeExecutionFailure? failure) =>
        failure is
        {
            Retryable: true,
            Kind: DurableCodeExecutionFailureKind.SubmissionUncertain or
                DurableCodeExecutionFailureKind.TransportUnavailable or
                DurableCodeExecutionFailureKind.TimedOut or
                DurableCodeExecutionFailureKind.RateLimited or
                DurableCodeExecutionFailureKind.ServiceUnavailable,
        };

    private static bool IsOpaqueOperationId(string? operationId)
    {
        const string prefix = "tool:v1:operation:";
        if (operationId is null ||
            operationId.Length != prefix.Length + 64 ||
            !operationId.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var character in operationId.AsSpan(prefix.Length))
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
                return false;
        }

        return true;
    }

    private static bool TryResolvePendingRoute(
        AgentToolPendingOperation pending,
        out CodeExecutionRouteIdentity route)
    {
        route = new CodeExecutionRouteIdentity(
            pending.ServiceSlug,
            pending.UserServiceId,
            pending.RouteIdentitySource);
        if (!CodeExecutionContract.IsValidServiceSlug(route.ServiceSlug) ||
            route.Source == CodeExecutionRouteIdentitySource.Unspecified ||
            !Enum.IsDefined(route.Source))
        {
            return false;
        }

        if (route.UserServiceId is null)
        {
            return route.Source == CodeExecutionRouteIdentitySource.CodeExecutionContract &&
                   string.Equals(
                       route.ServiceSlug,
                       CodeExecutionContract.ServiceSlug,
                       StringComparison.Ordinal);
        }

        if ((route.Source is CodeExecutionRouteIdentitySource.NyxIdUserServiceCatalog or
                CodeExecutionRouteIdentitySource.WorkflowCapabilityAdmission) &&
            !CodeExecutionContract.IsSupportedServiceSlug(route.ServiceSlug))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(route.UserServiceId) &&
               string.Equals(route.UserServiceId, route.UserServiceId.Trim(), StringComparison.Ordinal);
    }

    private static bool IsCompatiblePendingRoute(
        CodeExecutionRouteIdentity pendingRoute,
        CodeExecutionRouteIdentity preparedRoute)
    {
        if (Equals(pendingRoute, preparedRoute))
            return true;

        // V4 plans have no proof-bound code route. Submit resolves the contract route once and
        // the actor must keep polling that exact catalog route from its durable receipt.
        return preparedRoute.Source == CodeExecutionRouteIdentitySource.CodeExecutionContract &&
               preparedRoute.UserServiceId is null &&
               pendingRoute.Source == CodeExecutionRouteIdentitySource.NyxIdUserServiceCatalog &&
               !string.IsNullOrWhiteSpace(pendingRoute.UserServiceId) &&
               CodeExecutionContract.IsSupportedServiceSlug(pendingRoute.ServiceSlug);
    }

    private static long RetryAfterMilliseconds(TimeSpan? value, TimeSpan fallback)
    {
        var selected = value is { } retryAfter && retryAfter > TimeSpan.Zero
            ? retryAfter
            : fallback;
        return Math.Clamp((long)Math.Ceiling(selected.TotalMilliseconds), 250L, 30_000L);
    }

    private static AgentToolPendingOperationStatus ToPendingOperationStatus(
        DurableCodeExecutionState state) =>
        state switch
        {
            DurableCodeExecutionState.Unspecified => AgentToolPendingOperationStatus.Unspecified,
            DurableCodeExecutionState.Queued => AgentToolPendingOperationStatus.Queued,
            DurableCodeExecutionState.Provisioning => AgentToolPendingOperationStatus.Provisioning,
            DurableCodeExecutionState.Preparing => AgentToolPendingOperationStatus.Preparing,
            DurableCodeExecutionState.Running => AgentToolPendingOperationStatus.Running,
            DurableCodeExecutionState.Collecting => AgentToolPendingOperationStatus.Collecting,
            DurableCodeExecutionState.Succeeded => AgentToolPendingOperationStatus.Succeeded,
            DurableCodeExecutionState.Failed => AgentToolPendingOperationStatus.Failed,
            DurableCodeExecutionState.Cancelled => AgentToolPendingOperationStatus.Cancelled,
            DurableCodeExecutionState.OutcomeUncertain => AgentToolPendingOperationStatus.OutcomeUncertain,
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown durable code execution state."),
        };

    private static CodeExecutionFailure InvalidDurableOutcomeFailure() =>
        new(
            CodeExecutionFailureKind.MalformedOutput,
            "code_execution_outcome_invalid",
            "Code execution returned an invalid outcome.");

    private static CodeExecutionFailure ToCodeExecutionFailure(
        DurableCodeExecutionFailure failure) =>
        new(
            failure.Kind switch
            {
                DurableCodeExecutionFailureKind.AdmissionDenied or
                    DurableCodeExecutionFailureKind.IdempotencyConflict =>
                    CodeExecutionFailureKind.AdmissionDenied,
                DurableCodeExecutionFailureKind.TargetNotConfigured =>
                    CodeExecutionFailureKind.TargetNotConfigured,
                DurableCodeExecutionFailureKind.TimedOut =>
                    CodeExecutionFailureKind.TimedOut,
                DurableCodeExecutionFailureKind.ResponseTooLarge =>
                    CodeExecutionFailureKind.ResponseTooLarge,
                DurableCodeExecutionFailureKind.MalformedOutput =>
                    CodeExecutionFailureKind.MalformedOutput,
                DurableCodeExecutionFailureKind.ProviderRejected or
                    DurableCodeExecutionFailureKind.ExecutionFailed or
                    DurableCodeExecutionFailureKind.Cancelled =>
                    CodeExecutionFailureKind.ExecutionFailed,
                DurableCodeExecutionFailureKind.OperationNotFound or
                    DurableCodeExecutionFailureKind.Expired or
                    DurableCodeExecutionFailureKind.OutcomeUncertain or
                    DurableCodeExecutionFailureKind.SubmissionUncertain =>
                    CodeExecutionFailureKind.OutcomeUncertain,
                _ => CodeExecutionFailureKind.TransportUnavailable,
            },
            failure.Code,
            failure.Message,
            failure.DiagnosticId,
            failure.ProviderPhase);

    private static bool TryResolveAdmittedRoute(
        out string serviceSlug,
        out string? userServiceId)
    {
        serviceSlug = CodeExecutionContract.ServiceSlug;
        userServiceId = null;
        var admission = AgentToolRequestContext.Current?.OperationAdmission;
        if (admission is null)
            return true;

        if (string.IsNullOrWhiteSpace(admission.ServiceInstanceId) ||
            !string.Equals(
                admission.ServiceInstanceId,
                admission.ServiceInstanceId.Trim(),
                StringComparison.Ordinal) ||
            !CodeExecutionContract.IsSupportedServiceSlug(admission.ServiceSlug) ||
            admission.Identity is not AgentToolOperationIdentity.PlatformBuiltIn
            {
                CapabilityId: "code_execute",
            } ||
            admission.AuthorizationBasis != AgentToolOperationAuthorizationBasis.PlatformContract ||
            !string.Equals(admission.HttpMethod, "POST", StringComparison.Ordinal) ||
            !string.Equals(admission.PathTemplate, "/execute", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(admission.ContractDigest))
        {
            return false;
        }

        serviceSlug = admission.ServiceSlug;
        userServiceId = admission.ServiceInstanceId;
        return true;
    }

    private static bool TryParseLanguage(string language, out CodeExecutionLanguage result)
    {
        result = language.Trim() switch
        {
            "python" => CodeExecutionLanguage.Python,
            "javascript" => CodeExecutionLanguage.JavaScript,
            "typescript" => CodeExecutionLanguage.TypeScript,
            "bash" => CodeExecutionLanguage.Bash,
            _ => CodeExecutionLanguage.Unspecified,
        };
        return result != CodeExecutionLanguage.Unspecified;
    }

    private static AgentToolTerminalOutcome Terminal(
        string callId,
        string toolName,
        CodeExecutionOutcome outcome)
    {
        if (!IsValidOutcome(outcome))
        {
            return TerminalFailure(
                callId,
                toolName,
                new CodeExecutionFailure(
                    CodeExecutionFailureKind.MalformedOutput,
                    "code_execution_outcome_invalid",
                    "Code execution returned an invalid outcome."));
        }

        var resultJson = SerializeOutcome(outcome);
        var userServiceId = outcome.ResolvedRoute?.UserServiceId;
        var receipt = outcome.Failure is null
            ? SuccessReceipt(callId, toolName, resultJson, userServiceId)
            : FailureReceipt(callId, toolName, resultJson, outcome.Failure, userServiceId);
        return new AgentToolTerminalOutcome(resultJson, receipt);
    }

    private static AgentToolTerminalOutcome TerminalFailure(
        string callId,
        string toolName,
        CodeExecutionFailure failure) =>
        Terminal(callId, toolName, CodeExecutionOutcome.Failed(failure));

    private static bool IsValidOutcome(CodeExecutionOutcome outcome)
    {
        if (outcome.Result is null)
            return outcome.Failure is not null;
        if (outcome.Failure is null)
            return outcome.Result.ExitCode == 0 && outcome.ResolvedRoute is not null;

        return outcome.Result.ExitCode != 0 &&
               outcome.Failure.Kind == CodeExecutionFailureKind.ExecutionFailed &&
               outcome.ResolvedRoute is not null;
    }

    private static AgentToolReceipt SuccessReceipt(
        string callId,
        string toolName,
        string resultJson,
        string? userServiceId) =>
        NyxIdProxyReceiptFactory.CreateSuccess(callId, toolName, userServiceId, resultJson) ??
        new AgentToolReceipt
        {
            CallId = callId ?? string.Empty,
            ToolName = string.IsNullOrWhiteSpace(toolName) ? "code_execute" : toolName,
            Status = AgentToolReceiptStatus.Success,
            ApprovalMode = AgentToolReceiptApprovalMode.NeverRequire,
            ResultJson = resultJson,
        };

    private static AgentToolReceipt FailureReceipt(
        string callId,
        string toolName,
        string resultJson,
        CodeExecutionFailure failure,
        string? userServiceId) =>
        NyxIdProxyReceiptFactory.CreateError(
            callId,
            string.IsNullOrWhiteSpace(toolName) ? "code_execute" : toolName,
            userServiceId,
            failure.Code,
            failure.Message,
            resultJson,
            failure.Kind == CodeExecutionFailureKind.OutcomeUncertain
                ? AgentToolFailureOutcome.OutcomeUncertain
                : AgentToolFailureOutcome.CalleeConfirmed);

    private static string SerializeOutcome(CodeExecutionOutcome outcome)
    {
        var result = outcome.Result;
        var failure = outcome.Failure;
        if (failure is null && result is not null)
        {
            return JsonSerializer.Serialize(new
            {
                success = true,
                output = Output(result),
            });
        }

        if (result is not null)
        {
            var providerPhase = ProviderPhaseName(failure!.ProviderPhase);
            if (providerPhase is not null)
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    output = Output(result),
                    error = failure.Code,
                    code = failure.Code,
                    message = failure.Message,
                    diagnostic_id = failure.DiagnosticId,
                    provider_phase = providerPhase,
                });
            }
            return JsonSerializer.Serialize(new
            {
                success = false,
                output = Output(result),
                error = failure!.Code,
                code = failure.Code,
                message = failure.Message,
                diagnostic_id = failure.DiagnosticId,
            });
        }

        var terminalProviderPhase = ProviderPhaseName(failure!.ProviderPhase);
        if (terminalProviderPhase is not null)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = failure.Code,
                code = failure.Code,
                message = failure.Message,
                diagnostic_id = failure.DiagnosticId,
                provider_phase = terminalProviderPhase,
            });
        }
        return JsonSerializer.Serialize(new
        {
            success = false,
            error = failure!.Code,
            code = failure.Code,
            message = failure.Message,
            diagnostic_id = failure.DiagnosticId,
        });
    }

    private static string? ProviderPhaseName(DurableCodeExecutionPhase phase) => phase switch
    {
        DurableCodeExecutionPhase.SandboxCreate => "sandbox_create",
        DurableCodeExecutionPhase.SandboxReady => "sandbox_ready",
        DurableCodeExecutionPhase.InputWrite => "input_write",
        DurableCodeExecutionPhase.DependencyInstall => "dependency_install",
        DurableCodeExecutionPhase.Execute => "execute",
        DurableCodeExecutionPhase.Collect => "collect",
        DurableCodeExecutionPhase.CleaningUp => "cleaning_up",
        _ => null,
    };

    private static object Output(CodeExecutionResult result) => new
    {
        stdout = result.Stdout,
        stderr = result.Stderr,
        exit_code = result.ExitCode,
        diagnostic_id = result.DiagnosticId,
        execution_time_ms = result.ElapsedMilliseconds,
    };

    private static bool TryReadResult(JsonElement root, out CodeExecutionResult result)
    {
        result = new CodeExecutionResult(string.Empty, string.Empty, 0);
        if (!root.TryGetProperty("output", out var output) ||
            output.ValueKind != JsonValueKind.Object ||
            !TryReadString(output, "stdout", out var stdout) ||
            !TryReadString(output, "stderr", out var stderr) ||
            !output.TryGetProperty("exit_code", out var exitCode) ||
            !exitCode.TryGetInt32(out var exitCodeValue))
        {
            return false;
        }

        result = new CodeExecutionResult(stdout, stderr, exitCodeValue);
        return true;
    }

    private static bool TryReadString(JsonElement owner, string name, out string value)
    {
        value = string.Empty;
        if (!owner.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
            return false;
        value = element.GetString() ?? string.Empty;
        return true;
    }

    private static bool TryReadNonEmptyString(JsonElement owner, string name, out string value) =>
        TryReadString(owner, name, out value) && !string.IsNullOrWhiteSpace(value);

    private sealed record CancellationTerminalResolution(
        AgentToolTerminalOutcome? CompletedOutcome,
        TimeSpan? RetryAfter)
    {
        public static CancellationTerminalResolution Completed(AgentToolTerminalOutcome outcome) =>
            new(outcome, null);

        public static CancellationTerminalResolution Pending(TimeSpan? retryAfter = null) =>
            new(null, retryAfter);
    }

    private sealed record CodeExecutionPreparation(
        CodeExecutionRequest? Request,
        CodeExecutionFailure? Failure)
    {
        public static CodeExecutionPreparation Succeeded(CodeExecutionRequest request) =>
            new(request, null);

        public static CodeExecutionPreparation Failed(CodeExecutionFailure failure) =>
            new(null, failure);
    }

    private sealed record DurableGrantResolution(
        NyxIdDurableOperationGrantRef? Grant,
        CodeExecutionFailure? Failure)
    {
        public static DurableGrantResolution Succeeded(NyxIdDurableOperationGrantRef? grant) =>
            new(grant, null);

        public static DurableGrantResolution RebindRequired() =>
            new(
                null,
                new CodeExecutionFailure(
                    CodeExecutionFailureKind.AdmissionDenied,
                    "code_execution_durable_grant_rebind_required",
                    "The scheduled NyxID credential is missing one exact active code execution grant and must be rebound."));
    }
}
