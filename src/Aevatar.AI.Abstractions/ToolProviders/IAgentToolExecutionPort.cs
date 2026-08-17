using Aevatar.AI.Abstractions;

namespace Aevatar.AI.Abstractions.ToolProviders;

public interface IAgentToolExecutionPort
{
    Task<AgentToolExecutionOutcome> ExecuteAsync(
        AgentToolExecutionRequest request,
        CancellationToken ct = default);

    Task<AgentToolCancellationResult> CancelAsync(
        AgentToolCancellationRequest request,
        CancellationToken ct = default) =>
        Task.FromResult(AgentToolCancellationResult.Failed(
            "tool_cancellation_not_supported",
            "The configured tool execution port does not support durable cancellation.",
            retryable: true));
}

public interface IAgentToolAdmissionLedger
{
    Task<AgentToolAdmissionResult> TryStartAsync(
        AgentToolAdmissionFact fact,
        CancellationToken ct = default);
}

public enum AgentToolAdmissionStatus
{
    Unspecified = 0,
    Started = 1,
    Duplicate = 2,
    Conflict = 3,
    StoreUnavailable = 4,
    InvalidFact = 5,
    Expired = 6,
}

public sealed record AgentToolAdmissionResult(
    AgentToolAdmissionStatus Status,
    string SafeMessage = "");

public sealed record AgentToolExecutionRequest(
    IAgentTool Tool,
    string ArgumentsJson,
    AgentToolExecutionContext ExecutionContext,
    AgentToolApprovalContinuationMode ApprovalContinuationMode,
    AgentToolApprovalGrant? ApprovalGrant,
    AgentToolExecutionAttemptKind ExecutionAttemptKind = AgentToolExecutionAttemptKind.Initial,
    AgentToolUnattendedExecutionAuthorization? UnattendedAuthorization = null,
    AgentToolPendingOperation? PendingOperation = null)
{
    public AgentToolExecutionOwner ExecutionOwner => ExecutionContext.ExecutionOwner;
}

public sealed record AgentToolCancellationRequest(
    IAgentTool Tool,
    string ArgumentsJson,
    AgentToolExecutionContext ExecutionContext,
    AgentToolApprovalContinuationMode ApprovalContinuationMode,
    AgentToolExecutionAttemptKind ExecutionAttemptKind,
    AgentToolPendingOperation PendingOperation,
    AgentToolOperationCancellationReason Reason,
    long DeadlineUnixMs,
    AgentToolCancellationTerminalIntent? TerminalIntent = null,
    AgentToolUnattendedExecutionAuthorization? UnattendedAuthorization = null)
{
    public AgentToolExecutionOwner ExecutionOwner => ExecutionContext.ExecutionOwner;
}

public enum AgentToolCancellationDisposition
{
    Completed = 1,
    Pending = 2,
    Failed = 3,
}

public sealed record AgentToolCancellationResult(
    AgentToolCancellationDisposition Disposition,
    AgentToolExecutionOutcome? CompletedOutcome = null,
    AgentToolPendingOperation? PendingOperation = null,
    string FailureCode = "",
    string SafeMessage = "",
    bool Retryable = false,
    AgentToolCancellationTerminalIntent? PendingTerminalIntent = null)
{
    public static AgentToolCancellationResult Completed(AgentToolExecutionOutcome outcome) =>
        new(AgentToolCancellationDisposition.Completed, CompletedOutcome: outcome);

    public static AgentToolCancellationResult Pending(
        AgentToolPendingOperation operation,
        string failureCode = "",
        string safeMessage = "",
        bool retryable = true,
        AgentToolCancellationTerminalIntent? terminalIntent = null) =>
        new(
            AgentToolCancellationDisposition.Pending,
            PendingOperation: operation,
            FailureCode: failureCode,
            SafeMessage: safeMessage,
            Retryable: retryable,
            PendingTerminalIntent: terminalIntent);

    public static AgentToolCancellationResult Failed(
        string failureCode,
        string safeMessage,
        bool retryable = false) =>
        new(
            AgentToolCancellationDisposition.Failed,
            FailureCode: failureCode,
            SafeMessage: safeMessage,
            Retryable: retryable);
}

