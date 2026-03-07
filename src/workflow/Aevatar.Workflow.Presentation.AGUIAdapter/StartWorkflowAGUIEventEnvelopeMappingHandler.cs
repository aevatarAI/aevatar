using Aevatar.Foundation.Abstractions;
using Aevatar.Presentation.AGUI;
using Aevatar.Workflow.Core;

namespace Aevatar.Workflow.Presentation.AGUIAdapter;

public sealed class StartWorkflowAGUIEventEnvelopeMappingHandler : IAGUIEventEnvelopeMappingHandler
{
    public int Order => 0;

    public bool TryMap(EventEnvelope envelope, out IReadOnlyList<AGUIEvent> events)
    {
        if (envelope.Payload?.Is(StartWorkflowEvent.Descriptor) != true)
        {
            events = [];
            return false;
        }

        var evt = envelope.Payload.Unpack<StartWorkflowEvent>();
        var threadId = AGUIEventEnvelopeMappingHelpers.ResolveThreadId(envelope, evt.WorkflowName);
        var runId = AGUIEventEnvelopeMappingHelpers.ResolveRunId(envelope, threadId);
        events =
        [
            new RunStartedEvent
            {
                Timestamp = AGUIEventEnvelopeMappingHelpers.ToUnixMs(envelope.Timestamp),
                ThreadId = threadId,
                RunId = runId,
            },
        ];
        return true;
    }
}

