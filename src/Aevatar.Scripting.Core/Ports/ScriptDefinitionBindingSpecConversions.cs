using Aevatar.Scripting.Abstractions;

namespace Aevatar.Scripting.Core.Ports;

public static class ScriptDefinitionBindingSpecConversions
{
    public static ScriptDefinitionBindingSpec ToBindingSpec(this ScriptDefinitionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return new ScriptDefinitionBindingSpec
        {
            ScriptId = snapshot.ScriptId,
            Revision = snapshot.Revision,
            SourceText = snapshot.SourceText,
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
            spec.SourceText ?? string.Empty,
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
