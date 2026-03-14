using Aevatar.CQRS.Projection.Core.Abstractions;
using Google.Protobuf;

namespace Aevatar.Workflow.Projection.Orchestration;

public sealed class WorkflowBindingSessionEventCodec : IProjectionSessionEventCodec<EventEnvelope>
{
    public string Channel => "workflow-binding";

    public string GetEventType(EventEnvelope evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        return string.IsNullOrWhiteSpace(evt.Payload?.TypeUrl)
            ? EventEnvelope.Descriptor.FullName
            : evt.Payload.TypeUrl;
    }

    public ByteString Serialize(EventEnvelope evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        return evt.ToByteString();
    }

    public EventEnvelope? Deserialize(string eventType, ByteString payload)
    {
        if (string.IsNullOrWhiteSpace(eventType) || payload == null || payload.IsEmpty)
            return null;

        try
        {
            var envelope = EventEnvelope.Parser.ParseFrom(payload);
            return string.Equals(GetEventType(envelope), eventType, StringComparison.Ordinal)
                ? envelope
                : null;
        }
        catch (InvalidProtocolBufferException)
        {
            return null;
        }
    }
}
