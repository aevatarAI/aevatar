using Aevatar.Foundation.Abstractions;
using Aevatar.Presentation.AGUI;

namespace Aevatar.Workflow.Presentation.AGUIAdapter;

public sealed class AIReasoningAGUIEventEnvelopeMappingHandler : IAGUIEventEnvelopeMappingHandler
{
    public int Order => 35;

    public bool TryMap(EventEnvelope envelope, out IReadOnlyList<AGUIEvent> events)
    {
        if (envelope.Payload?.Is(Aevatar.AI.Abstractions.TextMessageReasoningEvent.Descriptor) != true)
        {
            events = [];
            return false;
        }

        var evt = envelope.Payload.Unpack<Aevatar.AI.Abstractions.TextMessageReasoningEvent>();
        events =
        [
            new CustomEvent
            {
                Timestamp = AGUIEventEnvelopeMappingHelpers.ToUnixMs(envelope.Timestamp),
                Name = "aevatar.llm.reasoning",
                Value = new
                {
                    evt.SessionId,
                    evt.Delta,
                    Role = AGUIEventEnvelopeMappingHelpers.ResolveRoleFromPublisher(envelope.PublisherId),
                },
            },
        ];
        return true;
    }
}

