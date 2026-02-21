using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Application.Runs;

public interface IWorkflowRunCompletionPolicy
{
    bool TryResolve(
        WorkflowOutputFrame frame,
        out WorkflowProjectionCompletionStatus status);
}
