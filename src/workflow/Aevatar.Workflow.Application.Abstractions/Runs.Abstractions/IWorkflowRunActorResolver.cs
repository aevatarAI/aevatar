namespace Aevatar.Workflow.Application.Abstractions.Runs;

public interface IWorkflowRunActorResolver
{
    Task<WorkflowActorResolutionResult> ResolveOrCreateAsync(
        WorkflowChatRunRequest request,
        CancellationToken ct = default);
}

public sealed record WorkflowActorResolutionResult(
    WorkflowRunCreationReceipt? Target,
    string WorkflowNameForRun,
    WorkflowChatRunStartError Error,
    WorkflowChatRunStartFailureDetail? FailureDetail = null);
