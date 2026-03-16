using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.CQRS.Projection.Core.Orchestration;

public sealed class DurableProjectionDispatchCompensator<TReadModel>
    : IProjectionStoreDispatchCompensator<TReadModel>
    where TReadModel : class, IProjectionReadModel
{
    private readonly IProjectionDispatchCompensationOutbox _outbox;
    private readonly ILogger<DurableProjectionDispatchCompensator<TReadModel>> _logger;

    public DurableProjectionDispatchCompensator(
        IProjectionDispatchCompensationOutbox outbox,
        ILogger<DurableProjectionDispatchCompensator<TReadModel>>? logger = null)
    {
        _outbox = outbox;
        _logger = logger ?? NullLogger<DurableProjectionDispatchCompensator<TReadModel>>.Instance;
    }

    public async Task CompensateAsync(
        ProjectionStoreDispatchCompensationContext<TReadModel> context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();

        if (context.ReadModel is not IMessage protobufReadModel)
        {
            throw new InvalidOperationException(
                $"Projection dispatch compensation requires protobuf read model payloads. readModelType='{typeof(TReadModel).FullName}'.");
        }

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
            ReadModelType = typeof(TReadModel).AssemblyQualifiedName ?? context.ReadModelType,
            ReadModel = Any.Pack(protobufReadModel),
            Key = context.ReadModel.Id,
            EnqueuedAtUtc = Timestamp.FromDateTime(now),
            LastError = context.Exception.GetType().Name,
        };

        await _outbox.EnqueueAsync(evt, ct);
        _logger.LogWarning(
            context.Exception,
            "Projection dispatch failure enqueued to actor-based compensation outbox. readModelType={ReadModelType} operation={Operation} failedStore={FailedStore} recordId={RecordId}",
            evt.ReadModelType,
            context.Operation,
            context.FailedStore,
            recordId);
    }
}
