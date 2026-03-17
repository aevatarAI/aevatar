using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Projection.ReadModels;

namespace Aevatar.Scripting.Projection.Materialization;

public interface IScriptNativeDocumentMaterializer
{
    ScriptNativeDocumentReadModel Materialize(
        string actorId,
        string scriptId,
        string definitionActorId,
        string revision,
        ScriptDomainFactCommitted fact,
        string sourceEventId,
        DateTimeOffset updatedAt,
        ScriptNativeDocumentProjection nativeDocument);
}
