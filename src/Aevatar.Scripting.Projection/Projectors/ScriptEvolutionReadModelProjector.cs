using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Projection.Orchestration;
using Aevatar.Scripting.Projection.ReadModels;

namespace Aevatar.Scripting.Projection.Projectors;

public sealed class ScriptEvolutionReadModelProjector
    : IProjectionProjector<ScriptEvolutionSessionProjectionContext, IReadOnlyList<string>>
{
    private readonly IProjectionStoreDispatcher<ScriptEvolutionReadModel, string> _storeDispatcher;
    private readonly IProjectionClock _clock;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<IProjectionEventReducer<ScriptEvolutionReadModel, ScriptEvolutionSessionProjectionContext>>> _reducersByType;

    public ScriptEvolutionReadModelProjector(
        IProjectionStoreDispatcher<ScriptEvolutionReadModel, string> storeDispatcher,
        IProjectionClock clock,
        IEnumerable<IProjectionEventReducer<ScriptEvolutionReadModel, ScriptEvolutionSessionProjectionContext>> reducers)
    {
        _storeDispatcher = storeDispatcher;
        _clock = clock;
        _reducersByType = reducers
            .GroupBy(x => x.EventTypeUrl, StringComparer.Ordinal)
            .ToDictionary(
                x => x.Key,
                x => (IReadOnlyList<IProjectionEventReducer<ScriptEvolutionReadModel, ScriptEvolutionSessionProjectionContext>>)x.ToList(),
                StringComparer.Ordinal);
    }

    public ValueTask InitializeAsync(
        ScriptEvolutionSessionProjectionContext context,
        CancellationToken ct = default)
    {
        _ = context;
        ct.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public async ValueTask ProjectAsync(
        ScriptEvolutionSessionProjectionContext context,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        var payload = envelope.Payload;
        if (payload == null)
            return;
        var readModelId = ResolveReadModelId(context, payload);
        if (string.IsNullOrWhiteSpace(readModelId))
            return;

        var typeUrl = payload.TypeUrl;
        if (!_reducersByType.TryGetValue(typeUrl, out var reducers))
            return;

        var now = ProjectionEnvelopeTimestampResolver.Resolve(envelope, _clock.UtcNow);
        await _storeDispatcher.MutateAsync(readModelId, readModel =>
        {
            if (string.IsNullOrWhiteSpace(readModel.Id))
                readModel.Id = readModelId;

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
        ScriptEvolutionSessionProjectionContext context,
        IReadOnlyList<string> topology,
        CancellationToken ct = default)
    {
        _ = context;
        _ = topology;
        _ = ct;
        return ValueTask.CompletedTask;
    }

    private static string ResolveReadModelId(
        ScriptEvolutionSessionProjectionContext context,
        Google.Protobuf.WellKnownTypes.Any payload)
    {
        if (payload.Is(ScriptEvolutionProposedEvent.Descriptor))
            return ResolveProposalId(payload.Unpack<ScriptEvolutionProposedEvent>().ProposalId, context.ProposalId);
        if (payload.Is(ScriptEvolutionValidatedEvent.Descriptor))
            return ResolveProposalId(payload.Unpack<ScriptEvolutionValidatedEvent>().ProposalId, context.ProposalId);
        if (payload.Is(ScriptEvolutionRejectedEvent.Descriptor))
            return ResolveProposalId(payload.Unpack<ScriptEvolutionRejectedEvent>().ProposalId, context.ProposalId);
        if (payload.Is(ScriptEvolutionPromotedEvent.Descriptor))
            return ResolveProposalId(payload.Unpack<ScriptEvolutionPromotedEvent>().ProposalId, context.ProposalId);
        if (payload.Is(ScriptEvolutionRolledBackEvent.Descriptor))
            return ResolveProposalId(payload.Unpack<ScriptEvolutionRolledBackEvent>().ProposalId, context.ProposalId);

        return string.Empty;
    }

    private static string ResolveProposalId(string? proposalId, string fallbackProposalId) =>
        string.IsNullOrWhiteSpace(proposalId)
            ? fallbackProposalId ?? string.Empty
            : proposalId;

}
