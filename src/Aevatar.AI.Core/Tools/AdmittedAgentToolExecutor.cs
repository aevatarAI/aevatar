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

namespace Aevatar.AI.Core.Tools;

public sealed class AdmittedAgentToolExecutor : IAgentToolExecutionPort
{
    private readonly IAgentToolAdmissionLedger _admissionLedger;
    private readonly IAuditTrailAppender _auditTrailAppender;
    private readonly ToolAuditRecordFactory _auditRecordFactory;

    public AdmittedAgentToolExecutor(
        IAgentToolAdmissionLedger admissionLedger,
        IAuditTrailAppender auditTrailAppender,
        IAuditActorIdentityHasher identityHasher,
        TimeProvider? timeProvider = null)
    {
        _admissionLedger = admissionLedger ?? throw new ArgumentNullException(nameof(admissionLedger));
        _auditTrailAppender = auditTrailAppender ?? throw new ArgumentNullException(nameof(auditTrailAppender));
        _auditRecordFactory = new ToolAuditRecordFactory(
            identityHasher ?? throw new ArgumentNullException(nameof(identityHasher)),
            timeProvider);
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
        AgentToolTerminalOutcome? reconciledOutcome = null;
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
                ct).ConfigureAwait(false);
            if (recovery.Failure is not null)
                return recovery.Failure;
            reconciledOutcome = recovery.CompletedOutcome;
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
            ct).ConfigureAwait(false);
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
            reconciledOutcome,
            ct).ConfigureAwait(false);
    }

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
        AgentToolTerminalOutcome? reconciledOutcome,
        CancellationToken ct)
    {
        using var activity = GenAIActivitySource.StartExecuteTool(toolName, toolCallId);
        var startedAt = Stopwatch.GetTimestamp();
        AgentToolExecutionOutcome outcome;
        try
        {
            AgentToolTerminalOutcome terminalOutcome;
            if (reconciledOutcome is null)
            {
                using var contextScope = AgentToolContextScope.Push(credentialDecision.ExecutionContext);
                terminalOutcome = await tool.ExecuteWithOutcomeAsync(
                    toolCallId,
                    toolName,
                    argumentsJson,
                    ct).ConfigureAwait(false);
            }
            else
            {
                terminalOutcome = reconciledOutcome;
            }
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
            outcome = CreateFailure(
                tool,
                toolName,
                toolCallId,
                callSafety,
                isMutation,
                ResolveExceptionErrorCode(ex),
                SafeExceptionClass(ex),
                AgentToolExecutionFailureStage.TerminalExecution,
                terminalInvoked: reconciledOutcome is null,
                retryable: false,
                auditCompleted: false);
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
            ct).ConfigureAwait(false);
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
        CancellationToken ct)
    {
        if (replayPolicy is AgentToolReplayPolicy.ReadOnlyRetryable or
            AgentToolReplayPolicy.IdempotentRetryable)
        {
            return new DuplicateRecoveryResolution(null, null);
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
                        executionContext),
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

            if (reconciliation?.Disposition == AgentToolOperationReconciliationDisposition.NotFound)
                return new DuplicateRecoveryResolution(null, null);
            if (reconciliation?.Disposition == AgentToolOperationReconciliationDisposition.Completed &&
                reconciliation.CompletedOutcome is not null)
            {
                return new DuplicateRecoveryResolution(reconciliation.CompletedOutcome, null);
            }
        }

        const string failureCode = "outcome_uncertain";
        const string safeMessage =
            "OUTCOME_UNCERTAIN: the prior external effect cannot be proven complete or safe to replay.";
        return new DuplicateRecoveryResolution(
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
                auditCompleted: false));
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
            tool is not IAgentToolOperationReconciler)
        {
            return new ReplayPolicyFailure(
                "missing_tool_operation_reconciler",
                "RECONCILABLE requires a tool-owned operation reconciler.");
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
        CancellationToken ct)
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
                isMutation);
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
        if (context.Credentials.NyxIdCredentialKind == AgentToolNyxIdCredentialKind.ProxyDelegation)
        {
            var proxyDelegationToken = NormalizeIdentity(context.Credentials.NyxIdAccessToken);
            if (proxyDelegationToken is null)
            {
                return new CredentialDecision(
                    false,
                    context,
                    ResolveCredentialSource(context),
                    $"Tool '{toolName}' was not executed because the typed NyxID proxy delegation credential has no valid primary token. Credential fallback was not used.");
            }

            var delegationContext = context with
            {
                Credentials = context.Credentials with
                {
                    NyxIdAccessToken = proxyDelegationToken,
                    NyxIdOrgToken = null,
                    SenderNyxIdAccessToken = null,
                    SourceReadableNyxIdAccessToken =
                        NormalizeIdentity(context.Credentials.SourceReadableNyxIdAccessToken),
                },
            };
            return new CredentialDecision(
                true,
                delegationContext,
                ResolveCredentialSource(delegationContext),
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
            $"Tool '{toolName}' was not executed because sender binding '{senderBindingId}' has no valid NyxID credential.");
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
        bool auditCompleted)
    {
        var resultJson = BuildFailureJson(failureCode, safeMessage, toolName);
        var receipt = AgentToolReceiptFactory.CreateError(
            tool,
            toolCallId,
            toolName,
            callSafety,
            resultJson,
            failureCode,
            safeMessage);
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

    private static string BuildFailureJson(string code, string message, string toolName) =>
        JsonSerializer.Serialize(new { error = code, code, message, tool_name = toolName });

    private static string SafeExceptionClass(Exception ex) => ex.GetType().Name;

    private static bool IsAuditRecorded(AuditTrailAppendResult append) =>
        append.Status is AuditTrailAppendStatus.Appended or AuditTrailAppendStatus.Duplicate;

    private static string ResolveExceptionErrorCode(Exception exception) =>
        exception is CodexExecutionException codexException
            ? codexException.Failure.Kind switch
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
            }
            : "tool_execution_exception";

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
        AgentToolExecutionOutcome? Failure);

    private sealed record ReplayPolicyFailure(
        string Code,
        string SafeMessage);
}
