using Aevatar.CQRS.Projection.WorkflowExecution.ReadModels;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.CQRS.Projection.WorkflowExecution.Reducers;

/// <summary>
/// Generic reducer base for a single protobuf event type.
/// </summary>
public abstract class WorkflowExecutionEventReducerBase<TEvent>
    : IProjectionEventReducer<WorkflowExecutionReport, WorkflowExecutionProjectionContext>
    where TEvent : class, IMessage<TEvent>, new()
{
    private static readonly string _eventTypeUrl = Any.Pack(new TEvent()).TypeUrl;

    public abstract int Order { get; }

    public string EventTypeUrl => _eventTypeUrl;

    public void Reduce(
        WorkflowExecutionReport report,
        WorkflowExecutionProjectionContext context,
        EventEnvelope envelope,
        DateTimeOffset now)
    {
        var payload = envelope.Payload;
        if (payload == null) return;
        if (!string.Equals(payload.TypeUrl, _eventTypeUrl, StringComparison.Ordinal)) return;

        var evt = payload.Unpack<TEvent>();
        Reduce(report, context, envelope, evt, now);
    }

    protected abstract void Reduce(
        WorkflowExecutionReport report,
        WorkflowExecutionProjectionContext context,
        EventEnvelope envelope,
        TEvent evt,
        DateTimeOffset now);
}
