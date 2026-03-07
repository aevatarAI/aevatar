using Aevatar.Foundation.Abstractions;
using Aevatar.Presentation.AGUI;
using Aevatar.Workflow.Core;

namespace Aevatar.Workflow.Presentation.AGUIAdapter;

public sealed class StepCompletedAGUIEventEnvelopeMappingHandler : IAGUIEventEnvelopeMappingHandler
{
    public int Order => 20;

    public bool TryMap(EventEnvelope envelope, out IReadOnlyList<AGUIEvent> events)
    {
        if (envelope.Payload?.Is(StepCompletedEvent.Descriptor) != true)
        {
            events = [];
            return false;
        }

        var evt = envelope.Payload.Unpack<StepCompletedEvent>();
        var metadata = new Dictionary<string, string>();
        foreach (var (key, value) in evt.Metadata)
            metadata[key] = value;
        events =
        [
            new StepFinishedEvent
            {
                Timestamp = AGUIEventEnvelopeMappingHelpers.ToUnixMs(envelope.Timestamp),
                StepName = evt.StepId,
            },
            new CustomEvent
            {
                Timestamp = AGUIEventEnvelopeMappingHelpers.ToUnixMs(envelope.Timestamp),
                Name = "aevatar.step.completed",
                Value = new
                {
                    evt.RunId,
                    evt.StepId,
                    evt.Success,
                    evt.Output,
                    evt.Error,
                    Metadata = metadata,
                },
            },
        ];
        return true;
    }
}

