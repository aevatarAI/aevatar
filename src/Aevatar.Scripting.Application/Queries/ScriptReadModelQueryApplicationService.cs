using Aevatar.Scripting.Abstractions.Queries;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Application.Queries;

public sealed class ScriptReadModelQueryApplicationService : IScriptReadModelQueryApplicationService
{
    private readonly IScriptReadModelQueryPort _queryPort;

    public ScriptReadModelQueryApplicationService(IScriptReadModelQueryPort queryPort)
    {
        _queryPort = queryPort ?? throw new ArgumentNullException(nameof(queryPort));
    }

    public Task<ScriptReadModelSnapshot?> GetSnapshotAsync(
        string actorId,
        CancellationToken ct = default) =>
        _queryPort.GetSnapshotAsync(actorId, ct);

    public Task<IReadOnlyList<ScriptReadModelSnapshot>> ListSnapshotsAsync(
        int take = 200,
        CancellationToken ct = default) =>
        _queryPort.ListSnapshotsAsync(take, ct);

    public Task<Any?> ExecuteDeclaredQueryAsync(
        string actorId,
        Any queryPayload,
        CancellationToken ct = default) =>
        _queryPort.ExecuteDeclaredQueryAsync(actorId, queryPayload, ct);
}
