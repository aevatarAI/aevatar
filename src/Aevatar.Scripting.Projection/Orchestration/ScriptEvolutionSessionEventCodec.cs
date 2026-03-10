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

    public Any Serialize(ScriptEvolutionSessionCompletedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        return Any.Pack(evt);
    }

    public ScriptEvolutionSessionCompletedEvent? Deserialize(string eventType, Any payload)
    {
        if (!string.Equals(eventType, EventType, StringComparison.Ordinal) ||
            payload == null ||
            !payload.Is(ScriptEvolutionSessionCompletedEvent.Descriptor))
        {
            return null;
        }

        try
        {
            return payload.Unpack<ScriptEvolutionSessionCompletedEvent>();
        }
        catch (InvalidProtocolBufferException)
        {
            return null;
        }
    }
}
