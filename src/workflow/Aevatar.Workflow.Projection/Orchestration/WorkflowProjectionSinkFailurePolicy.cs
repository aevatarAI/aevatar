using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;

namespace Aevatar.Workflow.Projection.Orchestration;

public sealed class WorkflowProjectionSinkFailurePolicy
    : EventSinkProjectionFailurePolicyBase<WorkflowExecutionRuntimeLease, WorkflowRunEventEnvelope>
{
    public const string SinkBackpressureErrorCode = "RUN_SINK_BACKPRESSURE";
    public const string SinkWriteErrorCode = "RUN_SINK_WRITE_FAILED";

    private readonly IProjectionSessionEventHub<WorkflowRunEventEnvelope> _runEventStreamHub;
    private readonly IProjectionClock _clock;

    public WorkflowProjectionSinkFailurePolicy(
        IEventSinkProjectionSubscriptionManager<WorkflowExecutionRuntimeLease, WorkflowRunEventEnvelope> sinkSubscriptionManager,
        IProjectionSessionEventHub<WorkflowRunEventEnvelope> runEventStreamHub,
        IProjectionClock clock)
        : base(sinkSubscriptionManager)
    {
        _runEventStreamHub = runEventStreamHub;
        _clock = clock;
    }

    protected override async ValueTask OnBackpressureAsync(
        WorkflowExecutionRuntimeLease runtimeLease,
        IEventSink<WorkflowRunEventEnvelope> sink,
        WorkflowRunEventEnvelope sourceEvent,
        EventSinkBackpressureException exception,
        CancellationToken ct)
    {
        _ = ct;
        await PublishSinkFailureAsync(
            runtimeLease,
            sink,
            SinkBackpressureErrorCode,
            exception.Message,
            sourceEvent);
    }

    protected override async ValueTask OnCompletedAsync(
        WorkflowExecutionRuntimeLease runtimeLease,
        IEventSink<WorkflowRunEventEnvelope> sink,
        WorkflowRunEventEnvelope sourceEvent,
        EventSinkCompletedException exception,
        CancellationToken ct)
    {
        _ = ct;
        await PublishSinkFailureAsync(
            runtimeLease,
            sink,
            SinkWriteErrorCode,
            exception.Message,
            sourceEvent);
    }

    protected override async ValueTask OnInvalidOperationAsync(
        WorkflowExecutionRuntimeLease runtimeLease,
        IEventSink<WorkflowRunEventEnvelope> sink,
        WorkflowRunEventEnvelope sourceEvent,
        InvalidOperationException exception,
        CancellationToken ct)
    {
        _ = ct;
        await PublishSinkFailureAsync(
            runtimeLease,
            sink,
            SinkWriteErrorCode,
            exception.Message,
            sourceEvent);
    }

    private async Task PublishSinkFailureAsync(
        WorkflowExecutionRuntimeLease runtimeLease,
        IEventSink<WorkflowRunEventEnvelope> sink,
        string code,
        string message,
        WorkflowRunEventEnvelope sourceEvent)
    {
        var evtType = WorkflowRunEventTypes.GetEventType(sourceEvent);
        var runError = new WorkflowRunEventEnvelope
        {
            Timestamp = _clock.UtcNow.ToUnixTimeMilliseconds(),
            RunError = new WorkflowRunErrorEventPayload
            {
                Code = code,
                Message = $"Live sink delivery failed. eventType={evtType}, reason={message}",
            },
        };

        try
        {
            await sink.PushAsync(runError, CancellationToken.None);
        }
        catch
        {
            // The failing current sink may already be unwritable; releasing it still completes the stream.
        }

        if (string.IsNullOrWhiteSpace(runtimeLease.ActorId) || string.IsNullOrWhiteSpace(runtimeLease.CommandId))
            return;

        try
        {
            await _runEventStreamHub.PublishAsync(
                runtimeLease.ActorId,
                runtimeLease.CommandId,
                runError,
                CancellationToken.None);
        }
        catch
        {
            // Best-effort telemetry path; do not fail run processing on secondary publish errors.
        }
    }
}
