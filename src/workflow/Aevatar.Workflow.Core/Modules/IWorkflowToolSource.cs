using Aevatar.Workflow.Abstractions;

namespace Aevatar.Workflow.Core.Modules;

public interface IWorkflowTool
{
    string Name { get; }

    Task<WorkflowToolExecutionResult> ExecuteAsync(WorkflowToolExecutionRequest request, CancellationToken ct = default);
}

public sealed record WorkflowToolExecutionResult(
    string ResultJson,
    WorkflowManagedHandoffOutcome? ManagedHandoff = null,
    WorkflowToolApprovalPendingOutcome? PendingApproval = null)
{
    public static WorkflowToolExecutionResult Success(string resultJson) =>
        new(resultJson ?? string.Empty);
}

public sealed record WorkflowToolApprovalPendingOutcome(
    string ApprovalRequestId,
    string ToolName,
    string ToolCallId,
    string ArgumentsJson,
    string ApprovalMode,
    bool IsReadOnly,
    bool IsDestructive);

public sealed record WorkflowToolExecutionRequest(
    string ArgumentsJson,
    string RunId,
    string StepId,
    string ExecutionId,
    string CallId,
    string ScopeId,
    WorkflowCallerCredential CallerCredential,
    WorkflowToolRuntimeContext RuntimeContext,
    ToolApprovalGrant? ApprovalGrant = null)
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
}

public sealed record ToolApprovalGrant(
    string ApprovalRequestId,
    string ToolName,
    string ToolCallId);

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
