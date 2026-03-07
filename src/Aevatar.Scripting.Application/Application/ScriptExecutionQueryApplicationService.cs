using Aevatar.Scripting.Abstractions.Queries;

namespace Aevatar.Scripting.Application;

public sealed class ScriptExecutionQueryApplicationService : IScriptExecutionQueryApplicationService
{
    private readonly IScriptExecutionProjectionQueryPort _queryPort;

    public ScriptExecutionQueryApplicationService(IScriptExecutionProjectionQueryPort queryPort)
    {
        _queryPort = queryPort ?? throw new ArgumentNullException(nameof(queryPort));
    }

    public Task<ScriptExecutionSnapshot?> GetRuntimeSnapshotAsync(
        string runtimeActorId,
        CancellationToken ct = default) =>
        _queryPort.GetRuntimeSnapshotAsync(runtimeActorId, ct);
}
