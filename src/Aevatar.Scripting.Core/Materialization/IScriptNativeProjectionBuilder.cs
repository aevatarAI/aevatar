using Aevatar.Scripting.Abstractions;
using Google.Protobuf;

namespace Aevatar.Scripting.Core.Materialization;

public interface IScriptNativeProjectionBuilder
{
    ScriptNativeDocumentProjection? BuildDocument(
        IMessage? semanticReadModel,
        ScriptReadModelMaterializationPlan plan);

    ScriptNativeGraphProjection? BuildGraph(
        string actorId,
        string scriptId,
        string definitionActorId,
        string revision,
        IMessage? semanticReadModel,
        ScriptReadModelMaterializationPlan plan);
}
