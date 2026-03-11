using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Scripting.Application;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class ScriptEvolutionAcceptedReceiptFactory
    : ICommandReceiptFactory<ScriptEvolutionCommandTarget, ScriptEvolutionAcceptedReceipt>
{
    public ScriptEvolutionAcceptedReceipt Create(
        ScriptEvolutionCommandTarget target,
        CommandContext context)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(context);

        return new ScriptEvolutionAcceptedReceipt(
            ManagerActorId: target.ManagerActorId,
            SessionActorId: target.SessionActorId,
            ProposalId: target.ProposalId,
            CommandId: context.CommandId,
            CorrelationId: context.CorrelationId);
    }
}
