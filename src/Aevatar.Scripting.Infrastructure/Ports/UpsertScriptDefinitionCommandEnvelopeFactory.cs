using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Application;
using Aevatar.Scripting.Core;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class UpsertScriptDefinitionCommandEnvelopeFactory
    : ICommandEnvelopeFactory<UpsertScriptDefinitionCommand>
{
    // Refactor (iter42/cluster-044-scripting-source-package-json-shadow):
    //   Old pattern: Scripting persists and republishes source_text as a compatibility shadow of ScriptPackageSpec; multi-file packages can be encoded as JSON text and reparsed from persisted source.
    //   New principle: ScriptPackageSpec is the sole internal source-package contract for commands/state/events/readmodels; source_text is only an external one-file adapter field at Host/Application boundary.
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
                SourceHash = command.SourceHash ?? string.Empty,
                ScriptPackage = command.ScriptPackage?.Clone() ?? new ScriptPackageSpec(),
                ScopeId = command.ScopeId ?? string.Empty,
            });
    }
}
