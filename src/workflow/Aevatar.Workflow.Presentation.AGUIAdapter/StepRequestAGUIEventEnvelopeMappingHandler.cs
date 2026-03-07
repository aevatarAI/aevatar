using Aevatar.Foundation.Abstractions;
using Aevatar.Presentation.AGUI;
using Aevatar.Workflow.Core;

namespace Aevatar.Workflow.Presentation.AGUIAdapter;

public sealed class StepRequestAGUIEventEnvelopeMappingHandler : IAGUIEventEnvelopeMappingHandler
{
    public int Order => 10;

    public bool TryMap(EventEnvelope envelope, out IReadOnlyList<AGUIEvent> events)
    {
        if (envelope.Payload?.Is(StepRequestEvent.Descriptor) != true)
        {
            events = [];
            return false;
        }

        var evt = envelope.Payload.Unpack<StepRequestEvent>();
        var ts = AGUIEventEnvelopeMappingHelpers.ToUnixMs(envelope.Timestamp);
        events =
        [
            new StepStartedEvent { Timestamp = ts, StepName = evt.StepId },
            new CustomEvent
            {
                Timestamp = ts,
                Name = "aevatar.step.request",
                Value = new
                {
                    evt.RunId,
                    evt.StepId,
                    evt.StepType,
                    evt.TargetRole,
                    evt.Input,
                },
            },
        ];
        return true;
    }
}

