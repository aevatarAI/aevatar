using Aevatar.Foundation.Abstractions;
using Aevatar.Presentation.AGUI;

namespace Aevatar.Workflow.Presentation.AGUIAdapter;

public sealed class AITextStreamAGUIEventEnvelopeMappingHandler : IAGUIEventEnvelopeMappingHandler
{
    public int Order => 30;

    public bool TryMap(EventEnvelope envelope, out IReadOnlyList<AGUIEvent> events)
    {
        events = [];
        if (envelope.Payload == null)
            return false;

        var payload = envelope.Payload;
        var ts = AGUIEventEnvelopeMappingHelpers.ToUnixMs(envelope.Timestamp);

        if (payload.Is(Aevatar.AI.Abstractions.TextMessageStartEvent.Descriptor))
        {
            var evt = payload.Unpack<Aevatar.AI.Abstractions.TextMessageStartEvent>();
            var msgId = AGUIEventEnvelopeMappingHelpers.ResolveMessageId(evt.SessionId, envelope.Id);
            events =
            [
                new TextMessageStartEvent
                {
                    Timestamp = ts,
                    MessageId = msgId,
                    Role = "assistant",
                },
            ];
            return true;
        }

        if (payload.Is(Aevatar.AI.Abstractions.TextMessageContentEvent.Descriptor))
        {
            var evt = payload.Unpack<Aevatar.AI.Abstractions.TextMessageContentEvent>();
            var msgId = AGUIEventEnvelopeMappingHelpers.ResolveMessageId(evt.SessionId, envelope.Id);
            events =
            [
                new TextMessageContentEvent
                {
                    Timestamp = ts,
                    MessageId = msgId,
                    Delta = evt.Delta,
                },
            ];
            return true;
        }

        if (payload.Is(Aevatar.AI.Abstractions.TextMessageEndEvent.Descriptor))
        {
            var evt = payload.Unpack<Aevatar.AI.Abstractions.TextMessageEndEvent>();
            var msgId = AGUIEventEnvelopeMappingHelpers.ResolveMessageId(evt.SessionId, envelope.Id);
            events =
            [
                new TextMessageEndEvent
                {
                    Timestamp = ts,
                    MessageId = msgId,
                },
            ];
            return true;
        }

        if (payload.Is(Aevatar.AI.Abstractions.ChatResponseEvent.Descriptor))
        {
            var evt = payload.Unpack<Aevatar.AI.Abstractions.ChatResponseEvent>();
            var msgId = AGUIEventEnvelopeMappingHelpers.ResolveMessageId(evt.SessionId, envelope.Id);
            events =
            [
                new TextMessageStartEvent
                {
                    Timestamp = ts,
                    MessageId = msgId,
                    Role = "assistant",
                },
                new TextMessageContentEvent
                {
                    Timestamp = ts,
                    MessageId = msgId,
                    Delta = evt.Content,
                },
                new TextMessageEndEvent
                {
                    Timestamp = ts,
                    MessageId = msgId,
                },
            ];
            return true;
        }

        return false;
    }
}
