using Aevatar.Workflow.Core;

namespace Aevatar.CQRS.Projection.WorkflowExecution.Orchestration;

/// <summary>
/// Marks projection completion when the envelope payload is <see cref="WorkflowCompletedEvent" />.
/// </summary>
public sealed class WorkflowCompletedEventProjectionCompletionDetector<TContext>
    : IProjectionCompletionDetector<TContext>
    where TContext : IProjectionRunContext
{
    public bool IsProjectionCompleted(TContext context, EventEnvelope envelope)
    {
        var payload = envelope.Payload;
        if (payload == null || !payload.Is(WorkflowCompletedEvent.Descriptor))
            return false;

        var completed = payload.Unpack<WorkflowCompletedEvent>();
        return string.Equals(completed.RunId, context.RunId, StringComparison.Ordinal);
    }
}
