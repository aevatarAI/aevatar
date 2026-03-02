using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Projection.Orchestration;
using Aevatar.Scripting.Projection.ReadModels;

namespace Aevatar.Scripting.Projection.Projectors;

public sealed class ScriptEvolutionReadModelProjector
    : IProjectionProjector<ScriptEvolutionProjectionContext, IReadOnlyList<string>>
{
    private readonly IProjectionStoreDispatcher<ScriptEvolutionReadModel, string> _storeDispatcher;
    private readonly IProjectionClock _clock;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<IProjectionEventReducer<ScriptEvolutionReadModel, ScriptEvolutionProjectionContext>>> _reducersByType;

    public ScriptEvolutionReadModelProjector(
        IProjectionStoreDispatcher<ScriptEvolutionReadModel, string> storeDispatcher,
        IProjectionClock clock,
        IEnumerable<IProjectionEventReducer<ScriptEvolutionReadModel, ScriptEvolutionProjectionContext>> reducers)
    {
        _storeDispatcher = storeDispatcher;
        _clock = clock;
        _reducersByType = reducers
            .GroupBy(x => x.EventTypeUrl, StringComparer.Ordinal)
            .ToDictionary(
                x => x.Key,
                x => (IReadOnlyList<IProjectionEventReducer<ScriptEvolutionReadModel, ScriptEvolutionProjectionContext>>)x.ToList(),
                StringComparer.Ordinal);
    }

    public ValueTask InitializeAsync(
        ScriptEvolutionProjectionContext context,
        CancellationToken ct = default)
    {
        _ = context;
        ct.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public async ValueTask ProjectAsync(
        ScriptEvolutionProjectionContext context,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        var payload = envelope.Payload;
        if (payload == null)
            return;
        var readModelId = ResolveReadModelId(payload);
        if (string.IsNullOrWhiteSpace(readModelId))
            return;

        var typeUrl = payload.TypeUrl;
        if (!_reducersByType.TryGetValue(typeUrl, out var reducers))
            return;

        var now = ResolveEventTimestamp(envelope, _clock.UtcNow);
        await _storeDispatcher.MutateAsync(readModelId, readModel =>
        {
            var mutated = false;
            foreach (var reducer in reducers)
                mutated |= reducer.Reduce(readModel, context, envelope, now);

            if (!mutated)
                return;

            readModel.LastEventId = envelope.Id ?? string.Empty;
            readModel.UpdatedAt = now;
        }, ct);
    }

    public ValueTask CompleteAsync(
        ScriptEvolutionProjectionContext context,
        IReadOnlyList<string> topology,
        CancellationToken ct = default)
    {
        _ = context;
        _ = topology;
        _ = ct;
        return ValueTask.CompletedTask;
    }

    private static string ResolveReadModelId(Google.Protobuf.WellKnownTypes.Any payload)
    {
        if (payload.Is(ScriptEvolutionProposedEvent.Descriptor))
            return payload.Unpack<ScriptEvolutionProposedEvent>().ProposalId ?? string.Empty;
        if (payload.Is(ScriptEvolutionValidatedEvent.Descriptor))
            return payload.Unpack<ScriptEvolutionValidatedEvent>().ProposalId ?? string.Empty;
        if (payload.Is(ScriptEvolutionRejectedEvent.Descriptor))
            return payload.Unpack<ScriptEvolutionRejectedEvent>().ProposalId ?? string.Empty;
        if (payload.Is(ScriptEvolutionPromotedEvent.Descriptor))
            return payload.Unpack<ScriptEvolutionPromotedEvent>().ProposalId ?? string.Empty;
        if (payload.Is(ScriptEvolutionRolledBackEvent.Descriptor))
            return payload.Unpack<ScriptEvolutionRolledBackEvent>().ProposalId ?? string.Empty;

        return string.Empty;
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
