using Aevatar.Foundation.Abstractions;

namespace Aevatar.Scripting.Application;

public sealed class UpsertScriptDefinitionActorRequestAdapter
{
    public EventEnvelope Map(UpsertScriptDefinitionActorRequest request, string actorId)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);

        return ScriptingActorRequestEnvelopeFactory.Create(
            actorId,
            request.ScriptRevision ?? string.Empty,
            new UpsertScriptDefinitionRequestedEvent
            {
                ScriptId = request.ScriptId ?? string.Empty,
                ScriptRevision = request.ScriptRevision ?? string.Empty,
                SourceText = request.SourceText ?? string.Empty,
                SourceHash = request.SourceHash ?? string.Empty,
            });
    }
}
