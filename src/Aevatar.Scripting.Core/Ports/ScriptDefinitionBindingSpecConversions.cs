using Aevatar.Scripting.Abstractions;

namespace Aevatar.Scripting.Core.Ports;

public static class ScriptDefinitionBindingSpecConversions
{
    // Refactor (iter42/cluster-044-scripting-source-package-json-shadow):
    //   Old pattern: Scripting persists and republishes source_text as a compatibility shadow of ScriptPackageSpec; multi-file packages can be encoded as JSON text and reparsed from persisted source.
    //   New principle: ScriptPackageSpec is the sole internal source-package contract for commands/state/events/readmodels; source_text is only an external one-file adapter field at Host/Application boundary.
    public static ScriptDefinitionBindingSpec ToBindingSpec(this ScriptDefinitionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return new ScriptDefinitionBindingSpec
        {
            ScriptId = snapshot.ScriptId,
            Revision = snapshot.Revision,
            SourceHash = snapshot.SourceHash,
            ScriptPackage = snapshot.ScriptPackage?.Clone() ?? new ScriptPackageSpec(),
            StateTypeUrl = snapshot.StateTypeUrl,
            ReadModelTypeUrl = snapshot.ReadModelTypeUrl,
            ReadModelSchemaVersion = snapshot.ReadModelSchemaVersion,
            ReadModelSchemaHash = snapshot.ReadModelSchemaHash,
            ProtocolDescriptorSet = snapshot.ProtocolDescriptorSet,
            StateDescriptorFullName = snapshot.StateDescriptorFullName,
            ReadModelDescriptorFullName = snapshot.ReadModelDescriptorFullName,
            RuntimeSemantics = snapshot.RuntimeSemantics?.Clone() ?? new ScriptRuntimeSemanticsSpec(),
        };
    }

    public static ScriptDefinitionSnapshot? ToSnapshot(this ScriptDefinitionBindingSpec? spec)
    {
        if (spec == null)
            return null;

        return new ScriptDefinitionSnapshot(
            spec.ScriptId ?? string.Empty,
            spec.Revision ?? string.Empty,
            spec.SourceHash ?? string.Empty,
            spec.ScriptPackage?.Clone() ?? new ScriptPackageSpec(),
            spec.StateTypeUrl ?? string.Empty,
            spec.ReadModelTypeUrl ?? string.Empty,
            spec.ReadModelSchemaVersion ?? string.Empty,
            spec.ReadModelSchemaHash ?? string.Empty,
            spec.ProtocolDescriptorSet,
            spec.StateDescriptorFullName ?? string.Empty,
            spec.ReadModelDescriptorFullName ?? string.Empty,
            spec.RuntimeSemantics?.Clone() ?? new ScriptRuntimeSemanticsSpec());
    }
}
