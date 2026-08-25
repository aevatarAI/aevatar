using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.WorkflowDelivery;
using Aevatar.Studio.Projection.Orchestration;
using Aevatar.Studio.Projection.ReadModels;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Projection.Projectors;

public sealed class WorkflowDeliveryCurrentStateProjector
    : ICurrentStateProjectionMaterializer<StudioMaterializationContext>
{
    private readonly IProjectionWriteDispatcher<WorkflowDeliveryCurrentStateDocument> _writeDispatcher;
    private readonly IProjectionClock _clock;

    public WorkflowDeliveryCurrentStateProjector(
        IProjectionWriteDispatcher<WorkflowDeliveryCurrentStateDocument> writeDispatcher,
        IProjectionClock clock)
    {
        _writeDispatcher = writeDispatcher ?? throw new ArgumentNullException(nameof(writeDispatcher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async ValueTask ProjectAsync(
        StudioMaterializationContext context,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(envelope);

        if (!CommittedStateEventEnvelope.TryUnpackState<WorkflowDeliveryState>(
                envelope,
                out _,
                out var stateEvent,
                out var state) ||
            stateEvent?.EventData == null ||
            state == null)
        {
            return;
        }

        var document = new WorkflowDeliveryCurrentStateDocument
        {
            Id = context.RootActorId,
            ActorId = context.RootActorId,
            StateVersion = stateEvent.Version,
            LastEventId = stateEvent.EventId ?? string.Empty,
            UpdatedAt = Timestamp.FromDateTimeOffset(
                CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow)),
            DeliveryId = state.DeliveryId,
            Package = state.Package?.Clone(),
            TargetScopeId = state.TargetScopeId,
            ExpiresAtUtc = state.ExpiresAtUtc?.Clone(),
            Note = state.Note,
            LifecycleStatus = state.LifecycleStatus,
            CreatedBy = state.CreatedBy,
            CreatedAtUtc = state.CreatedAtUtc?.Clone(),
            AccessedAtUtc = state.AccessedAtUtc?.Clone(),
            RevokedBy = state.RevokedBy,
            RevokedAtUtc = state.RevokedAtUtc?.Clone(),
            Installation = state.Installation?.Clone(),
        };
        document.Connections.AddRange(state.Connections.Select(static value => value.Clone()));

        await _writeDispatcher.UpsertAsync(document, ct);
    }
}
