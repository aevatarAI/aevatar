using Aevatar.Foundation.Abstractions;
using Aevatar.Presentation.AGUI;
using Aevatar.Workflow.Core;

namespace Aevatar.Workflow.Presentation.AGUIAdapter;

public sealed class WorkflowWaitingSignalAGUIEventEnvelopeMappingHandler : IAGUIEventEnvelopeMappingHandler
{
    public int Order => 46;

    public bool TryMap(EventEnvelope envelope, out IReadOnlyList<AGUIEvent> events)
    {
        if (envelope.Payload?.Is(WaitingForSignalEvent.Descriptor) != true)
        {
            events = [];
            return false;
        }

        var evt = envelope.Payload.Unpack<WaitingForSignalEvent>();
        var ts = AGUIEventEnvelopeMappingHelpers.ToUnixMs(envelope.Timestamp);
        var runId = string.IsNullOrWhiteSpace(evt.RunId)
            ? AGUIEventEnvelopeMappingHelpers.ResolveRunId(envelope, string.Empty)
            : evt.RunId;

        events =
        [
            new CustomEvent
            {
                Timestamp = ts,
                Name = "aevatar.workflow.waiting_signal",
                Value = new
                {
                    RunId = runId,
                    evt.StepId,
                    evt.SignalName,
                    evt.Prompt,
                    evt.TimeoutMs,
                    evt.WaitToken,
                },
            },
        ];
        return true;
    }
}

