using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Abstractions.Queries;

public interface IScriptReadModelQueryPort
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
