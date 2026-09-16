using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.CodexExecution;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Auditing;
using Aevatar.AI.Core.Observability;
using Aevatar.Audit;
using Aevatar.Audit.Abstractions.Identity;
using Aevatar.Audit.Abstractions.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.Core.Tools;

public sealed class AdmittedAgentToolExecutor : IAgentToolExecutionPort
{
    private readonly IAgentToolAdmissionLedger _admissionLedger;
    private readonly IAuditTrailAppender _auditTrailAppender;
    private readonly ToolAuditRecordFactory _auditRecordFactory;
    private readonly ILogger<AdmittedAgentToolExecutor> _logger;
    private readonly TimeProvider _timeProvider;

    public AdmittedAgentToolExecutor(
        IAgentToolAdmissionLedger admissionLedger,
        IAuditTrailAppender auditTrailAppender,
        IAuditActorIdentityHasher identityHasher,
        TimeProvider? timeProvider = null,
        ILogger<AdmittedAgentToolExecutor>? logger = null)
    {
        _admissionLedger = admissionLedger ?? throw new ArgumentNullException(nameof(admissionLedger));
        _auditTrailAppender = auditTrailAppender ?? throw new ArgumentNullException(nameof(auditTrailAppender));
        _logger = logger ?? NullLogger<AdmittedAgentToolExecutor>.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _auditRecordFactory = new ToolAuditRecordFactory(
            identityHasher ?? throw new ArgumentNullException(nameof(identityHasher)),
            _timeProvider);
    }

    public async Task<AgentToolExecutionOutcome> ExecuteAsync(
        AgentToolExecutionRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Tool);
        ArgumentNullException.ThrowIfNull(request.ExecutionContext);

        var tool = request.Tool;
        var toolName = NormalizeIdentity(tool.Name);
        var requestId = NormalizeIdentity(request.ExecutionContext.Request.RequestId);
        var toolCallId = NormalizeIdentity(request.ExecutionContext.Request.CallId);
        var executionOwner = NormalizeExecutionOwner(request.ExecutionOwner);
        var argumentsJson = AgentToolArgumentsDigest.Freeze(request.ArgumentsJson);
        var argumentsSha256 = AgentToolArgumentsDigest.ComputeSha256(argumentsJson);
        var fallbackSafety = new AgentToolCallSafety(true, false, true);

        if (toolName is null || requestId is null || toolCallId is null || executionOwner is null)
        {
            return CreateUnauditedFailure(
                tool,
                toolName ?? "unknown_tool",
                toolCallId ?? string.Empty,
                fallbackSafety,
                "invalid_tool_execution_identity",
                "Tool execution requires non-empty owner, request, call, and tool identities.",
                AgentToolExecutionFailureStage.RequestValidation);
        }

        if (request.ExecutionAttemptKind is not (
                AgentToolExecutionAttemptKind.Initial or
                AgentToolExecutionAttemptKind.ActorRecovery))
        {
            return CreateUnauditedFailure(
                tool,
                toolName,
                toolCallId,
                fallbackSafety,
                "invalid_tool_execution_attempt",
                "Tool execution requires an explicit initial or actor-recovery attempt kind.",
                AgentToolExecutionFailureStage.RequestValidation);
        }

        var operationId = NormalizeIdentity(request.ExecutionContext.Request.OperationId)
                          ?? CreateOperationId(executionOwner, requestId, toolCallId);
        var executionContext = request.ExecutionContext with
        {
            Request = request.ExecutionContext.Request with { OperationId = operationId },
        };

        if (request.PendingOperation is not null &&
            (request.ExecutionAttemptKind != AgentToolExecutionAttemptKind.ActorRecovery ||
             !string.Equals(
                 request.PendingOperation.OperationId,
                 operationId,
                 StringComparison.Ordinal)))
        {
            return CreateUnauditedFailure(
                tool,
                toolName,
                toolCallId,
                fallbackSafety,
                "invalid_pending_tool_operation",
                "A pending tool operation must belong to the exact actor-recovery operation.",
                AgentToolExecutionFailureStage.RequestValidation);
        }

        AgentToolCallSafety callSafety;
        AgentToolReplayPolicy replayPolicy;
        try
        {
            using var contextScope = AgentToolContextScope.Push(executionContext);
            callSafety = tool.GetCallSafety(argumentsJson)
                ?? throw new InvalidOperationException("Tool safety classification is required.");
            replayPolicy = tool.ResolveReplayPolicy(argumentsJson);
        }
        catch (Exception ex)
        {
            var failed = CreateFailure(
                tool,
                toolName,
                toolCallId,
                fallbackSafety,
                isMutation: true,
                "tool_classification_failed",
                SafeExceptionClass(ex),
                AgentToolExecutionFailureStage.Classification,
                terminalInvoked: false,
                retryable: false,
                auditCompleted: false);
            return await CompleteBeforeTerminalAsync(
                tool,
                failed,
                executionContext,
                AgentToolCredentialSource.System,
                executionOwner,
                requestId,
                toolName,
                toolCallId,
                argumentsSha256,
                fallbackSafety,
                ct).ConfigureAwait(false);
        }

        if (ValidateReplayPolicy(
                tool,
                callSafety,
                replayPolicy,
                operationId,
                executionContext.Request.IdempotencyKey) is { } replayPolicyFailure)
        {
            var failed = CreateFailure(
                tool,
                toolName,
                toolCallId,
                callSafety,
                isMutation: AgentToolCredentialPolicy.IsMutation(tool, callSafety),
                replayPolicyFailure.Code,
                replayPolicyFailure.SafeMessage,
                AgentToolExecutionFailureStage.Classification,
                terminalInvoked: false,
                retryable: false,
                auditCompleted: false);
            return await CompleteBeforeTerminalAsync(
                tool,
                failed,
                executionContext,
                AgentToolCredentialSource.System,
                executionOwner,
                requestId,
                toolName,
                toolCallId,
                argumentsSha256,
                callSafety,
                ct).ConfigureAwait(false);
        }

        var isMutation = AgentToolCredentialPolicy.IsMutation(tool, callSafety);
        var credentialDecision = ResolveCredentials(executionContext, isMutation, toolName);
        if (!credentialDecision.Allowed)
        {
            var denied = CreateDenied(
                tool,
                toolName,
                toolCallId,
                callSafety,
                isMutation,
                "credential_denied",
                credentialDecision.Message,
                AgentToolExecutionFailureStage.CredentialPolicy);
            return await CompleteBeforeTerminalAsync(
                tool,
                denied,
                executionContext,
                credentialDecision.CredentialSource,
                executionOwner,
                requestId,
                toolName,
                toolCallId,
                argumentsSha256,
                callSafety,
                ct).ConfigureAwait(false);
        }
        var approvalRequestId = CreateApprovalRequestId(
            executionOwner,
            requestId,
            toolName,
            toolCallId,
            argumentsSha256);
        var requiresApproval = RequiresApproval(tool, callSafety);
        if (request.ApprovalGrant is not null && request.UnattendedAuthorization is not null)
        {
            var denied = CreateDenied(
                tool,
                toolName,
                toolCallId,
                callSafety,
                isMutation,
                "conflicting_tool_authorization",
                "A tool call cannot use human approval and unattended authorization together.",
                AgentToolExecutionFailureStage.Approval,
                approvalRequestId);
            return await CompleteBeforeTerminalAsync(
                tool,
                denied,
                credentialDecision.ExecutionContext,
                credentialDecision.CredentialSource,
                executionOwner,
                requestId,
                toolName,
                toolCallId,
                argumentsSha256,
                callSafety,
                ct).ConfigureAwait(false);
        }

        if (request.UnattendedAuthorization is not null)
        {
            if (!MatchesUnattendedAuthorization(
                    request.UnattendedAuthorization,
                    request.ApprovalContinuationMode,
                    requiresApproval,
                    isMutation,
                    callSafety,
                    credentialDecision.ExecutionContext,
                    executionOwner,
                    requestId,
                    toolName,
                    toolCallId,
                    argumentsSha256))
            {
                var denied = CreateDenied(
                    tool,
                    toolName,
                    toolCallId,
                    callSafety,
                    isMutation,
                    "unattended_authorization_mismatch",
                    "The unattended authorization does not match this exact tool call.",
                    AgentToolExecutionFailureStage.Approval,
                    approvalRequestId);
                return await CompleteBeforeTerminalAsync(
                    tool,
                    denied,
                    credentialDecision.ExecutionContext,
                    credentialDecision.CredentialSource,
                    executionOwner,
                    requestId,
                    toolName,
                    toolCallId,
                    argumentsSha256,
                    callSafety,
                    ct).ConfigureAwait(false);
            }

            requiresApproval = false;
        }

