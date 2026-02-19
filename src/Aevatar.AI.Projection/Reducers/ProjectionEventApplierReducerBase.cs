using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.AI.Projection.Reducers;

/// <summary>
/// Generic reducer that unpacks one protobuf event and fan-outs to registered appliers.
/// </summary>
public abstract class ProjectionEventApplierReducerBase<TReadModel, TContext, TEvent>
    : IProjectionEventReducer<TReadModel, TContext>
    where TEvent : class, IMessage<TEvent>, new()
{
    private static readonly string EventType = Any.Pack(new TEvent()).TypeUrl;
    private readonly IReadOnlyList<IProjectionEventApplier<TReadModel, TContext, TEvent>> _appliers;

    protected ProjectionEventApplierReducerBase(
        IEnumerable<IProjectionEventApplier<TReadModel, TContext, TEvent>> appliers)
    {
        _appliers = appliers.ToList();
    }

    public string EventTypeUrl => EventType;

    public bool Reduce(
        TReadModel readModel,
        TContext context,
        EventEnvelope envelope,
        DateTimeOffset now)
    {
        if (_appliers.Count == 0)
            return false;

        var payload = envelope.Payload;
        if (payload == null || !string.Equals(payload.TypeUrl, EventType, StringComparison.Ordinal))
            return false;

        var evt = payload.Unpack<TEvent>();
        var mutated = false;
        foreach (var applier in _appliers)
            mutated |= applier.Apply(readModel, context, envelope, evt, now);

        return mutated;
    }
}
