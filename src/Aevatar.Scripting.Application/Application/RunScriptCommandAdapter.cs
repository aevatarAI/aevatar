using Aevatar.Foundation.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Application;

public sealed class RunScriptCommandAdapter
{
    private const string CommandPublisherId = "scripting.application";

    public EventEnvelope Map(RunScriptCommand command, string actorId)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);

        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(new RunScriptRequestedEvent
            {
                RunId = command.RunId ?? string.Empty,
                InputPayload = command.InputPayload?.Clone(),
                ScriptRevision = command.ScriptRevision ?? string.Empty,
                DefinitionActorId = command.DefinitionActorId ?? string.Empty,
                RequestedEventType = command.RequestedEventType ?? string.Empty,
            }),
            PublisherId = CommandPublisherId,
            Direction = EventDirection.Self,
            TargetActorId = actorId,
            CorrelationId = command.RunId ?? string.Empty,
        };
    }
}
