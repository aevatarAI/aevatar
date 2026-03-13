using Aevatar.Scripting.Abstractions.Queries;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Projection.Queries;

public interface IScriptReadModelQueryReader
{
    Task<ScriptReadModelSnapshot?> GetSnapshotAsync(
        string actorId,
        CancellationToken ct = default);

    Task<IReadOnlyList<ScriptReadModelSnapshot>> ListSnapshotsAsync(
        int take = 200,
        CancellationToken ct = default);

    Task<Any?> ExecuteDeclaredQueryAsync(
        string actorId,
        Any queryPayload,
        CancellationToken ct = default);
}
