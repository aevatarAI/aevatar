using Aevatar.Workflow.Abstractions;

namespace Aevatar.Workflow.Core.Modules;

public interface IWorkflowTool
{
    string Name { get; }

    Task<WorkflowToolExecutionResult> ExecuteAsync(WorkflowToolExecutionRequest request, CancellationToken ct = default);
}

public enum WorkflowToolExecutionOutcome
{
    Success = 0,
    ApprovalPending = 1,
}

public sealed record WorkflowToolExecutionResult(
    string ResultJson,
    WorkflowManagedHandoffOutcome? ManagedHandoff = null,
    WorkflowToolApprovalPendingOutcome? ApprovalPending = null,
    WorkflowToolExecutionOutcome Outcome = WorkflowToolExecutionOutcome.Success)
{
    public static WorkflowToolExecutionResult Success(string resultJson) =>
        new(resultJson ?? string.Empty);

    public static WorkflowToolExecutionResult PendingApproval(WorkflowToolApprovalPendingOutcome approvalPending) =>
        new(string.Empty, ApprovalPending: approvalPending, Outcome: WorkflowToolExecutionOutcome.ApprovalPending);
}

public sealed record WorkflowToolApprovalPendingOutcome(
    string ApprovalRequestId,
    string ToolName,
    string ToolCallId,
    string ArgumentsJson);

public sealed record WorkflowToolApprovalGrant(
    string ApprovalRequestId,
    bool Approved);

public sealed record WorkflowToolExecutionRequest(
    string ArgumentsJson,
    string RunId,
    string StepId,
    string ExecutionId,
    string CallId,
    string ScopeId,
    WorkflowCallerCredential CallerCredential,
    WorkflowToolRuntimeContext RuntimeContext,
    WorkflowToolApprovalGrant? ApprovalGrant = null)
{
    public WorkflowToolExecutionRequest(
        string ArgumentsJson,
        string RunId,
        string StepId,
        string ExecutionId,
        string CallId,
        string ScopeId,
        WorkflowCallerCredential CallerCredential)
        : this(
            ArgumentsJson,
            RunId,
            StepId,
            ExecutionId,
            CallId,
            ScopeId,
            CallerCredential,
            WorkflowToolRuntimeContext.Empty,
            null)
    {
    }

    public WorkflowToolExecutionRequest(
        string ArgumentsJson,
        string RunId,
        string StepId,
        string ExecutionId,
        string CallId,
        string ScopeId,
        WorkflowCallerCredential CallerCredential,
        WorkflowToolApprovalGrant? ApprovalGrant)
        : this(
            ArgumentsJson,
            RunId,
            StepId,
            ExecutionId,
            CallId,
            ScopeId,
            CallerCredential,
            WorkflowToolRuntimeContext.Empty,
            ApprovalGrant)
    {
    }
}

public sealed record WorkflowToolRuntimeContext(
    string ParentActorId,
    string ParentRunId,
    string ParentStepId,
    string RootRunId,
    int Depth)
{
    public static WorkflowToolRuntimeContext Empty { get; } = new(
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        0);
}

public interface IWorkflowToolSource
{
    Task<IReadOnlyList<IWorkflowTool>> GetToolsAsync(CancellationToken ct = default);
}
