using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Demos.CaseProjection.Reducers;

public abstract class CaseProjectionEventReducerBase<TEvent>
    : IProjectionEventReducer<CaseProjectionReadModel, CaseProjectionContext>
    where TEvent : class, IMessage<TEvent>, new()
{
    private static readonly string EventType = Any.Pack(new TEvent()).TypeUrl;

    public string EventTypeUrl => EventType;

    public bool Reduce(
        CaseProjectionReadModel readModel,
        CaseProjectionContext context,
        EventEnvelope envelope,
        DateTimeOffset now)
    {
        var payload = envelope.Payload;
        if (payload == null)
            return false;

        if (!string.Equals(payload.TypeUrl, EventType, StringComparison.Ordinal))
            return false;

        var evt = payload.Unpack<TEvent>();
        return Reduce(readModel, context, envelope, evt, now);
    }

    protected abstract bool Reduce(
        CaseProjectionReadModel readModel,
        CaseProjectionContext context,
        EventEnvelope envelope,
        TEvent evt,
        DateTimeOffset now);
}
