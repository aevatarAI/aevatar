using Aevatar.AI.Abstractions;

namespace Aevatar.AI.Abstractions.ToolProviders;

public interface IAgentToolExecutionPort
{
    Task<AgentToolExecutionOutcome> ExecuteAsync(
        AgentToolExecutionRequest request,
        CancellationToken ct = default);
}

public sealed record AgentToolExecutionRequest(
    IAgentTool Tool,
    string ArgumentsJson,
    AgentToolExecutionContext ExecutionContext,
    AgentToolApprovalContinuationMode ApprovalContinuationMode,
    AgentToolApprovalGrant? ApprovalGrant);

public enum AgentToolApprovalContinuationMode
{
    None = 0,
    ActorOwned = 1,
}

public sealed record AgentToolApprovalGrant(
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
}

public enum AgentToolExecutionFailureStage
{
    None = 0,
    RequestValidation = 1,
    Classification = 2,
    CredentialPolicy = 3,
    Approval = 4,
    AuditIntent = 5,
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
    bool AuditCompleted);
