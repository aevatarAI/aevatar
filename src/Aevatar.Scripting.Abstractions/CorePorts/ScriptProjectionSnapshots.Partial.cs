using Aevatar.Scripting.Abstractions;
using Google.Protobuf;

namespace Aevatar.Scripting.Core.Ports;

public sealed partial class ScriptDefinitionSnapshot
{
    public ScriptDefinitionSnapshot(
        string ScriptId,
        string Revision,
        string SourceText,
        string SourceHash,
        ScriptPackageSpec ScriptPackage,
        string StateTypeUrl,
        string ReadModelTypeUrl,
        string ReadModelSchemaVersion,
        string ReadModelSchemaHash,
        ByteString? ProtocolDescriptorSet = null,
        string StateDescriptorFullName = "",
        string ReadModelDescriptorFullName = "",
        ScriptRuntimeSemanticsSpec? RuntimeSemantics = null)
    {
        this.ScriptId = ScriptId ?? string.Empty;
        this.Revision = Revision ?? string.Empty;
        this.SourceText = SourceText ?? string.Empty;
        this.SourceHash = SourceHash ?? string.Empty;
        this.ScriptPackage = ScriptPackage?.Clone() ?? new ScriptPackageSpec();
        this.StateTypeUrl = StateTypeUrl ?? string.Empty;
        this.ReadModelTypeUrl = ReadModelTypeUrl ?? string.Empty;
        this.ReadModelSchemaVersion = ReadModelSchemaVersion ?? string.Empty;
        this.ReadModelSchemaHash = ReadModelSchemaHash ?? string.Empty;
        this.ProtocolDescriptorSet = ProtocolDescriptorSet ?? ByteString.Empty;
        this.StateDescriptorFullName = StateDescriptorFullName ?? string.Empty;
        this.ReadModelDescriptorFullName = ReadModelDescriptorFullName ?? string.Empty;
        this.RuntimeSemantics = RuntimeSemantics?.Clone() ?? new ScriptRuntimeSemanticsSpec();
    }

    public ScriptDefinitionSnapshot(
        string ScriptId,
        string Revision,
        string SourceText,
        string SourceHash,
        string StateTypeUrl,
        string ReadModelTypeUrl,
        string ReadModelSchemaVersion,
        string ReadModelSchemaHash,
        ByteString? ProtocolDescriptorSet = null,
        string StateDescriptorFullName = "",
        string ReadModelDescriptorFullName = "",
        ScriptRuntimeSemanticsSpec? RuntimeSemantics = null)
        : this(
            ScriptId,
            Revision,
            SourceText,
            SourceHash,
            ScriptPackageSpecExtensions.CreateSingleSource(SourceText),
            StateTypeUrl,
            ReadModelTypeUrl,
            ReadModelSchemaVersion,
            ReadModelSchemaHash,
            ProtocolDescriptorSet,
            StateDescriptorFullName,
            ReadModelDescriptorFullName,
            RuntimeSemantics)
    {
    }
}

public sealed partial class ScriptCatalogEntrySnapshot
{
    public ScriptCatalogEntrySnapshot(
        string ScriptId,
        string ActiveRevision,
        string ActiveDefinitionActorId,
        string ActiveSourceHash,
        string PreviousRevision,
        IEnumerable<string>? RevisionHistory,
        string LastProposalId)
    {
        this.ScriptId = ScriptId ?? string.Empty;
        this.ActiveRevision = ActiveRevision ?? string.Empty;
        this.ActiveDefinitionActorId = ActiveDefinitionActorId ?? string.Empty;
        this.ActiveSourceHash = ActiveSourceHash ?? string.Empty;
        this.PreviousRevision = PreviousRevision ?? string.Empty;
        if (RevisionHistory != null)
            this.RevisionHistory.Add(RevisionHistory);
        this.LastProposalId = LastProposalId ?? string.Empty;
    }
}
