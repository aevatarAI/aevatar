using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Application;
using Aevatar.Scripting.Core;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class UpsertScriptDefinitionCommandEnvelopeFactory
    : ICommandEnvelopeFactory<UpsertScriptDefinitionCommand>
{
    public EventEnvelope CreateEnvelope(
        UpsertScriptDefinitionCommand command,
        CommandContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        return ScriptingActorRequestEnvelopeFactory.Create(
            context.TargetId,
            context.CorrelationId,
            new UpsertScriptDefinitionRequestedEvent
            {
                ScriptId = command.ScriptId ?? string.Empty,
                ScriptRevision = command.ScriptRevision ?? string.Empty,
                SourceText = command.SourceText ?? string.Empty,
                SourceHash = command.SourceHash ?? string.Empty,
                ScopeId = command.ScopeId ?? string.Empty,
            });
    }
}
