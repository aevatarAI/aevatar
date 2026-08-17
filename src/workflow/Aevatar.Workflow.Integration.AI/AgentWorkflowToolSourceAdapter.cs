using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.CodeExecution;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Modules;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Integration.AI;

public sealed class AgentWorkflowToolSourceAdapter(
    IEnumerable<IAgentToolSource> agentToolSources,
    IAgentToolExecutionPort toolExecutionPort,
    ILogger<AgentWorkflowToolSourceAdapter>? logger = null) : IWorkflowToolSource
{
    private readonly IEnumerable<IAgentToolSource> _agentToolSources =
        agentToolSources ?? throw new ArgumentNullException(nameof(agentToolSources));
    private readonly IAgentToolExecutionPort _toolExecutionPort =
        toolExecutionPort ?? throw new ArgumentNullException(nameof(toolExecutionPort));
    private readonly ILogger<AgentWorkflowToolSourceAdapter> _logger =
        logger ?? NullLogger<AgentWorkflowToolSourceAdapter>.Instance;

    public async Task<IReadOnlyList<IWorkflowTool>> GetToolsAsync(CancellationToken ct = default)
    {
        var workflowTools = new List<IWorkflowTool>();
        foreach (var source in _agentToolSources)
        {
            IReadOnlyList<IAgentTool> tools;
            try
            {
                tools = await source.DiscoverToolsAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Agent tool source discovery failed: {Source}",
                    source.GetType().Name);
                continue;
            }

            foreach (var tool in tools)
                workflowTools.Add(new AgentWorkflowToolAdapter(tool, _toolExecutionPort, _logger));
        }

        return workflowTools;
    }

    private sealed class AgentWorkflowToolAdapter(
        IAgentTool tool,
        IAgentToolExecutionPort toolExecutionPort,
        ILogger logger) : IWorkflowDurableOperationTool
    {
        private readonly IAgentTool _tool = tool ?? throw new ArgumentNullException(nameof(tool));
        private readonly IAgentToolExecutionPort _toolExecutionPort =
            toolExecutionPort ?? throw new ArgumentNullException(nameof(toolExecutionPort));
        private readonly ILogger _logger = logger ?? NullLogger.Instance;

        public string Name => _tool.Name;

        public WorkflowToolRecoverySafety RecoverySafety =>
            WorkflowToolRecoverySafety.DurableStartOnceRedispatch;

        public Task<WorkflowToolExecutionResult> ExecuteAsync(
            WorkflowToolExecutionRequest request,
            CancellationToken ct = default) =>
            ExecuteCoreAsync(
                request,
                pendingOperation: null,
                AgentToolExecutionAttemptKind.Initial,
                recoverActorRedelivery: true,
                ct);

        public Task<WorkflowToolExecutionResult> ReconcileAsync(
            WorkflowToolExecutionRequest request,
            WorkflowToolPendingOperation pendingOperation,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(pendingOperation);
            return ExecuteCoreAsync(
                request,
                pendingOperation,
                AgentToolExecutionAttemptKind.ActorRecovery,
                recoverActorRedelivery: false,
                ct);
        }

        public async Task<WorkflowToolCancellationResult> CancelAsync(
            WorkflowToolCancellationRequest request,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(request.ExecutionRequest);
            ArgumentNullException.ThrowIfNull(request.PendingOperation);
            if (request.Reason != WorkflowToolOperationCancellationReason.WorkflowStopped)
            {
                return WorkflowToolCancellationResult.Failed(
                    "invalid_workflow_tool_cancellation_reason",
                    "Durable workflow tool cancellation requires an explicit workflow stop reason.",
                    retryable: true);
            }

            var executionRequest = request.ExecutionRequest;
            var toolContext = BuildToolContext(executionRequest, request.PendingOperation);
            LogCredentialContext(executionRequest, toolContext);
            var agentPending = ToAgentPendingOperation(request.PendingOperation)!;
            var cancellation = await _toolExecutionPort.CancelAsync(
                new AgentToolCancellationRequest(
                    _tool,
                    executionRequest.ArgumentsJson,
                    toolContext,
                    AgentToolApprovalContinuationMode.ActorOwned,
                    AgentToolExecutionAttemptKind.ActorRecovery,
                    agentPending,
                    AgentToolOperationCancellationReason.WorkflowStopped,
                    request.DeadlineUnixMs,
                    ToAgentCancellationTerminalIntent(request.TerminalIntent),
                    MapUnattendedAuthorization(
                        executionRequest,
                        toolContext,
                        executionRequest.ArgumentsJson,
                        _tool.Name)),
                ct).ConfigureAwait(false);

            if (cancellation.Disposition == AgentToolCancellationDisposition.Pending)
            {
                if (cancellation.PendingOperation is not { } pending)
                {
                    return WorkflowToolCancellationResult.Failed(
                        "tool_cancellation_pending_operation_invalid",
                        "The admitted tool cancellation returned an invalid pending operation.",
                        retryable: true);
                }

                return WorkflowToolCancellationResult.Pending(
                    ToWorkflowPendingOperation(pending),
                    string.IsNullOrWhiteSpace(cancellation.FailureCode)
                        ? null
                        : new WorkflowToolExecutionFailure(
                            cancellation.FailureCode,
                            string.IsNullOrWhiteSpace(cancellation.SafeMessage)
                                ? cancellation.FailureCode
                                : cancellation.SafeMessage,
                            TerminalInvoked: true,
                            Retryable: cancellation.Retryable),
                    cancellation.PendingTerminalIntent is null
                        ? null
                        : ToWorkflowCancellationTerminalIntent(cancellation.PendingTerminalIntent));
            }

            if (cancellation.Disposition == AgentToolCancellationDisposition.Failed)
            {
                return WorkflowToolCancellationResult.Failed(
                    cancellation.FailureCode,
                    string.IsNullOrWhiteSpace(cancellation.SafeMessage)
                        ? cancellation.FailureCode
                        : cancellation.SafeMessage,
                    cancellation.Retryable);
            }

            if (cancellation.Disposition != AgentToolCancellationDisposition.Completed ||
                cancellation.CompletedOutcome is not { AuditCompleted: true } completed)
            {
                return WorkflowToolCancellationResult.Pending(
                    request.PendingOperation,
                    new WorkflowToolExecutionFailure(
                        "tool_cancellation_terminal_audit_incomplete",
                        "The tool cancellation terminal audit is not durably recorded.",
                        TerminalInvoked: true,
                        Retryable: true),
                    cancellation.CompletedOutcome is null
                        ? request.TerminalIntent
                        : ToWorkflowCancellationTerminalIntent(
                            cancellation.CompletedOutcome,
                            AgentToolArgumentsDigest.ComputeSha256(executionRequest.ArgumentsJson)));
            }

            return WorkflowToolCancellationResult.Completed(MapExecutionOutcome(completed));
        }

        private async Task<WorkflowToolExecutionResult> ExecuteCoreAsync(
            WorkflowToolExecutionRequest request,
            WorkflowToolPendingOperation? pendingOperation,
            AgentToolExecutionAttemptKind executionAttemptKind,
            bool recoverActorRedelivery,
            CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(request);

            var toolContext = BuildToolContext(request, pendingOperation);
            LogCredentialContext(request, toolContext);
            var executionRequest = new AgentToolExecutionRequest(
                _tool,
                request.ArgumentsJson,
                toolContext,
                AgentToolApprovalContinuationMode.ActorOwned,
                request.ApprovalGrant == null
                    ? null
                    : new AgentToolApprovalGrant(
                        toolContext.ExecutionOwner.Clone(),
                        request.ApprovalGrant.ApprovalRequestId,
                        request.RunId,
                        request.ApprovalGrant.ToolName,
                        request.ApprovalGrant.ToolCallId,
                        AgentToolArgumentsDigest.ComputeSha256(request.ArgumentsJson)),
                ExecutionAttemptKind: executionAttemptKind,
                UnattendedAuthorization: MapUnattendedAuthorization(
                    request,
                    toolContext,
                    request.ArgumentsJson,
                    _tool.Name),
                PendingOperation: ToAgentPendingOperation(pendingOperation));
            var outcome = await _toolExecutionPort.ExecuteAsync(executionRequest, ct).ConfigureAwait(false);
            if (recoverActorRedelivery && IsActorRedeliveryAdmission(outcome))
            {
                outcome = await _toolExecutionPort.ExecuteAsync(
                    executionRequest with
                    {
                        ExecutionAttemptKind = AgentToolExecutionAttemptKind.ActorRecovery,
                    },
                    ct).ConfigureAwait(false);
            }

            if (outcome.Kind == AgentToolExecutionOutcomeKind.ApprovalRequired)
            {
                return new WorkflowToolExecutionResult(
                    string.Empty,
                    PendingApproval: new WorkflowToolApprovalPendingOutcome(
                        outcome.Receipt.ApprovalRequestId,
                        outcome.Receipt.ToolName,
                        outcome.Receipt.CallId,
                        request.ArgumentsJson,
                        outcome.Receipt.ApprovalMode.ToString(),
                        IsReadOnly: !outcome.IsMutation,
                        outcome.Receipt.IsDestructive));
            }

            if (outcome.Kind == AgentToolExecutionOutcomeKind.Pending)
            {
                if (outcome.PendingOperation is not { } pending)
                {
                    return WorkflowToolExecutionResult.Failed(
                        string.Empty,
                        "tool_pending_operation_invalid",
                        "The tool returned an invalid durable pending operation.");
                }

                return new WorkflowToolExecutionResult(
                    string.Empty,
                    PendingOperation: ToWorkflowPendingOperation(pending),
                    CancellationRecoveryIntent: outcome.CancellationRecoveryIntent is null
                        ? null
                        : ToWorkflowCancellationTerminalIntent(outcome.CancellationRecoveryIntent));
            }

            if (outcome.Kind is not (AgentToolExecutionOutcomeKind.Executed or
                AgentToolExecutionOutcomeKind.ExecutedAuditIncomplete))
            {
                return WorkflowToolExecutionResult.Failed(
                    outcome.ResultJson,
                    outcome.FailureCode,
                    string.IsNullOrWhiteSpace(outcome.SafeMessage)
                        ? outcome.FailureCode
                        : outcome.SafeMessage,
                    outcome.TerminalInvoked,
                    outcome.Retryable,
                    AgentWorkflowToolReceiptOutcomeMapper.MapFailureOutcome(
                        outcome.Receipt.FailureOutcome));
            }

            return AgentWorkflowToolReceiptOutcomeMapper.Map(outcome.Receipt, outcome.ResultJson);
        }

        private AgentToolExecutionContext BuildToolContext(
            WorkflowToolExecutionRequest request,
            WorkflowToolPendingOperation? pendingOperation)
        {
            var workflowRuntimeContext = new AgentWorkflowRuntimeContext(
                Normalize(request.RuntimeContext.ParentActorId),
                Normalize(request.RuntimeContext.ParentRunId),
                Normalize(request.RuntimeContext.ParentStepId),
                Normalize(request.RuntimeContext.RootRunId),
                Math.Max(0, request.RuntimeContext.Depth));
            var credentialContext = WorkflowCallerCredentialToolContextMapper.FromCredential(
                request.CallerCredential,
                workflowRuntimeContext);
            return WorkflowRunScopeToolContextMapper.Apply(request.ScopeId, credentialContext with
            {
                Request = credentialContext.Request with
                {
                    RequestId = Normalize(request.RunId),
                    CallId = Normalize(request.CallId),
                    IdempotencyKey = Normalize(request.IdempotencyKey),
                    IssuedAtUnixMs = request.IssuedAtUnixMs,
                    OperationId = Normalize(pendingOperation?.OperationId),
                },
                Schedule = new AgentToolScheduleContext(Normalize(request.ScheduleId)),
                OperationAdmission = WorkflowOperationAdmissionToolContextMapper.Map(
                    request.InvocationAdmission),
                InvocationSurface = AgentToolInvocationSurface.WorkflowToolCall,
                Chat = new AgentChatInvocationContext(
                    AgentChatInvocationSurface.WorkflowChat,
                    Normalize(request.RunId),
                    null,
                    null,
                    Normalize(request.StepId),
                    null),
                InputFileRefs = request.InputFileRefs.Select(ToChatFileRef).ToArray(),
                ExecutionOwner = AgentToolExecutionOwners.WorkflowRun(request.RunId),
            });
        }

        private void LogCredentialContext(
            WorkflowToolExecutionRequest request,
            AgentToolExecutionContext toolContext) =>
            _logger.LogInformation(
                "Workflow tool credential context prepared. toolName={ToolName} hasCallerCredentialBearer={HasCallerCredentialBearer} hasNyxIdAccessToken={HasNyxIdAccessToken} hasNyxIdOrgToken={HasNyxIdOrgToken}",
                _tool.Name,
                !string.IsNullOrWhiteSpace(request.CallerCredential?.BearerToken),
                !string.IsNullOrWhiteSpace(toolContext.Credentials.NyxIdAccessToken),
                !string.IsNullOrWhiteSpace(toolContext.Credentials.NyxIdOrgToken));

        private static WorkflowToolExecutionResult MapExecutionOutcome(AgentToolExecutionOutcome outcome)
        {
            if (outcome.Kind == AgentToolExecutionOutcomeKind.Executed)
            {
                var mapped = AgentWorkflowToolReceiptOutcomeMapper.Map(outcome.Receipt, outcome.ResultJson);
                return mapped.Failure is null
                    ? mapped
                    : mapped with
                    {
                        Failure = mapped.Failure with
                        {
                            TerminalInvoked = outcome.TerminalInvoked,
                            Retryable = outcome.Retryable,
                        },
                    };
            }

            return WorkflowToolExecutionResult.Failed(
                outcome.ResultJson,
                outcome.FailureCode,
                string.IsNullOrWhiteSpace(outcome.SafeMessage)
                    ? outcome.FailureCode
                    : outcome.SafeMessage,
                outcome.TerminalInvoked,
                outcome.Retryable,
                AgentWorkflowToolReceiptOutcomeMapper.MapFailureOutcome(
                    outcome.Receipt.FailureOutcome));
        }

        private static AgentToolCancellationTerminalIntent? ToAgentCancellationTerminalIntent(
            WorkflowToolCancellationTerminalAuditIntent? intent)
        {
            if (intent is null)
                return null;

            var packed = intent.ToolOwnedAuditIntent;
            if (packed?.Is(AgentToolCancellationAuditIntentPayload.Descriptor) != true)
            {
                throw new InvalidOperationException(
                    "The workflow cancellation terminal audit intent is not owned by the agent adapter.");
            }

            var payload = packed.Unpack<AgentToolCancellationAuditIntentPayload>();
            var mapped = FromCancellationAuditIntentPayload(payload) with
            {
                ArgumentsSha256 = intent.ArgumentsSha256,
            };
            if (!MatchesWorkflowTerminalResult(intent.Result, MapExecutionOutcome(ToExecutionOutcome(mapped))))
            {
                throw new InvalidOperationException(
                    "The workflow cancellation terminal result does not match its tool-owned audit intent.");
            }

            return mapped;
        }

        private static WorkflowToolCancellationTerminalAuditIntent ToWorkflowCancellationTerminalIntent(
            AgentToolCancellationTerminalIntent intent) =>
            new(
                MapExecutionOutcome(ToExecutionOutcome(intent)),
                Any.Pack(ToCancellationAuditIntentPayload(intent)),
                intent.ArgumentsSha256);

        private static WorkflowToolCancellationTerminalAuditIntent ToWorkflowCancellationTerminalIntent(
            AgentToolExecutionOutcome outcome,
            string argumentsSha256) =>
            ToWorkflowCancellationTerminalIntent(ToCancellationTerminalIntent(outcome, argumentsSha256));

        private static AgentToolExecutionOutcome ToExecutionOutcome(
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

        private static AgentToolCancellationTerminalIntent ToCancellationTerminalIntent(
            AgentToolExecutionOutcome outcome,
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
                new AgentToolCallSafety(
                    outcome.Receipt.ApprovalMode == AgentToolReceiptApprovalMode.AlwaysRequire
                        ? true
                        : null,
                    outcome.Receipt.Effect == AgentToolReceiptEffect.ReadOnly,
                    outcome.Receipt.IsDestructive),
                argumentsSha256);

        private static AgentToolCancellationAuditIntentPayload ToCancellationAuditIntentPayload(
            AgentToolCancellationTerminalIntent intent)
        {
            var payload = new AgentToolCancellationAuditIntentPayload
            {
                ResultJson = intent.ResultJson ?? string.Empty,
                Receipt = intent.Receipt.Clone(),
                IsMutation = intent.IsMutation,
                FailureCode = intent.FailureCode ?? string.Empty,
                SafeMessage = intent.SafeMessage ?? string.Empty,
                TerminalInvoked = intent.TerminalInvoked,
                Retryable = intent.Retryable,
                IsReadOnly = intent.CallSafety.IsReadOnly,
                IsDestructive = intent.CallSafety.IsDestructive,
                OutcomeKind = ToCancellationAuditOutcomeKind(intent.Kind),
                FailureStage = ToCancellationAuditFailureStage(intent.FailureStage),
            };
            if (intent.CallSafety.RequiresApproval.HasValue)
                payload.RequiresApproval = intent.CallSafety.RequiresApproval.Value;
            return payload;
        }

        private static AgentToolCancellationTerminalIntent FromCancellationAuditIntentPayload(
            AgentToolCancellationAuditIntentPayload payload)
        {
            if (payload.Receipt == null)
                throw new InvalidOperationException("The cancellation audit intent receipt is required.");

            return new AgentToolCancellationTerminalIntent(
                FromCancellationAuditOutcomeKind(payload.OutcomeKind),
                payload.ResultJson ?? string.Empty,
                payload.Receipt.Clone(),
                payload.IsMutation,
                payload.FailureCode ?? string.Empty,
                payload.SafeMessage ?? string.Empty,
                FromCancellationAuditFailureStage(payload.FailureStage),
                payload.TerminalInvoked,
                payload.Retryable,
                new AgentToolCallSafety(
                    payload.HasRequiresApproval ? payload.RequiresApproval : null,
                    payload.IsReadOnly,
                    payload.IsDestructive),
                string.Empty);
        }

        private static bool MatchesWorkflowTerminalResult(
            WorkflowToolExecutionResult left,
            WorkflowToolExecutionResult right) =>
            string.Equals(left.ResultJson, right.ResultJson, StringComparison.Ordinal) &&
            left.ManagedHandoff == null && right.ManagedHandoff == null &&
            left.PendingApproval == null && right.PendingApproval == null &&
            left.PendingOperation == null && right.PendingOperation == null &&
            ((left.Failure == null && right.Failure == null) ||
             (left.Failure != null && right.Failure != null &&
              string.Equals(left.Failure.ErrorCode, right.Failure.ErrorCode, StringComparison.Ordinal) &&
              string.Equals(left.Failure.ErrorMessage, right.Failure.ErrorMessage, StringComparison.Ordinal) &&
              left.Failure.TerminalInvoked == right.Failure.TerminalInvoked &&
              left.Failure.Retryable == right.Failure.Retryable &&
              left.Failure.FailureOutcome == right.Failure.FailureOutcome));

        private static AgentToolCancellationAuditOutcomeKind ToCancellationAuditOutcomeKind(
            AgentToolExecutionOutcomeKind kind) => kind switch
            {
                AgentToolExecutionOutcomeKind.Executed => AgentToolCancellationAuditOutcomeKind.Executed,
                AgentToolExecutionOutcomeKind.Failed => AgentToolCancellationAuditOutcomeKind.Failed,
                _ => throw new InvalidOperationException($"Unsupported cancellation audit outcome kind: {kind}."),
            };

        private static AgentToolExecutionOutcomeKind FromCancellationAuditOutcomeKind(
            AgentToolCancellationAuditOutcomeKind kind) => kind switch
            {
                AgentToolCancellationAuditOutcomeKind.Executed => AgentToolExecutionOutcomeKind.Executed,
                AgentToolCancellationAuditOutcomeKind.Failed => AgentToolExecutionOutcomeKind.Failed,
                _ => throw new InvalidOperationException($"Unsupported cancellation audit outcome kind: {kind}."),
            };

        private static AgentToolCancellationAuditFailureStage ToCancellationAuditFailureStage(
            AgentToolExecutionFailureStage stage) => stage switch
            {
                AgentToolExecutionFailureStage.None => AgentToolCancellationAuditFailureStage.None,
                AgentToolExecutionFailureStage.RequestValidation =>
                    AgentToolCancellationAuditFailureStage.RequestValidation,
                AgentToolExecutionFailureStage.Classification => AgentToolCancellationAuditFailureStage.Classification,
                AgentToolExecutionFailureStage.CredentialPolicy =>
                    AgentToolCancellationAuditFailureStage.CredentialPolicy,
                AgentToolExecutionFailureStage.Approval => AgentToolCancellationAuditFailureStage.Approval,
                AgentToolExecutionFailureStage.Admission => AgentToolCancellationAuditFailureStage.Admission,
                AgentToolExecutionFailureStage.TerminalExecution =>
                    AgentToolCancellationAuditFailureStage.TerminalExecution,
                AgentToolExecutionFailureStage.TerminalAudit => AgentToolCancellationAuditFailureStage.TerminalAudit,
                _ => throw new InvalidOperationException($"Unsupported cancellation audit failure stage: {stage}."),
            };

        private static AgentToolExecutionFailureStage FromCancellationAuditFailureStage(
            AgentToolCancellationAuditFailureStage stage) => stage switch
            {
                AgentToolCancellationAuditFailureStage.None => AgentToolExecutionFailureStage.None,
                AgentToolCancellationAuditFailureStage.RequestValidation =>
                    AgentToolExecutionFailureStage.RequestValidation,
                AgentToolCancellationAuditFailureStage.Classification => AgentToolExecutionFailureStage.Classification,
                AgentToolCancellationAuditFailureStage.CredentialPolicy =>
                    AgentToolExecutionFailureStage.CredentialPolicy,
                AgentToolCancellationAuditFailureStage.Approval => AgentToolExecutionFailureStage.Approval,
                AgentToolCancellationAuditFailureStage.Admission => AgentToolExecutionFailureStage.Admission,
                AgentToolCancellationAuditFailureStage.TerminalExecution =>
                    AgentToolExecutionFailureStage.TerminalExecution,
                AgentToolCancellationAuditFailureStage.TerminalAudit => AgentToolExecutionFailureStage.TerminalAudit,
                _ => throw new InvalidOperationException($"Unsupported cancellation audit failure stage: {stage}."),
            };

        private static AgentToolPendingOperation? ToAgentPendingOperation(
            WorkflowToolPendingOperation? pendingOperation) =>
            pendingOperation is null
                ? null
                : new AgentToolPendingOperation(
                    pendingOperation.OperationId,
                    pendingOperation.ProviderOperationId,
                    pendingOperation.StatusPath,
                    pendingOperation.ResultPath,
                    pendingOperation.CancelPath,
                    ToAgentPendingOperationStatus(pendingOperation.Status),
                    pendingOperation.ETag,
                    pendingOperation.RetryAfterMilliseconds,
                    pendingOperation.ExpiresAtUnixMs,
                    pendingOperation.ServiceSlug,
                    pendingOperation.UserServiceId,
                    ToAgentRouteIdentitySource(pendingOperation.RouteIdentitySource));

        private static WorkflowToolPendingOperation ToWorkflowPendingOperation(
            AgentToolPendingOperation pendingOperation) =>
            new(
                pendingOperation.OperationId,
                pendingOperation.ProviderOperationId,
                pendingOperation.StatusPath,
                pendingOperation.ResultPath,
                pendingOperation.CancelPath,
                ToWorkflowPendingOperationStatus(pendingOperation.Status),
                pendingOperation.ETag,
                pendingOperation.RetryAfterMilliseconds,
                pendingOperation.ExpiresAtUnixMs,
                pendingOperation.ServiceSlug,
                pendingOperation.UserServiceId,
                ToWorkflowRouteIdentitySource(pendingOperation.RouteIdentitySource));

        private static AgentToolPendingOperationStatus ToAgentPendingOperationStatus(
            WorkflowToolPendingOperationStatus status) =>
            status switch
            {
                WorkflowToolPendingOperationStatus.Unspecified => AgentToolPendingOperationStatus.Unspecified,
                WorkflowToolPendingOperationStatus.SubmissionUncertain =>
                    AgentToolPendingOperationStatus.SubmissionUncertain,
                WorkflowToolPendingOperationStatus.Queued => AgentToolPendingOperationStatus.Queued,
                WorkflowToolPendingOperationStatus.Provisioning => AgentToolPendingOperationStatus.Provisioning,
                WorkflowToolPendingOperationStatus.Preparing => AgentToolPendingOperationStatus.Preparing,
                WorkflowToolPendingOperationStatus.Running => AgentToolPendingOperationStatus.Running,
                WorkflowToolPendingOperationStatus.Collecting => AgentToolPendingOperationStatus.Collecting,
                WorkflowToolPendingOperationStatus.Succeeded => AgentToolPendingOperationStatus.Succeeded,
                WorkflowToolPendingOperationStatus.Failed => AgentToolPendingOperationStatus.Failed,
                WorkflowToolPendingOperationStatus.Cancelled => AgentToolPendingOperationStatus.Cancelled,
                WorkflowToolPendingOperationStatus.OutcomeUncertain =>
                    AgentToolPendingOperationStatus.OutcomeUncertain,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(status),
                    status,
                    "Unknown workflow pending operation status."),
            };

        private static WorkflowToolPendingOperationStatus ToWorkflowPendingOperationStatus(
            AgentToolPendingOperationStatus status) =>
            status switch
            {
                AgentToolPendingOperationStatus.Unspecified => WorkflowToolPendingOperationStatus.Unspecified,
                AgentToolPendingOperationStatus.SubmissionUncertain =>
                    WorkflowToolPendingOperationStatus.SubmissionUncertain,
                AgentToolPendingOperationStatus.Queued => WorkflowToolPendingOperationStatus.Queued,
                AgentToolPendingOperationStatus.Provisioning => WorkflowToolPendingOperationStatus.Provisioning,
                AgentToolPendingOperationStatus.Preparing => WorkflowToolPendingOperationStatus.Preparing,
                AgentToolPendingOperationStatus.Running => WorkflowToolPendingOperationStatus.Running,
                AgentToolPendingOperationStatus.Collecting => WorkflowToolPendingOperationStatus.Collecting,
                AgentToolPendingOperationStatus.Succeeded => WorkflowToolPendingOperationStatus.Succeeded,
                AgentToolPendingOperationStatus.Failed => WorkflowToolPendingOperationStatus.Failed,
                AgentToolPendingOperationStatus.Cancelled => WorkflowToolPendingOperationStatus.Cancelled,
                AgentToolPendingOperationStatus.OutcomeUncertain =>
                    WorkflowToolPendingOperationStatus.OutcomeUncertain,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(status),
                    status,
                    "Unknown agent pending operation status."),
            };

        private static CodeExecutionRouteIdentitySource ToAgentRouteIdentitySource(
            WorkflowToolPendingOperationRouteIdentitySource source) =>
            source switch
            {
                WorkflowToolPendingOperationRouteIdentitySource.Unspecified =>
                    CodeExecutionRouteIdentitySource.Unspecified,
                WorkflowToolPendingOperationRouteIdentitySource.CodeExecutionContract =>
                    CodeExecutionRouteIdentitySource.CodeExecutionContract,
                WorkflowToolPendingOperationRouteIdentitySource.NyxIdUserServiceCatalog =>
                    CodeExecutionRouteIdentitySource.NyxIdUserServiceCatalog,
                WorkflowToolPendingOperationRouteIdentitySource.WorkflowCapabilityAdmission =>
                    CodeExecutionRouteIdentitySource.WorkflowCapabilityAdmission,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(source),
                    source,
                    "Unknown workflow code execution route source."),
            };

        private static WorkflowToolPendingOperationRouteIdentitySource ToWorkflowRouteIdentitySource(
            CodeExecutionRouteIdentitySource source) =>
            source switch
            {
                CodeExecutionRouteIdentitySource.Unspecified =>
                    WorkflowToolPendingOperationRouteIdentitySource.Unspecified,
                CodeExecutionRouteIdentitySource.CodeExecutionContract =>
                    WorkflowToolPendingOperationRouteIdentitySource.CodeExecutionContract,
                CodeExecutionRouteIdentitySource.NyxIdUserServiceCatalog =>
                    WorkflowToolPendingOperationRouteIdentitySource.NyxIdUserServiceCatalog,
                CodeExecutionRouteIdentitySource.WorkflowCapabilityAdmission =>
                    WorkflowToolPendingOperationRouteIdentitySource.WorkflowCapabilityAdmission,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(source),
                    source,
                    "Unknown agent code execution route source."),
            };

        private static bool IsActorRedeliveryAdmission(AgentToolExecutionOutcome outcome) =>
            outcome.Kind == AgentToolExecutionOutcomeKind.Failed &&
            outcome.FailureStage == AgentToolExecutionFailureStage.Admission &&
            string.Equals(
                outcome.FailureCode,
                "tool_execution_already_started",
                StringComparison.Ordinal) &&
            !outcome.TerminalInvoked &&
            !outcome.Retryable;

        private static AgentToolUnattendedExecutionAuthorization? MapUnattendedAuthorization(
            WorkflowToolExecutionRequest request,
            AgentToolExecutionContext context,
            string argumentsJson,
            string toolName)
        {
            var permit = request.UnattendedInvocationPermit;
            var admission = context.OperationAdmission;
            var explicitGrant = request.InvocationAdmission?.NyxIdExplicitRequestGrant;
            if (permit is null || admission is null ||
                explicitGrant is null ||
                !string.Equals(permit.CallSiteId, request.InvocationAdmission?.CallSiteId, StringComparison.Ordinal) ||
                !string.Equals(
                    permit.CapabilityContractDigest,
                    request.InvocationAdmission?.Capability?.NyxIdUserRequest?.ContractDigest,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    permit.ExplicitRequestGrantDigest,
                    WorkflowCapabilityAdmissionPlanIntegrity.ComputeNyxIdExplicitRequestGrantDigest(explicitGrant),
                    StringComparison.Ordinal))
            {
                return null;
            }

            return new AgentToolUnattendedExecutionAuthorization(
                AgentToolUnattendedAuthorizationKind.WorkflowWebhookExact,
                permit.AuthorizationId,
                context.ExecutionOwner.Clone(),
                request.RunId,
                toolName,
                request.CallId,
                AgentToolArgumentsDigest.ComputeSha256(argumentsJson),
                permit.CallSiteId,
                AgentToolOperationSelector.ComputeDigest(admission));
        }

        private static ChatFileRef ToChatFileRef(WorkflowFileRef fileRef) =>
            new()
            {
                FileId = Normalize(fileRef.FileId) ?? string.Empty,
                ArtifactId = Normalize(fileRef.ArtifactId) ?? string.Empty,
                SourceKind = fileRef.SourceKind switch
                {
                    WorkflowFileSourceKind.ChatInput => ChatFileSourceKind.ChatInput,
                    WorkflowFileSourceKind.FormUpload => ChatFileSourceKind.FormUpload,
                    WorkflowFileSourceKind.ConnectedServiceResource => ChatFileSourceKind.ConnectedServiceResource,
                    WorkflowFileSourceKind.ExternalResource => ChatFileSourceKind.ExternalResource,
                    WorkflowFileSourceKind.Generated => ChatFileSourceKind.Generated,
                    _ => ChatFileSourceKind.Unspecified,
                },
                SourceMessageId = Normalize(fileRef.SourceMessageId) ?? string.Empty,
                SourceResourceKey = Normalize(fileRef.SourceResourceKey) ?? string.Empty,
                FileName = Normalize(fileRef.FileName) ?? string.Empty,
                MediaType = Normalize(fileRef.MediaType) ?? string.Empty,
                SizeBytes = fileRef.SizeBytes,
                Sha256 = Normalize(fileRef.Sha256) ?? string.Empty,
                CreatedAtUnixMs = fileRef.CreatedAtUnixMs,
                ExpiresAtUnixMs = fileRef.ExpiresAtUnixMs,
                OwnerRunId = Normalize(fileRef.OwnerRunId) ?? string.Empty,
                OwnerScopeId = Normalize(fileRef.OwnerScopeId) ?? string.Empty,
            };

        private static string? Normalize(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    }
}

