using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Application;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class ScriptEvolutionEnvelopeFactory
    : ICommandEnvelopeFactory<ScriptEvolutionProposal>
{
    private readonly StartScriptEvolutionSessionActorRequestAdapter _adapter = new();

    public EventEnvelope CreateEnvelope(ScriptEvolutionProposal command, CommandContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        return _adapter.Map(
            new StartScriptEvolutionSessionActorRequest(
                ProposalId: string.IsNullOrWhiteSpace(command.ProposalId) ? Guid.NewGuid().ToString("N") : command.ProposalId,
                ScriptId: command.ScriptId ?? string.Empty,
                BaseRevision: command.BaseRevision ?? string.Empty,
                CandidateRevision: command.CandidateRevision ?? string.Empty,
                CandidateSource: command.CandidateSource ?? string.Empty,
                CandidateSourceHash: command.CandidateSourceHash ?? string.Empty,
                Reason: command.Reason ?? string.Empty),
            context.TargetId);
    }
}
