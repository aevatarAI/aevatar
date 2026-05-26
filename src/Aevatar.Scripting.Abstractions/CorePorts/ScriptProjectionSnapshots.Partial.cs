using Aevatar.Scripting.Abstractions;
using Google.Protobuf;

namespace Aevatar.Scripting.Core.Ports;

public sealed partial class ScriptDefinitionSnapshot
{
    // Refactor (iter42/cluster-044-scripting-source-package-json-shadow):
    //   Old pattern: Scripting persists and republishes source_text as a compatibility shadow of ScriptPackageSpec; multi-file packages can be encoded as JSON text and reparsed from persisted source.
    //   New principle: ScriptPackageSpec is the sole internal source-package contract for commands/state/events/readmodels; source_text is only an external one-file adapter field at Host/Application boundary.
    public ScriptDefinitionSnapshot(
        string ScriptId,
        string Revision,
        string SourceHash,
        ScriptPackageSpec ScriptPackage,
        string StateTypeUrl,
        string ReadModelTypeUrl,
        string ReadModelSchemaVersion,
        string ReadModelSchemaHash,
        ByteString? ProtocolDescriptorSet = null,
        string StateDescriptorFullName = "",
        string ReadModelDescriptorFullName = "",
        ScriptRuntimeSemanticsSpec? RuntimeSemantics = null,
        string DefinitionActorId = "",
        string ScopeId = "")
    {
        this.ScriptId = ScriptId ?? string.Empty;
        this.Revision = Revision ?? string.Empty;
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
        this.DefinitionActorId = DefinitionActorId ?? string.Empty;
        this.ScopeId = ScopeId ?? string.Empty;
    }

    public string SourceText => ScriptPackage.GetPrimaryCSharpSource();

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
        ScriptRuntimeSemanticsSpec? RuntimeSemantics = null,
        string DefinitionActorId = "",
        string ScopeId = "")
        : this(
            ScriptId,
            Revision,
            SourceHash,
            ScriptPackage,
            StateTypeUrl,
            ReadModelTypeUrl,
            ReadModelSchemaVersion,
            ReadModelSchemaHash,
            ProtocolDescriptorSet,
            StateDescriptorFullName,
            ReadModelDescriptorFullName,
            RuntimeSemantics,
            DefinitionActorId,
            ScopeId)
    {
        _ = SourceText;
    }

    public ScriptDefinitionSnapshot(
        string ScriptId,
        string Revision,
        string SourceHash,
        string StateTypeUrl,
        string ReadModelTypeUrl,
        string ReadModelSchemaVersion,
        string ReadModelSchemaHash,
        ByteString? ProtocolDescriptorSet = null,
        string StateDescriptorFullName = "",
        string ReadModelDescriptorFullName = "",
        ScriptRuntimeSemanticsSpec? RuntimeSemantics = null,
        string DefinitionActorId = "",
        string ScopeId = "")
        : this(
            ScriptId,
            Revision,
            SourceHash,
            new ScriptPackageSpec(),
            StateTypeUrl,
            ReadModelTypeUrl,
            ReadModelSchemaVersion,
            ReadModelSchemaHash,
            ProtocolDescriptorSet,
            StateDescriptorFullName,
            ReadModelDescriptorFullName,
            RuntimeSemantics,
            DefinitionActorId,
            ScopeId)
    {
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
        ScriptRuntimeSemanticsSpec? RuntimeSemantics = null,
        string DefinitionActorId = "",
        string ScopeId = "")
        : this(
            ScriptId,
            Revision,
            SourceHash,
            ScriptPackageSpecExtensions.CreateSingleSource(SourceText),
            StateTypeUrl,
            ReadModelTypeUrl,
            ReadModelSchemaVersion,
            ReadModelSchemaHash,
            ProtocolDescriptorSet,
            StateDescriptorFullName,
            ReadModelDescriptorFullName,
            RuntimeSemantics,
            DefinitionActorId,
            ScopeId)
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
        string LastProposalId,
        string CatalogActorId = "",
        string ScopeId = "",
        long UpdatedAtUnixTimeMs = 0)
    {
        this.ScriptId = ScriptId ?? string.Empty;
        this.ActiveRevision = ActiveRevision ?? string.Empty;
        this.ActiveDefinitionActorId = ActiveDefinitionActorId ?? string.Empty;
        this.ActiveSourceHash = ActiveSourceHash ?? string.Empty;
        this.PreviousRevision = PreviousRevision ?? string.Empty;
        if (RevisionHistory != null)
            this.RevisionHistory.Add(RevisionHistory);
        this.LastProposalId = LastProposalId ?? string.Empty;
        this.CatalogActorId = CatalogActorId ?? string.Empty;
        this.ScopeId = ScopeId ?? string.Empty;
        this.UpdatedAtUnixTimeMs = UpdatedAtUnixTimeMs;
    }
}
