using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Projection.ReadModels;

namespace Aevatar.Scripting.Projection.Materialization;

public interface IScriptNativeGraphMaterializer
{
    ScriptNativeGraphReadModel Materialize(
        string actorId,
        string scriptId,
        string definitionActorId,
        string revision,
        ScriptDomainFactCommitted fact,
        string sourceEventId,
        DateTimeOffset updatedAt,
        ScriptNativeGraphProjection nativeGraph);
}
