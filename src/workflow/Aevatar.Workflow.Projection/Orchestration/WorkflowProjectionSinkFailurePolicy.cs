using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;

namespace Aevatar.Workflow.Projection.Orchestration;

public sealed class WorkflowProjectionSinkFailurePolicy
    : EventSinkProjectionFailurePolicyBase<WorkflowExecutionRuntimeLease, WorkflowRunEvent>
{
    public const string SinkBackpressureErrorCode = "RUN_SINK_BACKPRESSURE";
    public const string SinkWriteErrorCode = "RUN_SINK_WRITE_FAILED";

    private readonly IProjectionSessionEventHub<WorkflowRunEvent> _runEventStreamHub;
    private readonly IProjectionClock _clock;

    public WorkflowProjectionSinkFailurePolicy(
        IEventSinkProjectionSubscriptionManager<WorkflowExecutionRuntimeLease, WorkflowRunEvent> sinkSubscriptionManager,
        IProjectionSessionEventHub<WorkflowRunEvent> runEventStreamHub,
        IProjectionClock clock)
        : base(sinkSubscriptionManager)
    {
        _runEventStreamHub = runEventStreamHub;
        _clock = clock;
    }

    protected override async ValueTask OnBackpressureAsync(
        WorkflowExecutionRuntimeLease runtimeLease,
        IEventSink<WorkflowRunEvent> sink,
        WorkflowRunEvent sourceEvent,
        EventSinkBackpressureException exception,
        CancellationToken ct)
    {
        _ = sink;
        _ = ct;
        await PublishSinkFailureAsync(
            runtimeLease,
            SinkBackpressureErrorCode,
            exception.Message,
            sourceEvent);
    }

    protected override async ValueTask OnInvalidOperationAsync(
        WorkflowExecutionRuntimeLease runtimeLease,
        IEventSink<WorkflowRunEvent> sink,
        WorkflowRunEvent sourceEvent,
        InvalidOperationException exception,
        CancellationToken ct)
    {
        _ = sink;
        _ = ct;
        await PublishSinkFailureAsync(
            runtimeLease,
            SinkWriteErrorCode,
            exception.Message,
            sourceEvent);
    }

    private async Task PublishSinkFailureAsync(
        WorkflowExecutionRuntimeLease runtimeLease,
        string code,
        string message,
        WorkflowRunEvent sourceEvent)
    {
        if (string.IsNullOrWhiteSpace(runtimeLease.ActorId) || string.IsNullOrWhiteSpace(runtimeLease.CommandId))
            return;

        var evtType = sourceEvent.Type;
        var runError = new WorkflowRunErrorEvent
        {
            Code = code,
            Message = $"Live sink delivery failed. eventType={evtType}, reason={message}",
            Timestamp = _clock.UtcNow.ToUnixTimeMilliseconds(),
        };

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
