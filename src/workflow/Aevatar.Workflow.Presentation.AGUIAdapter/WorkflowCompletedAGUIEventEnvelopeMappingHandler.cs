using Aevatar.Foundation.Abstractions;
using Aevatar.Presentation.AGUI;
using Aevatar.Workflow.Core;

namespace Aevatar.Workflow.Presentation.AGUIAdapter;

public sealed class WorkflowCompletedAGUIEventEnvelopeMappingHandler : IAGUIEventEnvelopeMappingHandler
{
    public int Order => 40;

    public bool TryMap(EventEnvelope envelope, out IReadOnlyList<AGUIEvent> events)
    {
        if (envelope.Payload?.Is(WorkflowCompletedEvent.Descriptor) != true)
        {
            events = [];
            return false;
        }

        var evt = envelope.Payload.Unpack<WorkflowCompletedEvent>();
        var ts = AGUIEventEnvelopeMappingHelpers.ToUnixMs(envelope.Timestamp);

        if (evt.Success)
        {
            var threadId = AGUIEventEnvelopeMappingHelpers.ResolveThreadId(envelope, evt.WorkflowName);
            var runId = AGUIEventEnvelopeMappingHelpers.ResolveRunId(envelope, threadId);
            events =
            [
                new RunFinishedEvent
                {
                    Timestamp = ts,
                    ThreadId = threadId,
                    RunId = runId,
                    Result = new { output = evt.Output },
                },
            ];
            return true;
        }

        var errorThreadId = AGUIEventEnvelopeMappingHelpers.ResolveThreadId(envelope, evt.WorkflowName);
        events =
        [
            new RunErrorEvent
            {
                Timestamp = ts,
                Message = evt.Error,
                RunId = AGUIEventEnvelopeMappingHelpers.ResolveRunId(envelope, errorThreadId),
                Code = "WORKFLOW_FAILED",
            },
        ];
        return true;
    }
}