file static class AgentWorkflowToolReceiptOutcomeMapper
{
    private const string UnknownErrorCode = "tool_outcome_unknown";
    private const string UnknownErrorMessage = "The tool outcome could not be verified.";

    public static WorkflowToolExecutionResult Map(AgentToolReceipt receipt, string resultJson)
    {
        if (IsFailure(receipt.Status))
        {
            return WorkflowToolExecutionResult.Failed(
                receipt.ResultJson ?? string.Empty,
                ResolveFailureCode(receipt),
                ResolveFailureMessage(receipt),
                failureOutcome: MapFailureOutcome(receipt.FailureOutcome));
        }

        return WorkflowToolExecutionResult.Success(
            resultJson,
            ToWorkflowManagedHandoffOutcome(receipt.ManagedWorkflowHandoff));
    }

    public static WorkflowStepFailureOutcome MapFailureOutcome(
        AgentToolFailureOutcome failureOutcome) =>
        failureOutcome switch
        {
            AgentToolFailureOutcome.CalleeConfirmed => WorkflowStepFailureOutcome.CalleeConfirmed,
            AgentToolFailureOutcome.OutcomeUncertain => WorkflowStepFailureOutcome.OutcomeUncertain,
            _ => WorkflowStepFailureOutcome.OutcomeUncertain,
        };

    private static bool IsFailure(AgentToolReceiptStatus status) =>
        status is AgentToolReceiptStatus.Error or
            AgentToolReceiptStatus.Denied or
            AgentToolReceiptStatus.AuthorizationRequired or
            AgentToolReceiptStatus.Unspecified;

    private static string ResolveFailureCode(AgentToolReceipt receipt)
    {
        if (!string.IsNullOrWhiteSpace(receipt.ErrorCode))
            return receipt.ErrorCode.Trim();

        return receipt.Status switch
        {
            AgentToolReceiptStatus.Denied => "tool_denied",
            AgentToolReceiptStatus.AuthorizationRequired => "authorization_required",
            AgentToolReceiptStatus.Unspecified => UnknownErrorCode,
            _ => "tool_error",
        };
    }

    private static string ResolveFailureMessage(AgentToolReceipt receipt) =>
        string.IsNullOrWhiteSpace(receipt.ErrorMessage)
            ? receipt.Status == AgentToolReceiptStatus.Unspecified
                ? UnknownErrorMessage
                : ResolveFailureCode(receipt)
            : receipt.ErrorMessage.Trim();

    private static WorkflowManagedHandoffOutcome? ToWorkflowManagedHandoffOutcome(
        ManagedWorkflowHandoffReceipt? receipt)
    {
        if (receipt == null || string.IsNullOrWhiteSpace(receipt.InvocationId))
            return null;

        return new WorkflowManagedHandoffOutcome
        {
            ParentActorId = receipt.ParentActorId ?? string.Empty,
            ParentRunId = receipt.ParentRunId ?? string.Empty,
            ParentStepId = receipt.ParentStepId ?? string.Empty,
            InvocationId = receipt.InvocationId ?? string.Empty,
            ChildRunId = receipt.ChildRunId ?? string.Empty,
            StreamTopic = receipt.StreamTopic ?? string.Empty,
        };
    }
}
