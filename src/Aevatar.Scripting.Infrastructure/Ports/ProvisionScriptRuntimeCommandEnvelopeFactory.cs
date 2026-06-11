using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Application;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class ProvisionScriptRuntimeCommandEnvelopeFactory
    : ICommandEnvelopeFactory<ProvisionScriptRuntimeCommand>
{
    // Refactor (iter42/cluster-044-scripting-source-package-json-shadow):
    //   Old pattern: Scripting persists and republishes source_text as a compatibility shadow of ScriptPackageSpec; multi-file packages can be encoded as JSON text and reparsed from persisted source.
    //   New principle: ScriptPackageSpec is the sole internal source-package contract for commands/state/events/readmodels; source_text is only an external one-file adapter field at Host/Application boundary.
    public EventEnvelope CreateEnvelope(
        ProvisionScriptRuntimeCommand command,
        CommandContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        return ScriptingActorRequestEnvelopeFactory.Create(
            context.TargetId,
            context.CorrelationId,
            new BindScriptBehaviorRequestedEvent
            {
                DefinitionActorId = command.DefinitionActorId ?? string.Empty,
                ScriptId = command.DefinitionSnapshot.ScriptId,
                Revision = command.DefinitionSnapshot.Revision,
                SourceHash = command.DefinitionSnapshot.SourceHash,
                StateTypeUrl = command.DefinitionSnapshot.StateTypeUrl,
                ReadModelTypeUrl = command.DefinitionSnapshot.ReadModelTypeUrl,
                ReadModelSchemaVersion = command.DefinitionSnapshot.ReadModelSchemaVersion,
                ReadModelSchemaHash = command.DefinitionSnapshot.ReadModelSchemaHash,
                ScriptPackage = command.DefinitionSnapshot.ScriptPackage?.Clone() ?? new ScriptPackageSpec(),
                ProtocolDescriptorSet = command.DefinitionSnapshot.ProtocolDescriptorSet,
                StateDescriptorFullName = command.DefinitionSnapshot.StateDescriptorFullName,
                ReadModelDescriptorFullName = command.DefinitionSnapshot.ReadModelDescriptorFullName,
                RuntimeSemantics = command.DefinitionSnapshot.RuntimeSemantics?.Clone() ?? new ScriptRuntimeSemanticsSpec(),
                ScopeId = command.ScopeId ?? command.DefinitionSnapshot.ScopeId ?? string.Empty,
            });
    }
}
