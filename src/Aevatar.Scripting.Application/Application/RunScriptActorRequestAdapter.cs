using Aevatar.Foundation.Abstractions;

namespace Aevatar.Scripting.Application;

public sealed class RunScriptActorRequestAdapter
{
    public EventEnvelope Map(RunScriptActorRequest request, string actorId)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);

        return ScriptingActorRequestEnvelopeFactory.Create(
            actorId,
            request.RunId ?? string.Empty,
            new RunScriptRequestedEvent
            {
                RunId = request.RunId ?? string.Empty,
                InputPayload = request.InputPayload?.Clone(),
                ScriptRevision = request.ScriptRevision ?? string.Empty,
                DefinitionActorId = request.DefinitionActorId ?? string.Empty,
                RequestedEventType = request.RequestedEventType ?? string.Empty,
            });
    }
}
