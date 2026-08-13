using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Modules;
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
            var tools = await source.DiscoverToolsAsync(ct).ConfigureAwait(false);
            foreach (var tool in tools)
                workflowTools.Add(new AgentWorkflowToolAdapter(tool, _toolExecutionPort, _logger));
        }

        return workflowTools;
    }

    private sealed class AgentWorkflowToolAdapter(
        IAgentTool tool,
        IAgentToolExecutionPort toolExecutionPort,
        ILogger logger) : IWorkflowTool
    {
        private readonly IAgentTool _tool = tool ?? throw new ArgumentNullException(nameof(tool));
        private readonly IAgentToolExecutionPort _toolExecutionPort =
            toolExecutionPort ?? throw new ArgumentNullException(nameof(toolExecutionPort));
        private readonly ILogger _logger = logger ?? NullLogger.Instance;

        public string Name => _tool.Name;

        public async Task<WorkflowToolExecutionResult> ExecuteAsync(WorkflowToolExecutionRequest request, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            var workflowRuntimeContext = new AgentWorkflowRuntimeContext(
                Normalize(request.RuntimeContext.ParentActorId),
                Normalize(request.RuntimeContext.ParentRunId),
                Normalize(request.RuntimeContext.ParentStepId),
                Normalize(request.RuntimeContext.RootRunId),
                Math.Max(0, request.RuntimeContext.Depth));
            var credentialContext = WorkflowCallerCredentialToolContextMapper.FromCredential(
                request.CallerCredential,
                workflowRuntimeContext);
            var toolContext = WorkflowRunScopeToolContextMapper.Apply(request.ScopeId, credentialContext with
            {
                Request = credentialContext.Request with
                {
                    RequestId = Normalize(request.RunId),
                    CallId = Normalize(request.CallId),
                    IdempotencyKey = Normalize(request.IdempotencyKey),
                    IssuedAtUnixMs = request.IssuedAtUnixMs,
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
            _logger.LogInformation(
                "Workflow tool credential context prepared. toolName={ToolName} scopeId={ScopeId} rootRunId={RootRunId} parentRunId={ParentRunId} parentStepId={ParentStepId} hasCallerCredentialBearer={HasCallerCredentialBearer} hasNyxIdAccessToken={HasNyxIdAccessToken} hasNyxIdOrgToken={HasNyxIdOrgToken}",
                _tool.Name,
                request.ScopeId ?? string.Empty,
                request.RuntimeContext.RootRunId ?? string.Empty,
                request.RuntimeContext.ParentRunId ?? string.Empty,
                request.RuntimeContext.ParentStepId ?? string.Empty,
                !string.IsNullOrWhiteSpace(request.CallerCredential?.BearerToken),
                !string.IsNullOrWhiteSpace(toolContext.Credentials.NyxIdAccessToken),
                !string.IsNullOrWhiteSpace(toolContext.Credentials.NyxIdOrgToken));
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
                UnattendedAuthorization: MapUnattendedAuthorization(
                    request,
                    toolContext,
                    request.ArgumentsJson,
                    _tool.Name));
            var outcome = await _toolExecutionPort.ExecuteAsync(executionRequest, ct).ConfigureAwait(false);
            if (IsActorRedeliveryAdmission(outcome))
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
                    outcome.Retryable);
            }

            return AgentWorkflowToolReceiptOutcomeMapper.Map(outcome.Receipt, outcome.ResultJson);
        }

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
                ResolveFailureMessage(receipt));
        }

        return WorkflowToolExecutionResult.Success(
            resultJson,
            ToWorkflowManagedHandoffOutcome(receipt.ManagedWorkflowHandoff));
    }

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