        if (request.ApprovalGrant is not null &&
            !MatchesGrant(
                request.ApprovalGrant,
                request.ApprovalContinuationMode,
                approvalRequestId,
                executionOwner,
                requestId,
                toolName,
                toolCallId,
                argumentsSha256))
        {
            var denied = CreateDenied(
                tool,
                toolName,
                toolCallId,
                callSafety,
                isMutation,
                "approval_grant_mismatch",
                "The durable approval grant does not match this exact tool call.",
                AgentToolExecutionFailureStage.Approval,
                approvalRequestId);
            return await CompleteBeforeTerminalAsync(
                tool,
                denied,
                credentialDecision.ExecutionContext,
                credentialDecision.CredentialSource,
                executionOwner,
                requestId,
                toolName,
                toolCallId,
                argumentsSha256,
                callSafety,
                ct).ConfigureAwait(false);
        }

        if (requiresApproval && request.ApprovalGrant is null)
        {
            if (request.ApprovalContinuationMode != AgentToolApprovalContinuationMode.ActorOwned)
            {
                var denied = CreateDenied(
                    tool,
                    toolName,
                    toolCallId,
                    callSafety,
                    isMutation,
                    "approval_required_without_actor_continuation",
                    "This tool call requires an actor-owned durable approval continuation.",
                    AgentToolExecutionFailureStage.Approval,
                    approvalRequestId);
                return await CompleteBeforeTerminalAsync(
                    tool,
                    denied,
                    credentialDecision.ExecutionContext,
                    credentialDecision.CredentialSource,
                    executionOwner,
                    requestId,
                    toolName,
                    toolCallId,
                    argumentsSha256,
                    callSafety,
                    ct).ConfigureAwait(false);
            }

            return await CreateApprovalRequiredAsync(
                tool,
                toolName,
                toolCallId,
                callSafety,
                isMutation,
                approvalRequestId,
                executionOwner,
                requestId,
                argumentsSha256,
                credentialDecision,
                ct).ConfigureAwait(false);
        }

        AgentToolTerminalOutcome? reconciledOutcome = null;
        AgentToolPendingOperation? reconciledPendingOperation = null;
        var admission = await TryStartAsync(
            new AgentToolAdmissionFact
            {
                AdmissionId = CreateAdmissionId(executionOwner, requestId, toolCallId),
                RequestId = requestId,
                ToolCallId = toolCallId,
                ToolName = toolName,
                ArgumentsSha256 = argumentsSha256,
                ExecutionOwner = ToProto(executionOwner),
                IssuedAtUnixMs = request.ExecutionContext.Request.IssuedAtUnixMs,
                OperationId = operationId,
                ReplayPolicy = replayPolicy,
            },
            ct).ConfigureAwait(false);
        if (admission.Status == AgentToolAdmissionStatus.Duplicate &&
            request.ExecutionAttemptKind == AgentToolExecutionAttemptKind.ActorRecovery)
        {
            var recovery = await ResolveDuplicateRecoveryAsync(
                tool,
                replayPolicy,
                operationId,
                argumentsJson,
                callSafety,
                isMutation,
                toolName,
                toolCallId,
                credentialDecision.ExecutionContext,
                request.PendingOperation,
                ct).ConfigureAwait(false);
            if (recovery.Failure is not null)
                return recovery.Failure;
            reconciledOutcome = recovery.CompletedOutcome;
            reconciledPendingOperation = recovery.PendingOperation;
        }
        else if (admission.Status != AgentToolAdmissionStatus.Started)
        {
            var (failureCode, safeMessage, retryable) = admission.Status switch
            {
                AgentToolAdmissionStatus.Duplicate => (
                    "tool_execution_already_started",
                    "This exact tool call already started and will not be replayed.",
                    false),
                AgentToolAdmissionStatus.Conflict => (
                    "tool_admission_conflict",
                    "The tool call identity conflicts with an existing admission fact.",
                    false),
                AgentToolAdmissionStatus.StoreUnavailable => (
                    "tool_admission_unavailable",
                    string.IsNullOrWhiteSpace(admission.SafeMessage)
                        ? "The durable tool admission ledger is unavailable."
                        : admission.SafeMessage,
                    true),
                AgentToolAdmissionStatus.InvalidFact => (
                    "tool_admission_invalid_fact",
                    string.IsNullOrWhiteSpace(admission.SafeMessage)
                        ? "The tool admission fact has an invalid replay lifetime."
                        : admission.SafeMessage,
                    false),
                AgentToolAdmissionStatus.Expired => (
                    "tool_admission_expired",
                    "The tool call is outside the configured replay window.",
                    false),
                _ => (
                    "tool_admission_invalid_status",
                    "The durable tool admission ledger returned an invalid status.",
                    false),
            };
            return CreateFailure(
                tool,
                toolName,
                toolCallId,
                callSafety,
                isMutation,
                failureCode,
                safeMessage,
                AgentToolExecutionFailureStage.Admission,
                terminalInvoked: false,
                retryable,
                auditCompleted: false);
        }

