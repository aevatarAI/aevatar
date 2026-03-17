using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Scripting.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Projection.Orchestration;

public sealed class ScriptEvolutionSessionEventCodec
    : IProjectionSessionEventCodec<ScriptEvolutionSessionCompletedEvent>
{
    private const string EventType = "session.completed";

    public string Channel => "script-evolution";

    public string GetEventType(ScriptEvolutionSessionCompletedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        return EventType;
    }

    public ByteString Serialize(ScriptEvolutionSessionCompletedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        return Any.Pack(evt).ToByteString();
    }

    public ScriptEvolutionSessionCompletedEvent? Deserialize(string eventType, ByteString payload)
    {
        if (!string.Equals(eventType, EventType, StringComparison.Ordinal) ||
            payload == null ||
            payload.IsEmpty)
        {
            return null;
        }

        try
        {
            var envelope = Any.Parser.ParseFrom(payload);
            if (!envelope.Is(ScriptEvolutionSessionCompletedEvent.Descriptor))
                return null;

            return envelope.Unpack<ScriptEvolutionSessionCompletedEvent>();
        }
        catch (InvalidProtocolBufferException)
        {
            return null;
        }
    }
}
