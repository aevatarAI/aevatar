using Aevatar.Foundation.Abstractions;

namespace Aevatar.Scripting.Application;

public sealed class StartScriptEvolutionSessionActorRequestAdapter
{
    public EventEnvelope Map(StartScriptEvolutionSessionActorRequest request, string actorId)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);

        return ScriptingActorRequestEnvelopeFactory.Create(
            actorId,
            request.ProposalId ?? string.Empty,
            new StartScriptEvolutionSessionRequestedEvent
            {
                ProposalId = request.ProposalId ?? string.Empty,
                ScriptId = request.ScriptId ?? string.Empty,
                BaseRevision = request.BaseRevision ?? string.Empty,
                CandidateRevision = request.CandidateRevision ?? string.Empty,
                CandidateSource = request.CandidateSource ?? string.Empty,
                CandidateSourceHash = request.CandidateSourceHash ?? string.Empty,
                Reason = request.Reason ?? string.Empty,
            });
    }
}
