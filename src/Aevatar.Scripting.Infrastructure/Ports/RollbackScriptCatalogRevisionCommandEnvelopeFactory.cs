using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Application;
using Aevatar.Scripting.Core;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class RollbackScriptCatalogRevisionCommandEnvelopeFactory
    : ICommandEnvelopeFactory<RollbackScriptCatalogRevisionCommand>
{
    public EventEnvelope CreateEnvelope(
        RollbackScriptCatalogRevisionCommand command,
        CommandContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        return ScriptingActorRequestEnvelopeFactory.Create(
            context.TargetId,
            context.CorrelationId,
            new RollbackScriptRevisionRequestedEvent
            {
                ScriptId = command.ScriptId ?? string.Empty,
                TargetRevision = command.TargetRevision ?? string.Empty,
                Reason = command.Reason ?? string.Empty,
                ProposalId = command.ProposalId ?? string.Empty,
                ExpectedCurrentRevision = command.ExpectedCurrentRevision ?? string.Empty,
            });
    }
}