        var runningReceipt = AgentToolReceiptFactory.CreateRunning(
            tool,
            toolCallId,
            toolName,
            callSafety);
        var runningAppend = await AppendAsync(
            CreateRunningAuditId(executionOwner, requestId, toolCallId),
            AuditToolExecutionPhase.Running,
            tool,
            toolName,
            toolCallId,
            argumentsSha256,
            callSafety,
            credentialDecision.ExecutionContext,
            credentialDecision.CredentialSource,
            runningReceipt,
            AuditOutcome.Accepted,
            isMutation,
            ct,
            request.UnattendedAuthorization).ConfigureAwait(false);
        return await ExecuteTerminalAsync(
            tool,
            toolName,
            toolCallId,
            argumentsJson,
            argumentsSha256,
            requestId,
            executionOwner,
            callSafety,
            isMutation,
            credentialDecision,
            runningAppend,
            runningReceipt,
            replayPolicy,
            operationId,
            reconciledOutcome,
            reconciledPendingOperation,
            request.UnattendedAuthorization,
            ct).ConfigureAwait(false);
    }

    public async Task<AgentToolCancellationResult> CancelAsync(
        AgentToolCancellationRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Tool);
        ArgumentNullException.ThrowIfNull(request.ExecutionContext);
        ArgumentNullException.ThrowIfNull(request.PendingOperation);

        var tool = request.Tool;
        var toolName = NormalizeIdentity(tool.Name);
        var requestId = NormalizeIdentity(request.ExecutionContext.Request.RequestId);
        var toolCallId = NormalizeIdentity(request.ExecutionContext.Request.CallId);
        var operationId = NormalizeIdentity(request.ExecutionContext.Request.OperationId);
        var executionOwner = NormalizeExecutionOwner(request.ExecutionOwner);
        var argumentsJson = AgentToolArgumentsDigest.Freeze(request.ArgumentsJson);
        var argumentsSha256 = NormalizeArgumentsSha256(request.TerminalIntent?.ArgumentsSha256)
                              ?? AgentToolArgumentsDigest.ComputeSha256(argumentsJson);

        if (toolName is null || requestId is null || toolCallId is null || operationId is null ||
            executionOwner is null ||
            !string.Equals(request.PendingOperation.OperationId, operationId, StringComparison.Ordinal))
        {
            return CancellationFailure(
                "invalid_tool_cancellation_identity",
                "Tool cancellation requires exact owner, request, call, operation, and pending identities.");
        }

        if (request.ApprovalContinuationMode != AgentToolApprovalContinuationMode.ActorOwned ||
            request.ExecutionAttemptKind != AgentToolExecutionAttemptKind.ActorRecovery ||
            request.Reason != AgentToolOperationCancellationReason.WorkflowStopped ||
            request.DeadlineUnixMs <= 0)
        {
            return CancellationFailure(
                "invalid_tool_cancellation_attempt",
                "Durable tool cancellation requires an actor-owned recovery request.");
        }

        var executionContext = request.ExecutionContext with
        {
            Request = request.ExecutionContext.Request with { OperationId = operationId },
        };
        if (request.TerminalIntent is { } terminalIntent)
        {
            if (!IsValidCancellationTerminalIntent(terminalIntent, toolName, toolCallId))
            {
                return CancellationFailure(
                    "tool_cancellation_terminal_intent_invalid",
                    "The persisted tool cancellation terminal audit intent is invalid.");
            }

            var intentCredentialDecision = ResolveCredentials(
                executionContext,
                terminalIntent.IsMutation,
                toolName);
            return await FinalizeCancellationTerminalIntentAsync(
                tool,
                toolName,
                toolCallId,
                argumentsSha256,
                requestId,
                executionOwner,
                intentCredentialDecision,
                request.PendingOperation,
                terminalIntent,
                request.UnattendedAuthorization,
                ct).ConfigureAwait(false);
        }

        AgentToolCallSafety callSafety;
        AgentToolReplayPolicy replayPolicy;
        try
        {
            using var contextScope = AgentToolContextScope.Push(executionContext);
            callSafety = tool.GetCallSafety(argumentsJson)
                ?? throw new InvalidOperationException("Tool safety classification is required.");
            replayPolicy = tool.ResolveReplayPolicy(argumentsJson);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return CancellationFailure("tool_classification_failed", SafeExceptionClass(ex));
        }

        var isMutation = AgentToolCredentialPolicy.IsMutation(tool, callSafety);
        var credentialDecision = ResolveCredentials(executionContext, isMutation, toolName);
        if (HasCancellationDeadlineElapsed(request.DeadlineUnixMs))
        {
            return await FinalizeCancellationOutcomeUncertainAsync(
                tool,
                toolName,
                toolCallId,
                argumentsSha256,
                requestId,
                executionOwner,
                callSafety,
                isMutation,
                credentialDecision,
                request.PendingOperation,
                request.UnattendedAuthorization,
                ct).ConfigureAwait(false);
        }

        if (!credentialDecision.Allowed)
            return CancellationFailure("credential_denied", credentialDecision.Message);

        if (replayPolicy != AgentToolReplayPolicy.Reconcilable ||
            tool is not IAgentToolDurableOperation durableOperation ||
            ValidateReplayPolicy(
                tool,
                callSafety,
                replayPolicy,
                operationId,
                executionContext.Request.IdempotencyKey) is not null)
        {
            return CancellationFailure(
                "tool_operation_cancellation_unavailable",
                "Only a reconcilable durable tool can cancel an actor-owned pending operation.");
        }

        var admission = await TryStartAsync(
            new AgentToolAdmissionFact
            {
                AdmissionId = CreateAdmissionId(executionOwner, requestId, toolCallId),
                RequestId = requestId,
                ToolCallId = toolCallId,
                ToolName = toolName,
                ArgumentsSha256 = argumentsSha256,
                ExecutionOwner = ToProto(executionOwner),
                IssuedAtUnixMs = request.ExecutionContext.Request.IssuedAtUnixMs,
                OperationId = operationId,
                ReplayPolicy = replayPolicy,
            },
            ct).ConfigureAwait(false);
        if (HasCancellationDeadlineElapsed(request.DeadlineUnixMs))
        {
            return await FinalizeCancellationOutcomeUncertainAsync(
                tool,
                toolName,
                toolCallId,
                argumentsSha256,
                requestId,
                executionOwner,
                callSafety,
                isMutation,
                credentialDecision,
                request.PendingOperation,
                request.UnattendedAuthorization,
                ct).ConfigureAwait(false);
        }

        if (admission.Status != AgentToolAdmissionStatus.Duplicate)
        {
            return CancellationFailure(
                admission.Status == AgentToolAdmissionStatus.StoreUnavailable
                    ? "tool_admission_unavailable"
                    : "tool_cancellation_admission_not_duplicate",
                admission.Status == AgentToolAdmissionStatus.StoreUnavailable &&
                !string.IsNullOrWhiteSpace(admission.SafeMessage)
                    ? admission.SafeMessage
                    : "Tool cancellation requires the exact existing durable admission fact.");
        }

        var runningReceipt = AgentToolReceiptFactory.CreateRunning(
            tool,
            toolCallId,
            toolName,
            callSafety);
        var runningAppend = await AppendAsync(
            CreateRunningAuditId(executionOwner, requestId, toolCallId),
            AuditToolExecutionPhase.Running,
            tool,
            toolName,
            toolCallId,
            argumentsSha256,
            callSafety,
            credentialDecision.ExecutionContext,
            credentialDecision.CredentialSource,
            runningReceipt,
            AuditOutcome.Accepted,
            isMutation,
            ct,
            request.UnattendedAuthorization).ConfigureAwait(false);
        if (HasCancellationDeadlineElapsed(request.DeadlineUnixMs))
        {
            return await CompleteCancellationTerminalAsync(
                tool,
                toolName,
                toolCallId,
                argumentsSha256,
                requestId,
                executionOwner,
                callSafety,
                isMutation,
                credentialDecision,
                runningAppend,
                CreateCancellationOutcomeUncertain(
                    tool,
                    toolName,
                    toolCallId,
                    callSafety,
                    isMutation),
                request.PendingOperation,
                request.UnattendedAuthorization,
                ct).ConfigureAwait(false);
        }

        AgentToolOperationCancellationResult cancellation;
        try
        {
            using var contextScope = AgentToolContextScope.Push(credentialDecision.ExecutionContext);
            cancellation = await durableOperation.CancelOperationAsync(
                new AgentToolOperationCancellationRequest(
                    operationId,
                    argumentsJson,
                    credentialDecision.ExecutionContext,
                    request.PendingOperation,
                    request.Reason,
                    request.DeadlineUnixMs),
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (!HasCancellationDeadlineElapsed(request.DeadlineUnixMs))
            {
                return AgentToolCancellationResult.Pending(
                    request.PendingOperation,
                    "tool_cancellation_transport_unavailable",
                    SafeExceptionClass(ex));
            }

            return await CompleteCancellationTerminalAsync(
                tool,
                toolName,
                toolCallId,
                argumentsSha256,
                requestId,
                executionOwner,
                callSafety,
                isMutation,
                credentialDecision,
                runningAppend,
                CreateCancellationOutcomeUncertain(
                    tool,
                    toolName,
                    toolCallId,
                    callSafety,
                    isMutation),
                request.PendingOperation,
                request.UnattendedAuthorization,
                ct).ConfigureAwait(false);
        }

        if (cancellation.Disposition == AgentToolOperationCancellationDisposition.Pending &&
            cancellation.CompletedOutcome is null &&
            cancellation.PendingOperation is { } refreshed &&
            MatchesPendingOperationIdentity(request.PendingOperation, refreshed))
        {
            if (!HasCancellationDeadlineElapsed(request.DeadlineUnixMs))
                return AgentToolCancellationResult.Pending(refreshed);

            return await CompleteCancellationTerminalAsync(
                tool,
                toolName,
                toolCallId,
                argumentsSha256,
                requestId,
                executionOwner,
                callSafety,
                isMutation,
                credentialDecision,
                runningAppend,
                CreateCancellationOutcomeUncertain(
                    tool,
                    toolName,
                    toolCallId,
                    callSafety,
                    isMutation),
                refreshed,
                request.UnattendedAuthorization,
                ct).ConfigureAwait(false);
        }

        if (cancellation.Disposition != AgentToolOperationCancellationDisposition.Completed ||
            cancellation.CompletedOutcome is not { } terminalOutcome ||
            cancellation.PendingOperation is not null)
        {
            if (HasCancellationDeadlineElapsed(request.DeadlineUnixMs))
            {
                return await CompleteCancellationTerminalAsync(
                    tool,
                    toolName,
                    toolCallId,
                    argumentsSha256,
                    requestId,
                    executionOwner,
                    callSafety,
                    isMutation,
                    credentialDecision,
                    runningAppend,
                    CreateCancellationOutcomeUncertain(
                        tool,
                        toolName,
                        toolCallId,
                        callSafety,
                        isMutation),
                    request.PendingOperation,
                    request.UnattendedAuthorization,
                    ct).ConfigureAwait(false);
            }

            return AgentToolCancellationResult.Pending(
                request.PendingOperation,
                "tool_cancellation_outcome_invalid",
                "The durable tool returned an invalid cancellation outcome.");
        }

        var receipt = AgentToolReceiptFactory.CreateResult(
            tool,
            toolCallId,
            toolName,
            callSafety,
            terminalOutcome.ResultJson,
            terminalOutcome.Receipt,
            argumentsJson);
        var outcome = new AgentToolExecutionOutcome(
            AgentToolExecutionOutcomeKind.Executed,
            receipt.ResultJson ?? string.Empty,
            receipt,
            isMutation,
            string.Empty,
            string.Empty,
            AgentToolExecutionFailureStage.None,
            TerminalInvoked: true,
            Retryable: false,
            AuditCompleted: false);
        return await CompleteCancellationTerminalAsync(
            tool,
            toolName,
            toolCallId,
            argumentsSha256,
            requestId,
            executionOwner,
            callSafety,
            isMutation,
            credentialDecision,
            runningAppend,
            outcome,
            request.PendingOperation,
            request.UnattendedAuthorization,
            ct).ConfigureAwait(false);
    }

    private async Task<AgentToolCancellationResult> FinalizeCancellationOutcomeUncertainAsync(
        IAgentTool tool,
        string toolName,
        string toolCallId,
        string argumentsSha256,
        string requestId,
        ExecutionOwnerIdentity executionOwner,
        AgentToolCallSafety callSafety,
        bool isMutation,
        CredentialDecision credentialDecision,
        AgentToolPendingOperation pendingOperation,
        AgentToolUnattendedExecutionAuthorization? unattendedAuthorization,
        CancellationToken ct) =>
        await FinalizeCancellationOutcomeAsync(
            tool,
            toolName,
            toolCallId,
            argumentsSha256,
            requestId,
            executionOwner,
            callSafety,
            isMutation,
            credentialDecision,
            pendingOperation,
            CreateCancellationOutcomeUncertain(
                tool,
                toolName,
                toolCallId,
                callSafety,
                isMutation),
            unattendedAuthorization,
            ct).ConfigureAwait(false);

    private async Task<AgentToolCancellationResult> FinalizeCancellationTerminalIntentAsync(
        IAgentTool tool,
        string toolName,
        string toolCallId,
        string argumentsSha256,
        string requestId,
        ExecutionOwnerIdentity executionOwner,
        CredentialDecision credentialDecision,
        AgentToolPendingOperation pendingOperation,
        AgentToolCancellationTerminalIntent terminalIntent,
        AgentToolUnattendedExecutionAuthorization? unattendedAuthorization,
        CancellationToken ct) =>
        await FinalizeCancellationOutcomeAsync(
            tool,
            toolName,
            toolCallId,
            argumentsSha256,
            requestId,
            executionOwner,
            terminalIntent.CallSafety,
            terminalIntent.IsMutation,
            credentialDecision,
            pendingOperation,
            CreateCancellationOutcomeFromIntent(terminalIntent),
            unattendedAuthorization,
            ct).ConfigureAwait(false);

    private async Task<AgentToolCancellationResult> FinalizeCancellationOutcomeAsync(
        IAgentTool tool,
        string toolName,
        string toolCallId,
        string argumentsSha256,
        string requestId,
        ExecutionOwnerIdentity executionOwner,
        AgentToolCallSafety callSafety,
        bool isMutation,
        CredentialDecision credentialDecision,
        AgentToolPendingOperation pendingOperation,
        AgentToolExecutionOutcome outcome,
        AgentToolUnattendedExecutionAuthorization? unattendedAuthorization,
        CancellationToken ct)
    {
        var runningReceipt = AgentToolReceiptFactory.CreateRunning(
            tool,
            toolCallId,
            toolName,
            callSafety);
        var runningAppend = await AppendAsync(
            CreateRunningAuditId(executionOwner, requestId, toolCallId),
            AuditToolExecutionPhase.Running,
            tool,
            toolName,
            toolCallId,
            argumentsSha256,
            callSafety,
            credentialDecision.ExecutionContext,
            credentialDecision.CredentialSource,
            runningReceipt,
            AuditOutcome.Accepted,
            isMutation,
            ct,
            unattendedAuthorization).ConfigureAwait(false);
        return await CompleteCancellationTerminalAsync(
            tool,
            toolName,
            toolCallId,
            argumentsSha256,
            requestId,
            executionOwner,
            callSafety,
            isMutation,
            credentialDecision,
            runningAppend,
            outcome,
            pendingOperation,
            unattendedAuthorization,
            ct).ConfigureAwait(false);
    }

    private async Task<AgentToolCancellationResult> CompleteCancellationTerminalAsync(
        IAgentTool tool,
        string toolName,
        string toolCallId,
        string argumentsSha256,
        string requestId,
        ExecutionOwnerIdentity executionOwner,
        AgentToolCallSafety callSafety,
        bool isMutation,
        CredentialDecision credentialDecision,
        AuditTrailAppendResult runningAppend,
        AgentToolExecutionOutcome outcome,
        AgentToolPendingOperation pendingOperation,
        AgentToolUnattendedExecutionAuthorization? unattendedAuthorization,
        CancellationToken ct)
    {
        if (!IsAuditRecorded(runningAppend))
        {
            return AgentToolCancellationResult.Pending(
                pendingOperation,
                runningAppend.Status == AuditTrailAppendStatus.Conflict
                    ? "audit_intent_conflict"
                    : "audit_unavailable",
                "Tool cancellation reached a terminal outcome, but its running audit fact was not durably recorded.",
                terminalIntent: ToCancellationTerminalIntent(outcome, callSafety, argumentsSha256));
        }

        var terminalAppend = await AppendAsync(
            CreateTerminalAuditId(executionOwner, requestId, toolCallId),
            AuditToolExecutionPhase.Terminal,
            tool,
            toolName,
            toolCallId,
            argumentsSha256,
            callSafety,
            credentialDecision.ExecutionContext,
            credentialDecision.CredentialSource,
            outcome.Receipt,
            MapAuditOutcome(outcome),
            isMutation,
            ct,
            unattendedAuthorization).ConfigureAwait(false);
        if (IsAuditRecorded(terminalAppend))
            return AgentToolCancellationResult.Completed(outcome with { AuditCompleted = true });

        return AgentToolCancellationResult.Pending(
            pendingOperation,
            terminalAppend.Status == AuditTrailAppendStatus.Conflict
                ? "audit_intent_conflict"
                : "audit_unavailable",
            "Tool cancellation reached a terminal outcome, but its stable audit fact was not durably recorded.",
            terminalIntent: ToCancellationTerminalIntent(outcome, callSafety, argumentsSha256));
    }

    private static AgentToolExecutionOutcome CreateCancellationOutcomeFromIntent(
        AgentToolCancellationTerminalIntent intent) =>
        new(
            intent.Kind,
            intent.ResultJson,
            intent.Receipt.Clone(),
            intent.IsMutation,
            intent.FailureCode,
            intent.SafeMessage,
            intent.FailureStage,
            intent.TerminalInvoked,
            intent.Retryable,
            AuditCompleted: false);

    private static bool IsValidCancellationTerminalIntent(
        AgentToolCancellationTerminalIntent intent,
        string toolName,
        string toolCallId) =>
        intent.Receipt != null &&
        intent.CallSafety != null &&
        intent.Kind is AgentToolExecutionOutcomeKind.Executed or AgentToolExecutionOutcomeKind.Failed &&
        Enum.IsDefined(intent.FailureStage) &&
        string.Equals(NormalizeIdentity(intent.Receipt.ToolName), toolName, StringComparison.Ordinal) &&
        string.Equals(NormalizeIdentity(intent.Receipt.CallId), toolCallId, StringComparison.Ordinal) &&
        string.Equals(intent.Receipt.ResultJson ?? string.Empty, intent.ResultJson, StringComparison.Ordinal) &&
        NormalizeArgumentsSha256(intent.ArgumentsSha256) is not null;

    private static AgentToolCancellationTerminalIntent ToCancellationTerminalIntent(
        AgentToolExecutionOutcome outcome,
        AgentToolCallSafety callSafety,
        string argumentsSha256) =>
        new(
            outcome.Kind,
            outcome.ResultJson,
            outcome.Receipt.Clone(),
            outcome.IsMutation,
            outcome.FailureCode,
            outcome.SafeMessage,
            outcome.FailureStage,
            outcome.TerminalInvoked,
            outcome.Retryable,
            callSafety,
            argumentsSha256);

    private static string? NormalizeArgumentsSha256(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var normalized = value.Trim().ToLowerInvariant();
        return normalized.Length == 64 && normalized.All(Uri.IsHexDigit)
            ? normalized
            : null;
    }

    private static AgentToolExecutionOutcome CreateCancellationOutcomeUncertain(
        IAgentTool tool,
        string toolName,
        string toolCallId,
        AgentToolCallSafety callSafety,
        bool isMutation) =>
        CreateFailure(
            tool,
            toolName,
            toolCallId,
            callSafety,
            isMutation,
            "code_execution_cancel_outcome_uncertain",
            "The provider terminal outcome could not be confirmed before the workflow stop deadline.",
            AgentToolExecutionFailureStage.TerminalExecution,
            terminalInvoked: true,
            retryable: false,
            auditCompleted: false,
            failureOutcome: AgentToolFailureOutcome.OutcomeUncertain);

    private static AgentToolCancellationResult CancellationFailure(
        string failureCode,
        string safeMessage) =>
        AgentToolCancellationResult.Failed(failureCode, safeMessage, retryable: true);

    private bool HasCancellationDeadlineElapsed(long deadlineUnixMs) =>
        deadlineUnixMs > 0 &&
        deadlineUnixMs <= _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

    private static bool MatchesPendingOperationIdentity(
        AgentToolPendingOperation expected,
        AgentToolPendingOperation candidate) =>
        string.Equals(expected.OperationId, candidate.OperationId, StringComparison.Ordinal) &&
        string.Equals(expected.ProviderOperationId, candidate.ProviderOperationId, StringComparison.Ordinal) &&
        string.Equals(expected.StatusPath, candidate.StatusPath, StringComparison.Ordinal) &&
        string.Equals(expected.ResultPath, candidate.ResultPath, StringComparison.Ordinal) &&
        string.Equals(expected.CancelPath, candidate.CancelPath, StringComparison.Ordinal) &&
        string.Equals(expected.ServiceSlug, candidate.ServiceSlug, StringComparison.Ordinal) &&
        string.Equals(expected.UserServiceId, candidate.UserServiceId, StringComparison.Ordinal) &&
        expected.RouteIdentitySource == candidate.RouteIdentitySource;

    private async Task<AgentToolExecutionOutcome> ExecuteTerminalAsync(
        IAgentTool tool,
        string toolName,
        string toolCallId,
        string argumentsJson,
        string argumentsSha256,
        string requestId,
        ExecutionOwnerIdentity executionOwner,
        AgentToolCallSafety callSafety,
        bool isMutation,
        CredentialDecision credentialDecision,
        AuditTrailAppendResult runningAppend,
        AgentToolReceipt runningReceipt,
        AgentToolReplayPolicy replayPolicy,
        string operationId,
        AgentToolTerminalOutcome? reconciledOutcome,
        AgentToolPendingOperation? reconciledPendingOperation,
        AgentToolUnattendedExecutionAuthorization? unattendedAuthorization,
        CancellationToken ct)
    {
        using var activity = GenAIActivitySource.StartExecuteTool(toolName, toolCallId);
        var startedAt = Stopwatch.GetTimestamp();
        AgentToolExecutionOutcome outcome;
        try
        {
            AgentToolOperationStartResult operationResult;
            if (reconciledOutcome is not null)
            {
                operationResult = AgentToolOperationStartResult.Completed(reconciledOutcome);
            }
            else if (reconciledPendingOperation is not null)
            {
                operationResult = AgentToolOperationStartResult.Pending(reconciledPendingOperation);
            }
            else if (replayPolicy == AgentToolReplayPolicy.Reconcilable &&
                     tool is IAgentToolDurableOperation durableOperation)
            {
                using var contextScope = AgentToolContextScope.Push(credentialDecision.ExecutionContext);
                operationResult = await durableOperation.StartOperationAsync(
                    new AgentToolOperationStartRequest(
                        operationId,
                        toolCallId,
                        toolName,
                        argumentsJson,
                        credentialDecision.ExecutionContext),
                    ct).ConfigureAwait(false);
            }
            else
            {
                using var contextScope = AgentToolContextScope.Push(credentialDecision.ExecutionContext);
                operationResult = AgentToolOperationStartResult.Completed(
                    await tool.ExecuteWithOutcomeAsync(
                        toolCallId,
                        toolName,
                        argumentsJson,
                        ct).ConfigureAwait(false));
            }

            if (operationResult.Disposition == AgentToolOperationStartDisposition.Pending &&
                operationResult.PendingOperation is { } pendingOperation &&
                operationResult.CompletedOutcome is null &&
                string.Equals(
                    pendingOperation.OperationId,
                    operationId,
                    StringComparison.Ordinal))
            {
                activity?.SetTag("gen_ai.tool.status", "pending");
                return new AgentToolExecutionOutcome(
                    AgentToolExecutionOutcomeKind.Pending,
                    string.Empty,
                    runningReceipt,
                    isMutation,
                    string.Empty,
                    "Tool execution is pending durable provider completion.",
                    AgentToolExecutionFailureStage.None,
                    TerminalInvoked: reconciledPendingOperation is null,
                    Retryable: false,
                    AuditCompleted: IsAuditRecorded(runningAppend),
                    PendingOperation: pendingOperation,
                    CancellationRecoveryIntent: ToCancellationTerminalIntent(
                        CreateCancellationOutcomeUncertain(
                            tool,
                            toolName,
                            toolCallId,
                            callSafety,
                            isMutation),
                        callSafety,
                        argumentsSha256));
            }

            if (operationResult.Disposition != AgentToolOperationStartDisposition.Completed ||
                operationResult.CompletedOutcome is not { } terminalOutcome ||
                operationResult.PendingOperation is not null)
                throw new InvalidOperationException("The durable tool returned an invalid typed operation outcome.");

            var resultJson = terminalOutcome.ResultJson;
            var receipt = AgentToolReceiptFactory.CreateResult(
                tool,
                toolCallId,
                toolName,
                callSafety,
                resultJson,
                terminalOutcome.Receipt,
                argumentsJson);
            var safeResultJson = receipt.ResultJson ?? string.Empty;
            outcome = new AgentToolExecutionOutcome(
                AgentToolExecutionOutcomeKind.Executed,
                safeResultJson,
                receipt,
                isMutation,
                string.Empty,
                string.Empty,
                AgentToolExecutionFailureStage.None,
                TerminalInvoked: reconciledOutcome is null,
                Retryable: false,
                AuditCompleted: false);
            activity?.SetTag("gen_ai.tool.status", "ok");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogCodexExecutionFailure(ex, credentialDecision.ExecutionContext.WorkflowRuntime);
            var failureEvidence = ResolveExceptionFailureEvidence(ex);
            outcome = CreateFailure(
                tool,
                toolName,
                toolCallId,
                callSafety,
                isMutation,
                failureEvidence.Code,
                failureEvidence.Message,
                AgentToolExecutionFailureStage.TerminalExecution,
                terminalInvoked: reconciledOutcome is null,
                retryable: false,
                auditCompleted: false,
                diagnosticId: failureEvidence.DiagnosticId,
                failureOutcome: ToolExecutionAuditErrorCode.IsTimeout(failureEvidence.Code) ||
                                isMutation && string.Equals(
                                    failureEvidence.Code,
                                    "tool_execution_exception",
                                    StringComparison.Ordinal)
                    ? AgentToolFailureOutcome.OutcomeUncertain
                    : AgentToolFailureOutcome.CalleeConfirmed);
            activity?.SetTag("gen_ai.tool.status", "error");
            activity?.SetTag("error.type", ex.GetType().FullName);
            activity?.SetStatus(ActivityStatusCode.Error, outcome.SafeMessage);
        }
        finally
        {
            GenAIActivitySource.ToolInvocationDuration.Record(
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                new KeyValuePair<string, object?>("gen_ai.tool.name", toolName));
        }

        var terminalAppend = await AppendAsync(
            CreateTerminalAuditId(executionOwner, requestId, toolCallId),
            AuditToolExecutionPhase.Terminal,
            tool,
            toolName,
            toolCallId,
            argumentsSha256,
            callSafety,
            credentialDecision.ExecutionContext,
            credentialDecision.CredentialSource,
            outcome.Receipt,
            MapAuditOutcome(outcome),
            isMutation,
            ct,
            unattendedAuthorization).ConfigureAwait(false);
        var auditCompleted = IsAuditRecorded(runningAppend) && IsAuditRecorded(terminalAppend);
        if (auditCompleted)
            return outcome with { AuditCompleted = true };

        if (outcome.Kind == AgentToolExecutionOutcomeKind.Executed)
        {
            var hasConflict = runningAppend.Status == AuditTrailAppendStatus.Conflict ||
                              terminalAppend.Status == AuditTrailAppendStatus.Conflict;
            return outcome with
            {
                Kind = AgentToolExecutionOutcomeKind.ExecutedAuditIncomplete,
                FailureCode = hasConflict
                    ? "audit_intent_conflict"
                    : "audit_unavailable",
                SafeMessage = "Tool execution completed, but the terminal audit fact was not durably recorded.",
                FailureStage = AgentToolExecutionFailureStage.TerminalAudit,
                AuditCompleted = false,
            };
        }

        return outcome with { AuditCompleted = false };
    }

    private async Task<AgentToolExecutionOutcome> CreateApprovalRequiredAsync(
        IAgentTool tool,
        string toolName,
        string toolCallId,
        AgentToolCallSafety callSafety,
        bool isMutation,
        string approvalRequestId,
        ExecutionOwnerIdentity executionOwner,
        string requestId,
        string argumentsSha256,
        CredentialDecision credentialDecision,
        CancellationToken ct)
    {
        var resultJson = JsonSerializer.Serialize(new
        {
            approval_required = true,
            request_id = approvalRequestId,
            tool_name = toolName,
            tool_call_id = toolCallId,
            message = "This tool requires durable approval before execution.",
        });
        var receipt = AgentToolReceiptFactory.CreateApprovalRequired(
            tool,
            toolCallId,
            toolName,
            callSafety,
            resultJson,
            approvalRequestId);
        var outcome = new AgentToolExecutionOutcome(
            AgentToolExecutionOutcomeKind.ApprovalRequired,
            resultJson,
            receipt,
            isMutation,
            string.Empty,
            "Tool execution is waiting for durable approval.",
            AgentToolExecutionFailureStage.Approval,
            TerminalInvoked: false,
            Retryable: false,
            AuditCompleted: false);
        var append = await AppendAsync(
            CreateWaitingApprovalAuditId(executionOwner, requestId, toolCallId, approvalRequestId),
            AuditToolExecutionPhase.WaitingApproval,
            tool,
            toolName,
            toolCallId,
            argumentsSha256,
            callSafety,
            credentialDecision.ExecutionContext,
            credentialDecision.CredentialSource,
            receipt,
            AuditOutcome.Accepted,
            isMutation,
            ct).ConfigureAwait(false);
        return outcome with { AuditCompleted = IsAuditRecorded(append) };
    }

    private async Task<AgentToolExecutionOutcome> CompleteBeforeTerminalAsync(
        IAgentTool tool,
        AgentToolExecutionOutcome outcome,
        AgentToolExecutionContext executionContext,
        AgentToolCredentialSource credentialSource,
        ExecutionOwnerIdentity executionOwner,
        string requestId,
        string toolName,
        string toolCallId,
        string argumentsSha256,
        AgentToolCallSafety callSafety,
        CancellationToken ct)
    {
        var append = await AppendAsync(
            CreateTerminalAuditId(executionOwner, requestId, toolCallId),
            AuditToolExecutionPhase.Terminal,
            tool,
            toolName,
            toolCallId,
            argumentsSha256,
            callSafety,
            executionContext,
            credentialSource,
            outcome.Receipt,
            MapAuditOutcome(outcome),
            outcome.IsMutation,
            ct).ConfigureAwait(false);

        return outcome with { AuditCompleted = IsAuditRecorded(append) };
    }

    private async Task<AgentToolAdmissionResult> TryStartAsync(
        AgentToolAdmissionFact fact,
        CancellationToken ct)
    {
        try
        {
            return await _admissionLedger.TryStartAsync(fact, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new AgentToolAdmissionResult(
                AgentToolAdmissionStatus.StoreUnavailable,
                SafeExceptionClass(ex));
        }
    }

    private async Task<DuplicateRecoveryResolution> ResolveDuplicateRecoveryAsync(
        IAgentTool tool,
        AgentToolReplayPolicy replayPolicy,
        string operationId,
        string argumentsJson,
        AgentToolCallSafety callSafety,
        bool isMutation,
        string toolName,
        string toolCallId,
        AgentToolExecutionContext executionContext,
        AgentToolPendingOperation? pendingOperation,
        CancellationToken ct)
    {
        if (replayPolicy is AgentToolReplayPolicy.ReadOnlyRetryable or
            AgentToolReplayPolicy.IdempotentRetryable)
        {
            return new DuplicateRecoveryResolution(null, null, null);
        }

        if (replayPolicy == AgentToolReplayPolicy.Reconcilable &&
            tool is IAgentToolOperationReconciler reconciler)
        {
            AgentToolOperationReconciliationResult? reconciliation;
            try
            {
                using var contextScope = AgentToolContextScope.Push(executionContext);
                reconciliation = await reconciler.ReconcileOperationAsync(
                    new AgentToolOperationReconciliationRequest(
                        operationId,
                        argumentsJson,
                        executionContext,
                        pendingOperation),
                    ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                reconciliation = null;
            }

            if (reconciliation?.Disposition == AgentToolOperationReconciliationDisposition.NotFound &&
                string.IsNullOrWhiteSpace(pendingOperation?.ProviderOperationId))
                return new DuplicateRecoveryResolution(null, null, null);
            if (reconciliation?.Disposition == AgentToolOperationReconciliationDisposition.Completed &&
                reconciliation.CompletedOutcome is not null)
            {
                return new DuplicateRecoveryResolution(reconciliation.CompletedOutcome, null, null);
            }
            if (reconciliation?.Disposition == AgentToolOperationReconciliationDisposition.Pending &&
                reconciliation.PendingOperation is { } reconciledPending &&
                reconciliation.CompletedOutcome is null &&
                string.Equals(
                    reconciledPending.OperationId,
                    operationId,
                    StringComparison.Ordinal))
            {
                return new DuplicateRecoveryResolution(null, reconciledPending, null);
            }
        }

        const string failureCode = "outcome_uncertain";
        const string safeMessage =
            "OUTCOME_UNCERTAIN: the prior external effect cannot be proven complete or safe to replay.";
        return new DuplicateRecoveryResolution(
            null,
            null,
            CreateFailure(
                tool,
                toolName,
                toolCallId,
                callSafety,
                isMutation,
                failureCode,
                safeMessage,
                AgentToolExecutionFailureStage.Admission,
                terminalInvoked: false,
                retryable: false,
                auditCompleted: false,
                failureOutcome: AgentToolFailureOutcome.OutcomeUncertain));
    }

    private static ReplayPolicyFailure? ValidateReplayPolicy(
        IAgentTool tool,
        AgentToolCallSafety callSafety,
        AgentToolReplayPolicy replayPolicy,
        string operationId,
        string? idempotencyKey)
    {
        if (replayPolicy == AgentToolReplayPolicy.Unspecified ||
            !Enum.IsDefined(replayPolicy))
        {
            return new ReplayPolicyFailure(
                "invalid_tool_replay_policy",
                "The tool must declare a supported replay policy.");
        }

        if (replayPolicy == AgentToolReplayPolicy.ReadOnlyRetryable &&
            (!callSafety.IsReadOnly || callSafety.IsDestructive))
        {
            return new ReplayPolicyFailure(
                "invalid_read_only_replay_policy",
                "READ_ONLY_RETRYABLE requires a non-destructive read-only invocation.");
        }

        if (replayPolicy == AgentToolReplayPolicy.IdempotentRetryable &&
            !string.Equals(
                NormalizeIdentity(idempotencyKey),
                operationId,
                StringComparison.Ordinal))
        {
            return new ReplayPolicyFailure(
                "invalid_idempotent_replay_key",
                "IDEMPOTENT_RETRYABLE requires idempotency_key to exactly equal operation_id.");
        }

        if (replayPolicy == AgentToolReplayPolicy.Reconcilable &&
            tool is not IAgentToolDurableOperation)
        {
            return new ReplayPolicyFailure(
                "missing_tool_operation_reconciler",
                "RECONCILABLE requires a tool-owned durable operation implementation.");
        }

        return null;
    }

    private async Task<AuditTrailAppendResult> AppendAsync(
        string auditId,
        AuditToolExecutionPhase executionPhase,
        IAgentTool tool,
        string toolName,
        string toolCallId,
        string argumentsSha256,
        AgentToolCallSafety callSafety,
        AgentToolExecutionContext executionContext,
        AgentToolCredentialSource credentialSource,
        AgentToolReceipt receipt,
        AuditOutcome outcome,
        bool isMutation,
        CancellationToken ct,
        AgentToolUnattendedExecutionAuthorization? unattendedAuthorization = null)
    {
        try
        {
            var record = _auditRecordFactory.Create(
                auditId,
                executionPhase,
                tool,
                toolName,
                toolCallId,
                argumentsSha256,
                callSafety,
                executionContext,
                credentialSource,
                receipt,
                outcome,
                isMutation,
                unattendedAuthorization is null ? null : "unattended_exact",
                unattendedAuthorization?.AuthorizationId);
            return await _auditTrailAppender.AppendAsync(record, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return AuditTrailAppendResult.StoreUnavailable(auditId, SafeExceptionClass(ex));
        }
    }

    private static CredentialDecision ResolveCredentials(
        AgentToolExecutionContext context,
        bool isMutation,
        string toolName)
    {
        if (context.Credentials.NyxIdCredentialKind is
            AgentToolNyxIdCredentialKind.ProxyDelegation or
            AgentToolNyxIdCredentialKind.AgentKey)
        {
            var primaryCredential = NormalizeIdentity(context.Credentials.NyxIdAccessToken);
            if (primaryCredential is null)
            {
                var credentialLabel = context.Credentials.NyxIdCredentialKind ==
                                      AgentToolNyxIdCredentialKind.AgentKey
                    ? "Agent Key"
                    : "proxy delegation credential";
                return new CredentialDecision(
                    false,
                    context,
                    ResolveCredentialSource(context),
                    $"Tool '{toolName}' was not executed because the typed NyxID {credentialLabel} has no valid primary value. Credential fallback was not used.");
            }

            var primaryContext = context with
            {
                Credentials = context.Credentials with
                {
                    NyxIdAccessToken = primaryCredential,
                    NyxIdOrgToken = null,
                    SenderNyxIdAccessToken = null,
                    SourceReadableNyxIdAccessToken =
                        NormalizeIdentity(context.Credentials.SourceReadableNyxIdAccessToken),
                },
            };
            return new CredentialDecision(
                true,
                primaryContext,
                ResolveCredentialSource(primaryContext),
                string.Empty);
        }

        var senderBindingId = NormalizeIdentity(context.SenderBinding.BindingId);
        if (senderBindingId is null)
        {
            var isChannelMediated = NormalizeIdentity(context.Channel.SenderId) is not null;
            if (!isChannelMediated || !isMutation)
            {
                return new CredentialDecision(
                    true,
                    context,
                    ResolveDirectCredentialSource(context),
                    string.Empty);
            }

            return new CredentialDecision(
                false,
                context,
                AgentToolCredentialSource.ChannelRegistration,
                $"Tool '{toolName}' was not executed because the channel sender is not bound to a NyxID account.");
        }

        var senderToken = NormalizeIdentity(context.Credentials.SenderNyxIdAccessToken);
        if (senderToken is not null)
        {
            var senderContext = context with
            {
                Credentials = context.Credentials with
                {
                    NyxIdAccessToken = senderToken,
                    NyxIdOrgToken = senderToken,
                    SenderNyxIdAccessToken = senderToken,
                    NyxIdCredentialKind = AgentToolNyxIdCredentialKind.SourceReadableUserBearer,
                },
            };
            return new CredentialDecision(
                true,
                senderContext,
                ResolveCredentialSource(senderContext),
                string.Empty);
        }

        if (!isMutation)
            return new CredentialDecision(true, context, ResolveDirectCredentialSource(context), string.Empty);

        return new CredentialDecision(
            false,
            context,
            AgentToolCredentialSource.ChannelRegistration,
            $"Tool '{toolName}' was not executed because the bound sender has no valid NyxID credential.");
    }

    private static bool RequiresApproval(IAgentTool tool, AgentToolCallSafety callSafety)
    {
        if (tool.ApprovalMode == ToolApprovalMode.NeverRequire)
            return false;
        if (callSafety.RequiresApproval.HasValue)
            return callSafety.RequiresApproval.Value;
        if (tool.ApprovalMode == ToolApprovalMode.AlwaysRequire)
            return true;
        return !callSafety.IsReadOnly && callSafety.IsDestructive;
    }

    private static bool MatchesGrant(
        AgentToolApprovalGrant grant,
        AgentToolApprovalContinuationMode continuationMode,
        string approvalRequestId,
        ExecutionOwnerIdentity executionOwner,
        string requestId,
        string toolName,
        string toolCallId,
        string argumentsSha256) =>
        continuationMode == AgentToolApprovalContinuationMode.ActorOwned &&
        MatchesExecutionOwner(grant.ExecutionOwner, executionOwner) &&
        string.Equals(NormalizeIdentity(grant.ApprovalRequestId), approvalRequestId, StringComparison.Ordinal) &&
        string.Equals(NormalizeIdentity(grant.RequestId), requestId, StringComparison.Ordinal) &&
        string.Equals(NormalizeIdentity(grant.ToolName), toolName, StringComparison.Ordinal) &&
        string.Equals(NormalizeIdentity(grant.ToolCallId), toolCallId, StringComparison.Ordinal) &&
        string.Equals(NormalizeIdentity(grant.ArgumentsSha256), argumentsSha256, StringComparison.Ordinal);

    private static bool MatchesUnattendedAuthorization(
        AgentToolUnattendedExecutionAuthorization authorization,
        AgentToolApprovalContinuationMode continuationMode,
        bool requiresApproval,
        bool isMutation,
        AgentToolCallSafety callSafety,
        AgentToolExecutionContext executionContext,
        ExecutionOwnerIdentity executionOwner,
        string requestId,
        string toolName,
        string toolCallId,
        string argumentsSha256)
    {
        var admission = executionContext.OperationAdmission;
        return authorization.Kind == AgentToolUnattendedAuthorizationKind.WorkflowWebhookExact &&
               continuationMode == AgentToolApprovalContinuationMode.ActorOwned &&
               requiresApproval &&
               isMutation &&
               !callSafety.IsReadOnly &&
               !callSafety.IsDestructive &&
               executionContext.InvocationSurface == AgentToolInvocationSurface.WorkflowToolCall &&
               (executionContext.Credentials.NyxIdCredentialKind is
                   AgentToolNyxIdCredentialKind.ProxyDelegation or
                   AgentToolNyxIdCredentialKind.AgentKey) &&
               !string.IsNullOrWhiteSpace(executionContext.Credentials.NyxIdAccessToken) &&
               !string.IsNullOrWhiteSpace(executionContext.NyxIdAuthority.Platform) &&
               !string.IsNullOrWhiteSpace(executionContext.NyxIdAuthority.ExternalUserId) &&
               string.Equals(toolName, "nyxid_proxy", StringComparison.Ordinal) &&
               MatchesExecutionOwner(authorization.ExecutionOwner, executionOwner) &&
               string.Equals(NormalizeIdentity(authorization.RequestId), requestId, StringComparison.Ordinal) &&
               string.Equals(NormalizeIdentity(authorization.ToolName), toolName, StringComparison.Ordinal) &&
               string.Equals(NormalizeIdentity(authorization.ToolCallId), toolCallId, StringComparison.Ordinal) &&
               string.Equals(NormalizeIdentity(authorization.ArgumentsSha256), argumentsSha256, StringComparison.Ordinal) &&
               !string.IsNullOrWhiteSpace(authorization.AuthorizationId) &&
               !string.IsNullOrWhiteSpace(authorization.CallSiteId) &&
               admission is
               {
                   AuthorizationBasis: AgentToolOperationAuthorizationBasis.ExplicitRequest,
                   Identity: AgentToolOperationIdentity.AuthoredRequest,
                   ExecutionPolicy:
                   {
                       Risk: AgentToolOperationRisk.Write,
                       Approval: AgentToolOperationApproval.Required,
                       EnforcementOwner: AgentToolOperationEnforcementOwner.Aevatar,
                   },
               } &&
               admission.ExecutionPolicy.AllowedExecutionModes.Contains(
                   AgentToolOperationExecutionMode.Durable) &&
               string.Equals(
                   NormalizeIdentity(authorization.OperationSelectorDigest),
                   AgentToolOperationSelector.ComputeDigest(admission),
                   StringComparison.Ordinal);
    }

    private static AgentToolExecutionOutcome CreateDenied(
        IAgentTool tool,
        string toolName,
        string toolCallId,
        AgentToolCallSafety callSafety,
        bool isMutation,
        string failureCode,
        string safeMessage,
        AgentToolExecutionFailureStage failureStage,
        string approvalRequestId = "")
    {
        var resultJson = BuildFailureJson(failureCode, safeMessage, toolName);
        var receipt = AgentToolReceiptFactory.CreateDenied(
            tool,
            toolCallId,
            toolName,
            callSafety,
            resultJson,
            failureCode,
            safeMessage,
            approvalRequestId);
        return new AgentToolExecutionOutcome(
            AgentToolExecutionOutcomeKind.Denied,
            resultJson,
            receipt,
            isMutation,
            failureCode,
            safeMessage,
            failureStage,
            TerminalInvoked: false,
            Retryable: false,
            AuditCompleted: false);
    }

    private static AgentToolExecutionOutcome CreateFailure(
        IAgentTool tool,
        string toolName,
        string toolCallId,
        AgentToolCallSafety callSafety,
        bool isMutation,
        string failureCode,
        string safeMessage,
        AgentToolExecutionFailureStage failureStage,
        bool terminalInvoked,
        bool retryable,
        bool auditCompleted,
        string? diagnosticId = null,
        AgentToolFailureOutcome failureOutcome = AgentToolFailureOutcome.CalleeConfirmed)
    {
        var resultJson = BuildFailureJson(failureCode, safeMessage, toolName, diagnosticId);
        var receipt = AgentToolReceiptFactory.CreateError(
            tool,
            toolCallId,
            toolName,
            callSafety,
            resultJson,
            failureCode,
            safeMessage);
        receipt.FailureOutcome = failureOutcome;
        return new AgentToolExecutionOutcome(
            AgentToolExecutionOutcomeKind.Failed,
            resultJson,
            receipt,
            isMutation,
            failureCode,
            safeMessage,
            failureStage,
            terminalInvoked,
            retryable,
            auditCompleted);
    }

    private static AgentToolExecutionOutcome CreateUnauditedFailure(
        IAgentTool tool,
        string toolName,
        string toolCallId,
        AgentToolCallSafety callSafety,
        string failureCode,
        string safeMessage,
        AgentToolExecutionFailureStage failureStage) =>
        CreateFailure(
            tool,
            toolName,
            toolCallId,
            callSafety,
            isMutation: true,
            failureCode,
            safeMessage,
            failureStage,
            terminalInvoked: false,
            retryable: false,
            auditCompleted: false);

    private static AuditOutcome MapAuditOutcome(AgentToolExecutionOutcome outcome) =>
        outcome.Kind switch
        {
            AgentToolExecutionOutcomeKind.Executed
                when outcome.Receipt.Status == AgentToolReceiptStatus.Success => AuditOutcome.Success,
            AgentToolExecutionOutcomeKind.ApprovalRequired => AuditOutcome.Accepted,
            AgentToolExecutionOutcomeKind.Denied => AuditOutcome.Denied,
            _ => AuditOutcome.Error,
        };

    private static AgentToolCredentialSource ResolveDirectCredentialSource(AgentToolExecutionContext context)
    {
        if (context.CredentialSource != AgentToolCredentialSource.Unspecified)
            return context.CredentialSource;
        if (!string.IsNullOrWhiteSpace(context.Schedule.ScheduleId))
            return AgentToolCredentialSource.ScheduledRun;
        return string.IsNullOrWhiteSpace(context.Credentials.NyxIdAccessToken)
            ? AgentToolCredentialSource.System
            : AgentToolCredentialSource.BearerToken;
    }

    private static AgentToolCredentialSource ResolveCredentialSource(AgentToolExecutionContext context)
    {
        if (context.CredentialSource != AgentToolCredentialSource.Unspecified)
            return context.CredentialSource;
        if (!string.IsNullOrWhiteSpace(context.Schedule.ScheduleId))
            return AgentToolCredentialSource.ScheduledRun;
        return string.IsNullOrWhiteSpace(context.SenderBinding.BindingId)
            ? ResolveDirectCredentialSource(context)
            : AgentToolCredentialSource.ChannelRegistration;
    }

    private static string CreateApprovalRequestId(
        ExecutionOwnerIdentity executionOwner,
        string requestId,
        string toolName,
        string toolCallId,
        string argumentsSha256) =>
        "tool-approval:v1:" + HashLengthPrefixed(
            OwnerKindValue(executionOwner),
            executionOwner.OwnerId,
            requestId,
            toolName,
            toolCallId,
            argumentsSha256);

    private static string CreateAdmissionId(
        ExecutionOwnerIdentity executionOwner,
        string requestId,
        string toolCallId) =>
        "tool:v1:admission:" + HashLengthPrefixed(
            OwnerKindValue(executionOwner), executionOwner.OwnerId, requestId, toolCallId);

    private static string CreateOperationId(
        ExecutionOwnerIdentity executionOwner,
        string requestId,
        string toolCallId) =>
        "tool:v1:operation:" + HashLengthPrefixed(
            OwnerKindValue(executionOwner), executionOwner.OwnerId, requestId, toolCallId);

    private static string CreateWaitingApprovalAuditId(
        ExecutionOwnerIdentity executionOwner,
        string requestId,
        string toolCallId,
        string approvalRequestId) =>
        "tool:v1:waiting-approval:" + HashLengthPrefixed(
            OwnerKindValue(executionOwner), executionOwner.OwnerId, requestId, toolCallId, approvalRequestId);

    private static string CreateRunningAuditId(
        ExecutionOwnerIdentity executionOwner,
        string requestId,
        string toolCallId) =>
        "tool:v1:running:" + HashLengthPrefixed(
            OwnerKindValue(executionOwner), executionOwner.OwnerId, requestId, toolCallId);

    private static string CreateTerminalAuditId(
        ExecutionOwnerIdentity executionOwner,
        string requestId,
        string toolCallId) =>
        "tool:v1:terminal:" + HashLengthPrefixed(
            OwnerKindValue(executionOwner), executionOwner.OwnerId, requestId, toolCallId);

    private static string HashLengthPrefixed(params string[] values)
    {
        using var stream = new MemoryStream();
        Span<byte> length = stackalloc byte[sizeof(uint)];
        foreach (var value in values)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)bytes.Length));
            stream.Write(length);
            stream.Write(bytes);
        }

        return Convert.ToHexStringLower(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
    }

    private static string BuildFailureJson(
        string code,
        string message,
        string toolName,
        string? diagnosticId = null) =>
        diagnosticId is null
            ? JsonSerializer.Serialize(new { error = code, code, message, tool_name = toolName })
            : JsonSerializer.Serialize(new
            {
                error = code,
                code,
                message,
                diagnostic_id = diagnosticId,
                tool_name = toolName,
            });

    private static string SafeExceptionClass(Exception ex) => ex.GetType().Name;

    private void LogCodexExecutionFailure(
        Exception exception,
        AgentWorkflowRuntimeContext workflowRuntime)
    {
        if (exception is not CodexExecutionException codexException)
            return;

        var runId = NormalizeIdentity(workflowRuntime.RootRunId) ??
                    NormalizeIdentity(workflowRuntime.ParentRunId);
        var runHash = runId is null
            ? "none"
            : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(runId)))[..12];
        _logger.LogWarning(
            "Codex execution failed inside admitted tool execution. " +
            "failureKind={CodexFailureKind} failureCode={CodexFailureCode} runHash={WorkflowRunHash}",
            codexException.Failure.Kind,
            SafeDiagnosticCode(codexException.Failure.Code),
            runHash);
    }

    private static string SafeDiagnosticCode(string? value)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized) || normalized.Length > 96 ||
            !char.IsAsciiLetterLower(normalized[0]) ||
            normalized.Any(character =>
                !char.IsAsciiLetterLower(character) &&
                !char.IsAsciiDigit(character) &&
                character != '_'))
        {
            return "unclassified";
        }

        return normalized;
    }

    private static bool IsAuditRecorded(AuditTrailAppendResult append) =>
        append.Status is AuditTrailAppendStatus.Appended or AuditTrailAppendStatus.Duplicate;

    private static string ResolveExceptionErrorCode(Exception exception)
    {
        if (exception is not CodexExecutionException codexException)
            return "tool_execution_exception";

        var ownedExecutionCode = ToolExecutionAuditErrorCode.Resolve(codexException.Failure.Code);
        if (ownedExecutionCode is not null)
            return ownedExecutionCode;

        return codexException.Failure.Kind switch
        {
            CodexExecutionFailureKind.TargetNotConfigured => "codex_execution_target_not_configured",
            CodexExecutionFailureKind.AdmissionDenied => "codex_execution_admission_denied",
            CodexExecutionFailureKind.LlmProviderNotConnected => "codex_execution_llm_provider_not_connected",
            CodexExecutionFailureKind.CapacityUnavailable => "codex_execution_capacity_unavailable",
            CodexExecutionFailureKind.ProvisioningFailed => "codex_execution_provisioning_failed",
            CodexExecutionFailureKind.ReadinessFailed => "codex_execution_readiness_failed",
            CodexExecutionFailureKind.IsolationUnavailable => "codex_execution_isolation_unavailable",
            CodexExecutionFailureKind.MalformedOutput => "codex_execution_malformed_output",
            CodexExecutionFailureKind.TerminalFailure => "codex_execution_terminal_failure",
            CodexExecutionFailureKind.TimedOut => "codex_execution_timed_out",
            CodexExecutionFailureKind.Cancelled => "codex_execution_cancelled",
            CodexExecutionFailureKind.CleanupFailed => "codex_execution_cleanup_failed",
            _ => "tool_execution_exception",
        };
    }

    private static (string Code, string Message, string? DiagnosticId) ResolveExceptionFailureEvidence(
        Exception exception)
    {
        var code = ResolveExceptionErrorCode(exception);
        if (exception is not CodexExecutionException codexException)
            return (code, SafeExceptionClass(exception), null);

        var ownedCode = ToolExecutionAuditErrorCode.Resolve(codexException.Failure.Code);
        var message = ownedCode is null
            ? CanonicalCodexFailureMessage(codexException.Failure.Kind)
            : SafeCodexFailureMessage(codexException.Failure.Message, code);
        return (
            code,
            message,
            SafeDiagnosticId(codexException.Failure.DiagnosticId));
    }

    private static string SafeCodexFailureMessage(string? value, string fallback)
    {
        var normalized = value?.Trim();
        return string.IsNullOrEmpty(normalized) ||
               normalized.Length > 512 ||
               normalized.Any(static character => char.IsControl(character))
            ? fallback
            : normalized;
    }

    private static string? SafeDiagnosticId(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrEmpty(normalized) ||
               normalized.Length > 128 ||
               normalized.Any(static character =>
                   !char.IsAsciiLetterOrDigit(character) &&
                   character is not ('_' or '-' or '.' or ':'))
            ? null
            : normalized;
    }

    private static string CanonicalCodexFailureMessage(CodexExecutionFailureKind kind) =>
        kind switch
        {
            CodexExecutionFailureKind.TargetNotConfigured => "Codex execution target is not configured.",
            CodexExecutionFailureKind.AdmissionDenied => "Codex execution was not admitted.",
            CodexExecutionFailureKind.LlmProviderNotConnected => "Codex LLM provider is not connected.",
            CodexExecutionFailureKind.CapacityUnavailable => "Codex execution capacity is unavailable.",
            CodexExecutionFailureKind.ProvisioningFailed => "Codex execution provisioning failed.",
            CodexExecutionFailureKind.ReadinessFailed => "Codex execution target is not ready.",
            CodexExecutionFailureKind.IsolationUnavailable => "Codex execution isolation is unavailable.",
            CodexExecutionFailureKind.MalformedOutput => "Codex execution returned malformed output.",
            CodexExecutionFailureKind.TerminalFailure => "Codex execution failed.",
            CodexExecutionFailureKind.TimedOut => "Codex execution timed out.",
            CodexExecutionFailureKind.Cancelled => "Codex execution was cancelled.",
            CodexExecutionFailureKind.CleanupFailed => "Codex execution cleanup failed.",
            _ => "Codex execution failed.",
        };

    private static string? NormalizeIdentity(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static ExecutionOwnerIdentity? NormalizeExecutionOwner(AgentToolExecutionOwner? owner)
    {
        var ownerId = NormalizeIdentity(owner?.OwnerId);
        return owner is null || owner.Kind == AgentToolExecutionOwnerKind.Unspecified || ownerId is null
            ? null
            : new ExecutionOwnerIdentity(owner.Kind, ownerId);
    }

    private static bool MatchesExecutionOwner(
        AgentToolExecutionOwner? candidate,
        ExecutionOwnerIdentity expected) =>
        NormalizeExecutionOwner(candidate) is { } normalized && normalized == expected;

    private static AgentToolExecutionOwner ToProto(ExecutionOwnerIdentity owner) =>
        new()
        {
            Kind = owner.Kind,
            OwnerId = owner.OwnerId,
        };

    private static string OwnerKindValue(ExecutionOwnerIdentity owner) =>
        ((int)owner.Kind).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private sealed record CredentialDecision(
        bool Allowed,
        AgentToolExecutionContext ExecutionContext,
        AgentToolCredentialSource CredentialSource,
        string Message);

    private sealed record ExecutionOwnerIdentity(
        AgentToolExecutionOwnerKind Kind,
        string OwnerId);

    private sealed record DuplicateRecoveryResolution(
        AgentToolTerminalOutcome? CompletedOutcome,
        AgentToolPendingOperation? PendingOperation,
        AgentToolExecutionOutcome? Failure);

    private sealed record ReplayPolicyFailure(
        string Code,
        string SafeMessage);
}
