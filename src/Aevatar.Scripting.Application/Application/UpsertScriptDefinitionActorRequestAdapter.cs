using Aevatar.Foundation.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Application;

public sealed class UpsertScriptDefinitionActorRequestAdapter
{
    private const string RequestPublisherId = "scripting.application";

    public EventEnvelope Map(UpsertScriptDefinitionActorRequest request, string actorId)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);

        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(new UpsertScriptDefinitionRequestedEvent
            {
                ScriptId = request.ScriptId ?? string.Empty,
                ScriptRevision = request.ScriptRevision ?? string.Empty,
                SourceText = request.SourceText ?? string.Empty,
                SourceHash = request.SourceHash ?? string.Empty,
            }),
            PublisherId = RequestPublisherId,
            Direction = EventDirection.Self,
            TargetActorId = actorId,
            CorrelationId = request.ScriptRevision ?? string.Empty,
        };
    }
}
