using Aevatar.Workflow.Core;

namespace Aevatar.Workflow.Projection.RunIdResolvers;

public sealed class WorkflowCoreRunIdResolver : IWorkflowExecutionRunIdResolver
{
    public int Order => 0;

    public bool TryResolve(EventEnvelope envelope, out string? runId)
    {
        runId = null;
        var payload = envelope.Payload;
        if (payload == null)
            return false;

        if (payload.Is(StartWorkflowEvent.Descriptor))
        {
            runId = payload.Unpack<StartWorkflowEvent>().RunId;
            return !string.IsNullOrWhiteSpace(runId);
        }

        if (payload.Is(StepRequestEvent.Descriptor))
        {
            runId = payload.Unpack<StepRequestEvent>().RunId;
            return !string.IsNullOrWhiteSpace(runId);
        }

        if (payload.Is(StepCompletedEvent.Descriptor))
        {
            runId = payload.Unpack<StepCompletedEvent>().RunId;
            return !string.IsNullOrWhiteSpace(runId);
        }

        if (payload.Is(WorkflowSuspendedEvent.Descriptor))
        {
            runId = payload.Unpack<WorkflowSuspendedEvent>().RunId;
            return !string.IsNullOrWhiteSpace(runId);
        }

        if (payload.Is(WorkflowResumedEvent.Descriptor))
        {
            runId = payload.Unpack<WorkflowResumedEvent>().RunId;
            return !string.IsNullOrWhiteSpace(runId);
        }

        if (payload.Is(WorkflowCompletedEvent.Descriptor))
        {
            runId = payload.Unpack<WorkflowCompletedEvent>().RunId;
            return !string.IsNullOrWhiteSpace(runId);
        }

        return false;
    }
}
