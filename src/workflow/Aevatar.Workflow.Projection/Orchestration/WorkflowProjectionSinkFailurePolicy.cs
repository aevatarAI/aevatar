using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Projection.Orchestration;

public sealed class WorkflowProjectionSinkFailurePolicy : IWorkflowProjectionSinkFailurePolicy
{
    public const string SinkBackpressureErrorCode = "RUN_SINK_BACKPRESSURE";
    public const string SinkWriteErrorCode = "RUN_SINK_WRITE_FAILED";

    private readonly IWorkflowProjectionSinkSubscriptionManager _sinkSubscriptionManager;
    private readonly IProjectionSessionEventHub<WorkflowRunEvent> _runEventStreamHub;
    private readonly IProjectionClock _clock;

    public WorkflowProjectionSinkFailurePolicy(
        IWorkflowProjectionSinkSubscriptionManager sinkSubscriptionManager,
        IProjectionSessionEventHub<WorkflowRunEvent> runEventStreamHub,
        IProjectionClock clock)
    {
        _sinkSubscriptionManager = sinkSubscriptionManager;
        _runEventStreamHub = runEventStreamHub;
        _clock = clock;
    }

    public async ValueTask<bool> TryHandleAsync(
        WorkflowExecutionRuntimeLease runtimeLease,
        IWorkflowRunEventSink sink,
        WorkflowRunEvent sourceEvent,
        Exception exception,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(runtimeLease);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(sourceEvent);
        ArgumentNullException.ThrowIfNull(exception);
        ct.ThrowIfCancellationRequested();

        switch (exception)
        {
            case WorkflowRunEventSinkBackpressureException backpressureException:
                await _sinkSubscriptionManager.DetachAsync(runtimeLease, sink, CancellationToken.None);
                await PublishSinkFailureAsync(
                    runtimeLease,
                    SinkBackpressureErrorCode,
                    backpressureException.Message,
                    sourceEvent);
                return true;
            case WorkflowRunEventSinkCompletedException:
                await _sinkSubscriptionManager.DetachAsync(runtimeLease, sink, CancellationToken.None);
                return true;
            case InvalidOperationException invalidOperationException:
                await _sinkSubscriptionManager.DetachAsync(runtimeLease, sink, CancellationToken.None);
                await PublishSinkFailureAsync(
                    runtimeLease,
                    SinkWriteErrorCode,
                    invalidOperationException.Message,
                    sourceEvent);
                return true;
            default:
                return false;
        }
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
