using Aevatar.Scripting.Abstractions.Queries;

namespace Aevatar.Scripting.Application;

public interface IScriptExecutionQueryApplicationService
{
    Task<ScriptExecutionSnapshot?> GetRuntimeSnapshotAsync(
        string runtimeActorId,
        CancellationToken ct = default);
}
