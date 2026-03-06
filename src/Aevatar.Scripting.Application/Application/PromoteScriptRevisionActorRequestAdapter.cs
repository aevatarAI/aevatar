using Aevatar.Foundation.Abstractions;

namespace Aevatar.Scripting.Application;

public sealed class PromoteScriptRevisionActorRequestAdapter
{
    public EventEnvelope Map(PromoteScriptRevisionActorRequest request, string actorId)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);

        return ScriptingActorRequestEnvelopeFactory.Create(
            actorId,
            request.ProposalId ?? string.Empty,
            new PromoteScriptRevisionRequestedEvent
            {
                ScriptId = request.ScriptId ?? string.Empty,
                Revision = request.Revision ?? string.Empty,
                DefinitionActorId = request.DefinitionActorId ?? string.Empty,
                SourceHash = request.SourceHash ?? string.Empty,
                ProposalId = request.ProposalId ?? string.Empty,
                ExpectedBaseRevision = request.ExpectedBaseRevision ?? string.Empty,
            });
    }
}
