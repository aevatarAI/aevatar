using Aevatar.Workflow.Projection.ReadModels;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Projection.Reducers;

/// <summary>
/// Generic reducer base for a single protobuf event type.
/// </summary>
public abstract class WorkflowExecutionEventReducerBase<TEvent>
    : IProjectionEventReducer<WorkflowExecutionReport, WorkflowExecutionProjectionContext>
    where TEvent : class, IMessage<TEvent>, new()
{
    private static readonly string _eventTypeUrl = Any.Pack(new TEvent()).TypeUrl;

    public string EventTypeUrl => _eventTypeUrl;

    public bool Reduce(
        WorkflowExecutionReport report,
        WorkflowExecutionProjectionContext context,
        EventEnvelope envelope,
        DateTimeOffset now)
    {
        var payload = envelope.Payload;
        if (payload == null)
            return false;
        if (!string.Equals(payload.TypeUrl, _eventTypeUrl, StringComparison.Ordinal))
            return false;

        var evt = payload.Unpack<TEvent>();
        return Reduce(report, context, envelope, evt, now);
    }

    protected abstract bool Reduce(
        WorkflowExecutionReport report,
        WorkflowExecutionProjectionContext context,
        EventEnvelope envelope,
        TEvent evt,
        DateTimeOffset now);
}
