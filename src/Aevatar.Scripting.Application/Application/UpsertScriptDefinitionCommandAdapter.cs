using Aevatar.Foundation.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Application;

public sealed class UpsertScriptDefinitionCommandAdapter
{
    private const string CommandPublisherId = "scripting.application";

    public EventEnvelope Map(UpsertScriptDefinitionCommand command, string actorId)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);

        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(new UpsertScriptDefinitionRequestedEvent
            {
                ScriptId = command.ScriptId ?? string.Empty,
                ScriptRevision = command.ScriptRevision ?? string.Empty,
                SourceText = command.SourceText ?? string.Empty,
                SourceHash = command.SourceHash ?? string.Empty,
            }),
            PublisherId = CommandPublisherId,
            Direction = EventDirection.Self,
            TargetActorId = actorId,
            CorrelationId = command.ScriptRevision ?? string.Empty,
        };
    }
}
