using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Application.Runs;

public sealed class WorkflowRunCompletionPolicy : IWorkflowRunCompletionPolicy
{
    public bool TryResolve(
        WorkflowOutputFrame frame,
        out WorkflowProjectionCompletionStatus status)
    {
        status = WorkflowProjectionCompletionStatus.Unknown;
        if (string.Equals(frame.Type, WorkflowRunEventTypes.RunFinished, StringComparison.Ordinal))
        {
            status = WorkflowProjectionCompletionStatus.Completed;
            return true;
        }

        if (string.Equals(frame.Type, WorkflowRunEventTypes.RunError, StringComparison.Ordinal))
        {
            status = WorkflowProjectionCompletionStatus.Failed;
            return true;
        }

        return false;
    }
}
