using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Projection.Orchestration;

/// <summary>
/// Publishes projection dispatch failures to workflow run session event stream.
/// </summary>
public sealed class WorkflowProjectionDispatchFailureReporter
    : IProjectionDispatchFailureReporter<WorkflowExecutionProjectionContext>
{
    private const string ProjectionDispatchFailureCode = "PROJECTION_DISPATCH_FAILED";
    private readonly IProjectionSessionEventHub<WorkflowRunEvent> _runEventStreamHub;
    private readonly IProjectionClock _clock;

    public WorkflowProjectionDispatchFailureReporter(
        IProjectionSessionEventHub<WorkflowRunEvent> runEventStreamHub,
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
        var evt = new WorkflowRunErrorEvent
        {
            Code = ProjectionDispatchFailureCode,
            Message = $"Projection dispatch failed. eventId={envelope.Id}, payloadType={payloadType}, reason={exception.Message}",
            Timestamp = _clock.UtcNow.ToUnixTimeMilliseconds(),
        };

        return new ValueTask(
            _runEventStreamHub.PublishAsync(context.RootActorId, context.CommandId, evt, ct));
    }
}