public sealed record AgentToolCancellationTerminalIntent(
    AgentToolExecutionOutcomeKind Kind,
    string ResultJson,
    AgentToolReceipt Receipt,
    bool IsMutation,
    string FailureCode,
    string SafeMessage,
    AgentToolExecutionFailureStage FailureStage,
    bool TerminalInvoked,
    bool Retryable,
    AgentToolCallSafety CallSafety,
    string ArgumentsSha256 = "");

public enum AgentToolUnattendedAuthorizationKind
{
    Unspecified = 0,
    WorkflowWebhookExact = 1,
}

/// <summary>
/// Process-local permit produced from actor-owned workflow state after an exact
/// webhook authorization was validated. It is not a human approval grant and
/// is never accepted from an external request payload.
/// </summary>
public sealed record AgentToolUnattendedExecutionAuthorization(
    AgentToolUnattendedAuthorizationKind Kind,
    string AuthorizationId,
    AgentToolExecutionOwner ExecutionOwner,
    string RequestId,
    string ToolName,
    string ToolCallId,
    string ArgumentsSha256,
    string CallSiteId,
    string OperationSelectorDigest);

public enum AgentToolExecutionAttemptKind
{
    Unspecified = 0,
    Initial = 1,
    ActorRecovery = 2,
}

public static class AgentToolExecutionOwners
{
    public static AgentToolExecutionOwner Actor(string actorId) =>
        Create(AgentToolExecutionOwnerKind.Actor, actorId);

    public static AgentToolExecutionOwner Scope(string scopeId) =>
        Create(AgentToolExecutionOwnerKind.Scope, scopeId);

    public static AgentToolExecutionOwner WorkflowRun(string runId) =>
        Create(AgentToolExecutionOwnerKind.WorkflowRun, runId);

    public static AgentToolExecutionOwner ChannelRegistration(string registrationId) =>
        Create(AgentToolExecutionOwnerKind.ChannelRegistration, registrationId);

    public static AgentToolExecutionOwner Connector(string connectorName) =>
        Create(AgentToolExecutionOwnerKind.Connector, connectorName);

    public static AgentToolExecutionOwner HostService(string serviceName) =>
        Create(AgentToolExecutionOwnerKind.HostService, serviceName);

    private static AgentToolExecutionOwner Create(
        AgentToolExecutionOwnerKind kind,
        string ownerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        return new AgentToolExecutionOwner
        {
            Kind = kind,
            OwnerId = ownerId.Trim(),
        };
    }
}

public enum AgentToolApprovalContinuationMode
{
    None = 0,
    ActorOwned = 1,
}

public sealed record AgentToolApprovalGrant(
    AgentToolExecutionOwner ExecutionOwner,
    string ApprovalRequestId,
    string RequestId,
    string ToolName,
    string ToolCallId,
    string ArgumentsSha256);

public enum AgentToolExecutionOutcomeKind
{
    Executed = 0,
    ExecutedAuditIncomplete = 1,
    ApprovalRequired = 2,
    Denied = 3,
    Failed = 4,
    Pending = 5,
}

public enum AgentToolExecutionFailureStage
{
    None = 0,
    RequestValidation = 1,
    Classification = 2,
    CredentialPolicy = 3,
    Approval = 4,
    Admission = 5,
    TerminalExecution = 6,
    TerminalAudit = 7,
}

public sealed record AgentToolExecutionOutcome(
    AgentToolExecutionOutcomeKind Kind,
    string ResultJson,
    AgentToolReceipt Receipt,
    bool IsMutation,
    string FailureCode,
    string SafeMessage,
    AgentToolExecutionFailureStage FailureStage,
    bool TerminalInvoked,
    bool Retryable,
    bool AuditCompleted,
    AgentToolPendingOperation? PendingOperation = null,
    AgentToolCancellationTerminalIntent? CancellationRecoveryIntent = null);
