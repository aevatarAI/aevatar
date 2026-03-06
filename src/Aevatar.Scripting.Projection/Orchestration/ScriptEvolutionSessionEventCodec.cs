using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Scripting.Abstractions;
using Google.Protobuf;

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

    public string Serialize(ScriptEvolutionSessionCompletedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        return Convert.ToBase64String(evt.ToByteArray());
    }

    public ScriptEvolutionSessionCompletedEvent? Deserialize(string eventType, string payload)
    {
        if (!string.Equals(eventType, EventType, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(payload))
            return null;

        try
        {
            return ScriptEvolutionSessionCompletedEvent.Parser.ParseFrom(Convert.FromBase64String(payload));
        }
        catch (FormatException)
        {
            return null;
        }
        catch (InvalidProtocolBufferException)
        {
            return null;
        }
    }
}
