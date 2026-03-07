using Aevatar.Foundation.Abstractions;
using Aevatar.Presentation.AGUI;

namespace Aevatar.Workflow.Presentation.AGUIAdapter;

public sealed class ToolCallAGUIEventEnvelopeMappingHandler : IAGUIEventEnvelopeMappingHandler
{
    public int Order => 50;

    public bool TryMap(EventEnvelope envelope, out IReadOnlyList<AGUIEvent> events)
    {
        events = [];
        if (envelope.Payload == null)
            return false;

        var payload = envelope.Payload;
        var ts = AGUIEventEnvelopeMappingHelpers.ToUnixMs(envelope.Timestamp);

        if (payload.Is(Aevatar.AI.Abstractions.ToolCallEvent.Descriptor))
        {
            var evt = payload.Unpack<Aevatar.AI.Abstractions.ToolCallEvent>();
            events =
            [
                new ToolCallStartEvent
                {
                    Timestamp = ts,
                    ToolCallId = evt.CallId,
                    ToolName = evt.ToolName,
                },
            ];
            return true;
        }

        if (payload.Is(Aevatar.AI.Abstractions.ToolResultEvent.Descriptor))
        {
            var evt = payload.Unpack<Aevatar.AI.Abstractions.ToolResultEvent>();
            events =
            [
                new ToolCallEndEvent
                {
                    Timestamp = ts,
                    ToolCallId = evt.CallId,
                    Result = evt.ResultJson,
                },
            ];
            return true;
        }

        return false;
    }
}
