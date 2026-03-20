using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Application;
using Aevatar.Scripting.Core;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class PromoteScriptCatalogRevisionCommandEnvelopeFactory
    : ICommandEnvelopeFactory<PromoteScriptCatalogRevisionCommand>
{
    public EventEnvelope CreateEnvelope(
        PromoteScriptCatalogRevisionCommand command,
        CommandContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        return ScriptingActorRequestEnvelopeFactory.Create(
            context.TargetId,
            context.CorrelationId,
            new PromoteScriptRevisionRequestedEvent
            {
                ScriptId = command.ScriptId ?? string.Empty,
                Revision = command.Revision ?? string.Empty,
                DefinitionActorId = command.DefinitionActorId ?? string.Empty,
                SourceHash = command.SourceHash ?? string.Empty,
                ProposalId = command.ProposalId ?? string.Empty,
                ExpectedBaseRevision = command.ExpectedBaseRevision ?? string.Empty,
                ScopeId = command.ScopeId ?? string.Empty,
            });
    }
}
