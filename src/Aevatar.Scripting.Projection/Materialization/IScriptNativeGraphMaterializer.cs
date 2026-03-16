using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Core.Materialization;
using Aevatar.Scripting.Projection.ReadModels;
using Google.Protobuf;

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
        IMessage? semanticReadModel,
        ScriptReadModelMaterializationPlan plan);
}
