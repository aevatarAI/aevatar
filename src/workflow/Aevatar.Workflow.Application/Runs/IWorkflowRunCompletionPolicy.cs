using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Application.Runs;

public interface IWorkflowRunCompletionPolicy
{
    bool TryResolve(
        WorkflowRunEventEnvelope evt,
        out WorkflowProjectionCompletionStatus status);
}
