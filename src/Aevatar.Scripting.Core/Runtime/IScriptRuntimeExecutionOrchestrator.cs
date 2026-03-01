using Google.Protobuf;

namespace Aevatar.Scripting.Core.Runtime;

public interface IScriptRuntimeExecutionOrchestrator
{
    Task<IReadOnlyList<IMessage>> ExecuteRunAsync(
        ScriptRuntimeExecutionRequest request,
        CancellationToken ct);
}
