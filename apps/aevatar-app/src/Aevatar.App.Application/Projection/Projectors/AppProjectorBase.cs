using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;

namespace Aevatar.App.Application.Projection.Projectors;

public abstract class AppProjectorBase<TReadModel> : IProjectionProjector<AppProjectionContext, object?>
    where TReadModel : class, IProjectionReadModel
{
    private readonly IProjectionDocumentStore<TReadModel, string> _store;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<IProjectionEventReducer<TReadModel, AppProjectionContext>>> _reducersByType;

    protected AppProjectorBase(
        IProjectionDocumentStore<TReadModel, string> store,
        IEnumerable<IProjectionEventReducer<TReadModel, AppProjectionContext>> reducers)
    {
        _store = store;
        _reducersByType = reducers
            .GroupBy(x => x.EventTypeUrl, StringComparer.Ordinal)
            .ToDictionary(
                x => x.Key,
                x => (IReadOnlyList<IProjectionEventReducer<TReadModel, AppProjectionContext>>)x.ToList(),
                StringComparer.Ordinal);
    }

    protected abstract string ActorPrefix { get; }

    protected abstract TReadModel CreateInitialReadModel(string actorId);

    public async ValueTask InitializeAsync(AppProjectionContext context, CancellationToken ct = default)
    {
        if (!context.ActorId.StartsWith(ActorPrefix, StringComparison.Ordinal))
            return;
        var existing = await _store.GetAsync(context.ActorId, ct);
        if (existing is not null)
            return;
        var model = CreateInitialReadModel(context.ActorId);
        await _store.UpsertAsync(model, ct);
    }

    public ValueTask ProjectAsync(AppProjectionContext context, EventEnvelope envelope, CancellationToken ct = default)
    {
        if (!context.ActorId.StartsWith(ActorPrefix, StringComparison.Ordinal))
            return ValueTask.CompletedTask;
        var typeUrl = envelope.Payload?.TypeUrl;
        if (string.IsNullOrWhiteSpace(typeUrl)) return ValueTask.CompletedTask;
        if (!_reducersByType.TryGetValue(typeUrl, out var reducers)) return ValueTask.CompletedTask;
        if (!string.IsNullOrWhiteSpace(envelope.Id) && !context.TryMarkProcessed(envelope.Id))
            return ValueTask.CompletedTask;

        var now = ResolveTimestamp(envelope);
        return new ValueTask(_store.MutateAsync(context.ActorId, model =>
        {
            foreach (var reducer in reducers)
                reducer.Reduce(model, context, envelope, now);
        }, ct));
    }

    public ValueTask CompleteAsync(AppProjectionContext context, object? topology, CancellationToken ct = default)
        => ValueTask.CompletedTask;

    private static DateTimeOffset ResolveTimestamp(EventEnvelope envelope)
    {
        if (envelope.Timestamp == null) return DateTimeOffset.UtcNow;
        var dt = envelope.Timestamp.ToDateTime();
        if (dt.Kind != DateTimeKind.Utc) dt = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
        return new DateTimeOffset(dt);
    }
}
