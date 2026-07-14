using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.StudioTeam;
using Aevatar.Studio.Projection.Orchestration;
using Aevatar.Studio.Projection.ReadModels;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Projection.Projectors;

public sealed class NyxIdCatalogSnapshotCurrentStateProjector
    : ICurrentStateProjectionMaterializer<StudioMaterializationContext>
{
    private readonly IProjectionWriteDispatcher<NyxIdCatalogSnapshotCurrentStateDocument> _writeDispatcher;
    private readonly IProjectionClock _clock;

    public NyxIdCatalogSnapshotCurrentStateProjector(
        IProjectionWriteDispatcher<NyxIdCatalogSnapshotCurrentStateDocument> writeDispatcher,
        IProjectionClock clock)
    {
        _writeDispatcher = writeDispatcher;
        _clock = clock;
    }

    public async ValueTask ProjectAsync(
        StudioMaterializationContext context,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        if (!CommittedStateEventEnvelope.TryUnpackState<NyxIdCatalogSnapshotState>(
                envelope, out _, out var stateEvent, out var state) ||
            stateEvent?.EventData == null || state?.Owner == null)
        {
            return;
        }

        var document = new NyxIdCatalogSnapshotCurrentStateDocument
        {
            Id = context.RootActorId,
            ActorId = context.RootActorId,
            StateVersion = stateEvent.Version,
            LastEventId = stateEvent.EventId ?? string.Empty,
            UpdatedAt = Timestamp.FromDateTimeOffset(
                CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow)),
            Authority = state.Owner.Authority,
            OwnerKind = (int)state.Owner.OwnerKind,
            OwnerSubject = state.Owner.OwnerSubject,
            ObservedAt = state.ObservedAt,
            FreshUntil = state.FreshUntil,
            ExternalRevision = state.ExternalRevision,
            ContentDigest = state.ContentDigest,
            Invalidated = state.Invalidated,
            InvalidationReason = state.InvalidationReason,
        };
        document.Services.Add(state.Services.Select(MapService));
        await _writeDispatcher.UpsertAsync(document, ct);
    }

    private static NyxIdCatalogSnapshotServiceReadModel MapService(NyxIdCatalogSnapshotService service)
    {
        var result = new NyxIdCatalogSnapshotServiceReadModel
        {
            UserServiceId = service.UserServiceId,
            DisplayName = service.DisplayName,
            NodeGrantsNotRequired = service.NodeGrantsNotRequired,
            Reachable = service.Reachable,
            ServiceSlug = service.ServiceSlug,
        };
        result.Nodes.Add(service.Nodes.Select(static node => new NyxIdCatalogSnapshotNodeReadModel
        {
            NodeId = node.NodeId,
            DisplayName = node.DisplayName,
            Primary = node.Primary,
        }));
        return result;
    }
}
