using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.StudioMember;
using Aevatar.Studio.Projection.Orchestration;
using Aevatar.Studio.Projection.ReadModels;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Projection.Projectors;

/// <summary>
/// Materializes <see cref="StudioMemberState"/> committed events into
/// <see cref="StudioMemberCurrentStateDocument"/>. Surfaces the denormalized
/// roster fields (<c>scope_id</c>, <c>display_name</c>, <c>published_service_id</c>,
/// <c>lifecycle_stage</c>) that the query port uses for member-centric scans
/// without re-reading the actor state.
/// </summary>
public sealed class StudioMemberCurrentStateProjector
    : ICurrentStateProjectionMaterializer<StudioMaterializationContext>
{
    private readonly IProjectionWriteDispatcher<StudioMemberCurrentStateDocument> _writeDispatcher;
    private readonly IProjectionClock _clock;

    public StudioMemberCurrentStateProjector(
        IProjectionWriteDispatcher<StudioMemberCurrentStateDocument> writeDispatcher,
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

        if (!CommittedStateEventEnvelope.TryUnpackState<StudioMemberState>(
                envelope,
                out _,
                out var stateEvent,
                out var state) ||
            stateEvent?.EventData == null ||
            state == null)
        {
            return;
        }

        var updatedAt = CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow);

        var document = new StudioMemberCurrentStateDocument
        {
            Id = context.RootActorId,
            ActorId = context.RootActorId,
            StateVersion = stateEvent.Version,
            LastEventId = stateEvent.EventId ?? string.Empty,
            UpdatedAt = Timestamp.FromDateTimeOffset(updatedAt),
            StateRoot = Any.Pack(state),
            MemberId = state.MemberId,
            ScopeId = state.ScopeId,
            DisplayName = state.DisplayName,
            Description = state.Description,
            ImplementationKind = (int)state.ImplementationKind,
            LifecycleStage = (int)state.LifecycleStage,
            PublishedServiceId = state.PublishedServiceId,
            LastBoundRevisionId = state.LastBinding?.RevisionId ?? string.Empty,
            CreatedAt = state.CreatedAtUtc,
        };

        await _writeDispatcher.UpsertAsync(document, ct);
    }
}
