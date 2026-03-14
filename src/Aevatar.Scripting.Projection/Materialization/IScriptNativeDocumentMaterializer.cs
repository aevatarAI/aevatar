using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Core.Materialization;
using Aevatar.Scripting.Projection.ReadModels;
using Google.Protobuf;

namespace Aevatar.Scripting.Projection.Materialization;

public interface IScriptNativeDocumentMaterializer
{
    ScriptNativeDocumentReadModel Materialize(
        string actorId,
        string scriptId,
        string definitionActorId,
        string revision,
        ScriptDomainFactCommitted fact,
        IMessage? semanticReadModel,
        ScriptReadModelMaterializationPlan plan);
}
