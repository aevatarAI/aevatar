using Aevatar.Foundation.Projection.ReadModels;

namespace Aevatar.Scripting.Projection.ReadModels;

public sealed class ScriptDefinitionSnapshotDocument
    : AevatarReadModelBase,
      IProjectionReadModel,
      IProjectionReadModelCloneable<ScriptDefinitionSnapshotDocument>
{
    public string ScriptId { get; set; } = string.Empty;
    public string DefinitionActorId { get; set; } = string.Empty;
    public string Revision { get; set; } = string.Empty;
    public string SourceText { get; set; } = string.Empty;
    public string SourceHash { get; set; } = string.Empty;
    public string StateTypeUrl { get; set; } = string.Empty;
    public string ReadModelTypeUrl { get; set; } = string.Empty;
    public string ReadModelSchemaVersion { get; set; } = string.Empty;
    public string ReadModelSchemaHash { get; set; } = string.Empty;
    public string ScriptPackageBase64 { get; set; } = string.Empty;
    public string ProtocolDescriptorSetBase64 { get; set; } = string.Empty;
    public string StateDescriptorFullName { get; set; } = string.Empty;
    public string ReadModelDescriptorFullName { get; set; } = string.Empty;
    public string RuntimeSemanticsBase64 { get; set; } = string.Empty;

    public ScriptDefinitionSnapshotDocument DeepClone() =>
        new()
        {
            Id = Id,
            StateVersion = StateVersion,
            LastEventId = LastEventId,
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            ScriptId = ScriptId,
            DefinitionActorId = DefinitionActorId,
            Revision = Revision,
            SourceText = SourceText,
            SourceHash = SourceHash,
            StateTypeUrl = StateTypeUrl,
            ReadModelTypeUrl = ReadModelTypeUrl,
            ReadModelSchemaVersion = ReadModelSchemaVersion,
            ReadModelSchemaHash = ReadModelSchemaHash,
            ScriptPackageBase64 = ScriptPackageBase64,
            ProtocolDescriptorSetBase64 = ProtocolDescriptorSetBase64,
            StateDescriptorFullName = StateDescriptorFullName,
            ReadModelDescriptorFullName = ReadModelDescriptorFullName,
            RuntimeSemanticsBase64 = RuntimeSemanticsBase64,
        };
}
