using Aevatar.Scripting.Abstractions.Queries;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Projection.Queries;

public sealed class ScriptReadModelQueryService : IScriptReadModelQueryPort
{
    private readonly IScriptReadModelQueryReader _reader;

    public ScriptReadModelQueryService(IScriptReadModelQueryReader reader)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    public Task<ScriptReadModelSnapshot?> GetSnapshotAsync(
        string actorId,
        CancellationToken ct = default) =>
        _reader.GetSnapshotAsync(actorId, ct);

    public Task<IReadOnlyList<ScriptReadModelSnapshot>> ListSnapshotsAsync(
        int take = 200,
        CancellationToken ct = default) =>
        _reader.ListSnapshotsAsync(take, ct);

    public Task<Any?> ExecuteDeclaredQueryAsync(
        string actorId,
        Any queryPayload,
        CancellationToken ct = default) =>
        _reader.ExecuteDeclaredQueryAsync(actorId, queryPayload, ct);
}
