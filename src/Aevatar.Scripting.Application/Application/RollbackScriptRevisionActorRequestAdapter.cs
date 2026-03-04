using Aevatar.Foundation.Abstractions;

namespace Aevatar.Scripting.Application;

public sealed class RollbackScriptRevisionActorRequestAdapter
{
    public EventEnvelope Map(RollbackScriptRevisionActorRequest request, string actorId)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);

        return ScriptingActorRequestEnvelopeFactory.Create(
            actorId,
            request.ProposalId ?? string.Empty,
            new RollbackScriptRevisionRequestedEvent
            {
                ScriptId = request.ScriptId ?? string.Empty,
                TargetRevision = request.TargetRevision ?? string.Empty,
                Reason = request.Reason ?? string.Empty,
                ProposalId = request.ProposalId ?? string.Empty,
                ExpectedCurrentRevision = request.ExpectedCurrentRevision ?? string.Empty,
            });
    }
}
