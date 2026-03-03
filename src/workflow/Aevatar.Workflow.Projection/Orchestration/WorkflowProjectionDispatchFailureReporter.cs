
using Aevatar.Workflow.Application.Abstractions.Runs;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Projection.Orchestration;

/// <summary>
/// Publishes projection dispatch failures to workflow run session event stream.
/// </summary>
public sealed class WorkflowProjectionDispatchFailureReporter
    : IProjectionDispatchFailureReporter<WorkflowExecutionProjectionContext>
{
    internal const string ProjectionDispatchFailureEventName = "aevatar.projection.dispatch.failure";
    private readonly IProjectionSessionEventHub<WorkflowRunEventEnvelope> _runEventStreamHub;
    private readonly IProjectionClock _clock;

    public WorkflowProjectionDispatchFailureReporter(
        IProjectionSessionEventHub<WorkflowRunEventEnvelope> runEventStreamHub,
        IProjectionClock clock)
    {
        _runEventStreamHub = runEventStreamHub;
        _clock = clock;
    }

    public ValueTask ReportAsync(
        WorkflowExecutionProjectionContext context,
        EventEnvelope envelope,
        Exception exception,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(exception);

        if (string.IsNullOrWhiteSpace(context.RootActorId) || string.IsNullOrWhiteSpace(context.CommandId))
            return ValueTask.CompletedTask;

        var payloadType = envelope.Payload?.TypeUrl ?? "(none)";
        var evt = new WorkflowRunEventEnvelope
        {
            Timestamp = _clock.UtcNow.ToUnixTimeMilliseconds(),
            Custom = new WorkflowCustomEventPayload
            {
                Name = ProjectionDispatchFailureEventName,
                Payload = Any.Pack(new WorkflowProjectionDispatchFailureCustomPayload
                {
                    EventId = envelope.Id ?? string.Empty,
                    PayloadType = payloadType,
                    Reason = exception.Message ?? string.Empty,
                }),
            },
        };

        return new ValueTask(
            _runEventStreamHub.PublishAsync(context.RootActorId, context.CommandId, evt, ct));
    }
}
