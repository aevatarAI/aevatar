using Aevatar.Scripting.Projection.Orchestration;
using Aevatar.Scripting.Projection.ReadModels;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Projection.Reducers;

public abstract class ScriptEventReducerBase<TEvent>
    : IProjectionEventReducer<ScriptExecutionReadModel, ScriptProjectionContext>
    where TEvent : class, IMessage<TEvent>, new()
{
    private static readonly string EventType = Any.Pack(new TEvent()).TypeUrl;

    public string EventTypeUrl => EventType;

    public bool Reduce(
        ScriptExecutionReadModel readModel,
        ScriptProjectionContext context,
        EventEnvelope envelope,
        DateTimeOffset now)
    {
        var payload = envelope.Payload;
        if (payload == null)
            return false;
        if (!string.Equals(payload.TypeUrl, EventType, StringComparison.Ordinal))
            return false;

        var evt = payload.Unpack<TEvent>();
        return ReduceTyped(readModel, context, envelope, evt, now);
    }

    protected abstract bool ReduceTyped(
        ScriptExecutionReadModel readModel,
        ScriptProjectionContext context,
        EventEnvelope envelope,
        TEvent evt,
        DateTimeOffset now);
}
