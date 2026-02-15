using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Demos.CaseProjection.Reducers;

public abstract class CaseProjectionEventReducerBase<TEvent>
    : IProjectionEventReducer<CaseProjectionReadModel, CaseProjectionContext>
    where TEvent : class, IMessage<TEvent>, new()
{
    private static readonly string EventType = Any.Pack(new TEvent()).TypeUrl;

    public abstract int Order { get; }

    public string EventTypeUrl => EventType;

    public void Reduce(
        CaseProjectionReadModel readModel,
        CaseProjectionContext context,
        EventEnvelope envelope,
        DateTimeOffset now)
    {
        var payload = envelope.Payload;
        if (payload == null)
            return;

        if (!string.Equals(payload.TypeUrl, EventType, StringComparison.Ordinal))
            return;

        var evt = payload.Unpack<TEvent>();
        Reduce(readModel, context, envelope, evt, now);
    }

    protected abstract void Reduce(
        CaseProjectionReadModel readModel,
        CaseProjectionContext context,
        EventEnvelope envelope,
        TEvent evt,
        DateTimeOffset now);
}
