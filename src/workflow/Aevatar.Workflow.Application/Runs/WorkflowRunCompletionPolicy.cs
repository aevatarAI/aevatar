using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Application.Runs;

public sealed class WorkflowRunCompletionPolicy
    : ICommandCompletionPolicy<WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus>
{
    public WorkflowProjectionCompletionStatus IncompleteCompletion => WorkflowProjectionCompletionStatus.Unknown;

    public bool TryResolve(
        WorkflowRunEventEnvelope evt,
        out WorkflowProjectionCompletionStatus completion)
    {
        completion = WorkflowProjectionCompletionStatus.Unknown;
        if (evt.EventCase == WorkflowRunEventEnvelope.EventOneofCase.RunFinished)
        {
            completion = WorkflowProjectionCompletionStatus.Completed;
            return true;
        }

        if (evt.EventCase == WorkflowRunEventEnvelope.EventOneofCase.RunError)
        {
            completion = WorkflowProjectionCompletionStatus.Failed;
            return true;
        }

        return false;
    }
}
