using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Application;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class ProvisionScriptRuntimeCommandEnvelopeFactory
    : ICommandEnvelopeFactory<ProvisionScriptRuntimeCommand>
{
    public EventEnvelope CreateEnvelope(
        ProvisionScriptRuntimeCommand command,
        CommandContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        return ScriptingActorRequestEnvelopeFactory.Create(
            context.TargetId,
            context.CorrelationId,
            new ProvisionScriptBehaviorRequestedEvent
            {
                DefinitionActorId = command.DefinitionActorId ?? string.Empty,
                RequestedRevision = command.ScriptRevision ?? string.Empty,
                RequestId = context.CommandId ?? string.Empty,
            });
    }
}
