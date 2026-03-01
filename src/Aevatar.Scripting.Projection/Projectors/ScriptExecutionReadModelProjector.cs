using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Scripting.Projection.Orchestration;
using Aevatar.Scripting.Projection.ReadModels;

namespace Aevatar.Scripting.Projection.Projectors;

public sealed class ScriptExecutionReadModelProjector
    : IProjectionProjector<ScriptProjectionContext, IReadOnlyList<string>>
{
    private readonly IProjectionStoreDispatcher<ScriptExecutionReadModel, string> _storeDispatcher;
    private readonly IProjectionClock _clock;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<IProjectionEventReducer<ScriptExecutionReadModel, ScriptProjectionContext>>> _reducersByType;

    public ScriptExecutionReadModelProjector(
        IProjectionStoreDispatcher<ScriptExecutionReadModel, string> storeDispatcher,
        IProjectionClock clock,
        IEnumerable<IProjectionEventReducer<ScriptExecutionReadModel, ScriptProjectionContext>> reducers)
    {
        _storeDispatcher = storeDispatcher;
        _clock = clock;
        _reducersByType = reducers
            .GroupBy(x => x.EventTypeUrl, StringComparer.Ordinal)
            .ToDictionary(
                x => x.Key,
                x => (IReadOnlyList<IProjectionEventReducer<ScriptExecutionReadModel, ScriptProjectionContext>>)x.ToList(),
                StringComparer.Ordinal);
    }

    public ValueTask InitializeAsync(
        ScriptProjectionContext context,
        CancellationToken ct = default)
    {
        var now = _clock.UtcNow;
        var readModel = new ScriptExecutionReadModel
        {
            Id = context.RootActorId,
            ScriptId = context.ScriptId,
            UpdatedAt = now,
        };
        return new ValueTask(_storeDispatcher.UpsertAsync(readModel, ct));
    }

    public async ValueTask ProjectAsync(
        ScriptProjectionContext context,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        var payload = envelope.Payload;
        if (payload == null)
            return;
        var typeUrl = payload.TypeUrl;

        if (!_reducersByType.TryGetValue(typeUrl, out var reducers))
            return;

        var now = ResolveEventTimestamp(envelope, _clock.UtcNow);
        await _storeDispatcher.MutateAsync(context.RootActorId, readModel =>
        {
            var mutated = false;
            foreach (var reducer in reducers)
                mutated |= reducer.Reduce(readModel, context, envelope, now);

            if (!mutated)
                return;

            readModel.UpdatedAt = now;
            readModel.LastEventId = envelope.Id ?? string.Empty;
        }, ct);
    }

    public ValueTask CompleteAsync(
        ScriptProjectionContext context,
        IReadOnlyList<string> topology,
        CancellationToken ct = default)
    {
        _ = context;
        _ = topology;
        _ = ct;
        return ValueTask.CompletedTask;
    }

    private static DateTimeOffset ResolveEventTimestamp(
        EventEnvelope envelope,
        DateTimeOffset fallbackUtcNow)
    {
        var ts = envelope.Timestamp;
        if (ts == null)
            return fallbackUtcNow;

        var dt = ts.ToDateTime();
        if (dt.Kind != DateTimeKind.Utc)
            dt = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
        return new DateTimeOffset(dt);
    }
}
