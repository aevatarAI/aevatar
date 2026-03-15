using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Projection.Orchestration;

internal sealed class WorkflowProjectionDurableOutboxCompensator
    : IProjectionStoreDispatchCompensator<WorkflowExecutionReport>
{
    private readonly IProjectionDispatchCompensationOutbox _outbox;
    private readonly ILogger<WorkflowProjectionDurableOutboxCompensator> _logger;

    public WorkflowProjectionDurableOutboxCompensator(
        IProjectionDispatchCompensationOutbox outbox,
        ILogger<WorkflowProjectionDurableOutboxCompensator>? logger = null)
    {
        _outbox = outbox;
        _logger = logger ?? NullLogger<WorkflowProjectionDurableOutboxCompensator>.Instance;
    }

    public async Task CompensateAsync(
        ProjectionStoreDispatchCompensationContext<WorkflowExecutionReport> context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();

        var now = DateTime.UtcNow;
        var recordId = string.IsNullOrWhiteSpace(context.DispatchId)
            ? Guid.NewGuid().ToString("N")
            : context.DispatchId;

        var evt = new ProjectionCompensationEnqueuedEvent
        {
            RecordId = recordId,
            Operation = context.Operation,
            FailedStore = context.FailedStore,
            SucceededStores = { context.SucceededStores },
            ReadModelType = context.ReadModelType,
            ReadModel = Any.Pack(context.ReadModel),
            Key = context.ReadModel.Id,
            EnqueuedAtUtc = Timestamp.FromDateTime(now),
            LastError = context.Exception.GetType().Name,
        };

        await _outbox.EnqueueAsync(evt, ct);
        _logger.LogWarning(
            context.Exception,
            "Projection dispatch failure enqueued to actor-based compensation outbox. readModelType={ReadModelType} operation={Operation} failedStore={FailedStore} recordId={RecordId}",
            context.ReadModelType,
            context.Operation,
            context.FailedStore,
            recordId);
    }
}
