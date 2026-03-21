using Aevatar.Scripting.Abstractions.Queries;

namespace Aevatar.Scripting.Application.Queries;

public interface IScriptReadModelQueryApplicationService
{
    Task<ScriptReadModelSnapshot?> GetSnapshotAsync(
        string actorId,
        CancellationToken ct = default);

    Task<IReadOnlyList<ScriptReadModelSnapshot>> ListSnapshotsAsync(
        int take = 200,
        CancellationToken ct = default);
}
