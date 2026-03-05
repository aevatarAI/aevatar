using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.App.Application.Projection.Reducers;

public abstract class AppEventReducerBase<TReadModel, TEvent>
    : IProjectionEventReducer<TReadModel, AppProjectionContext>
    where TReadModel : class, IProjectionReadModel
    where TEvent : class, IMessage<TEvent>, new()
{
    private static readonly string EventType = Any.Pack(new TEvent()).TypeUrl;

    public string EventTypeUrl => EventType;

    public bool Reduce(
        TReadModel readModel,
        AppProjectionContext context,
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
        TReadModel readModel,
        AppProjectionContext context,
        EventEnvelope envelope,
        TEvent evt,
        DateTimeOffset now);
}
