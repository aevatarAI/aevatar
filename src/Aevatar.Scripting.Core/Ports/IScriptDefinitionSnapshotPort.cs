using Aevatar.Scripting.Abstractions;
using Google.Protobuf;

namespace Aevatar.Scripting.Core.Ports;

public sealed partial record ScriptDefinitionSnapshot(
    string ScriptId,
    string Revision,
    string SourceText,
    string SourceHash,
    ScriptPackageSpec ScriptPackage,
    string StateTypeUrl,
    string ReadModelTypeUrl,
    string ReadModelSchemaVersion,
    string ReadModelSchemaHash,
    ByteString ProtocolDescriptorSet,
    string StateDescriptorFullName,
    string ReadModelDescriptorFullName);

public sealed partial record ScriptDefinitionSnapshot
{
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
        string ReadModelDescriptorFullName = "")
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
            ProtocolDescriptorSet ?? ByteString.Empty,
            StateDescriptorFullName,
            ReadModelDescriptorFullName)
    {
    }
}

public interface IScriptDefinitionSnapshotPort
{
    Task<ScriptDefinitionSnapshot> GetRequiredAsync(
        string definitionActorId,
        string requestedRevision,
        CancellationToken ct);
}
