using Aevatar.Foundation.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Application;

public sealed class RunScriptActorRequestAdapter
{
    private const string RequestPublisherId = "scripting.application";

    public EventEnvelope Map(RunScriptActorRequest request, string actorId)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);

        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(new RunScriptRequestedEvent
            {
                RunId = request.RunId ?? string.Empty,
                InputPayload = request.InputPayload?.Clone(),
                ScriptRevision = request.ScriptRevision ?? string.Empty,
                DefinitionActorId = request.DefinitionActorId ?? string.Empty,
                RequestedEventType = request.RequestedEventType ?? string.Empty,
            }),
            PublisherId = RequestPublisherId,
            Direction = EventDirection.Self,
            TargetActorId = actorId,
            CorrelationId = request.RunId ?? string.Empty,
        };
    }
}
