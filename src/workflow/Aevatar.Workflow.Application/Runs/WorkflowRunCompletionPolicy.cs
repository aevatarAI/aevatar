using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Application.Runs;

public sealed class WorkflowRunCompletionPolicy : IWorkflowRunCompletionPolicy
{
    public bool TryResolve(
        WorkflowRunEventEnvelope evt,
        out WorkflowProjectionCompletionStatus status)
    {
        status = WorkflowProjectionCompletionStatus.Unknown;
        if (evt.EventCase == WorkflowRunEventEnvelope.EventOneofCase.RunFinished)
        {
            status = WorkflowProjectionCompletionStatus.Completed;
            return true;
        }

        if (evt.EventCase == WorkflowRunEventEnvelope.EventOneofCase.RunError)
        {
            status = WorkflowProjectionCompletionStatus.Failed;
            return true;
        }

        return false;
    }
}
