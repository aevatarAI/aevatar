using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.StudioTeam;
using Aevatar.Studio.Projection.Mapping;
using Aevatar.Studio.Projection.Orchestration;
using Aevatar.Studio.Projection.ReadModels;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Projection.Projectors;

/// <summary>
/// Materializes <see cref="StudioTeamState"/> committed events into
/// <see cref="StudioTeamCurrentStateDocument"/> (ADR-0017). Surfaces a
/// fully-typed projection of the authority — wire-stable string lifecycle,
/// derived <c>member_count</c> from the persisted <c>member_ids</c> roster.
///
/// The full member roster is intentionally NOT mirrored into the read model
/// (ADR-0017 §Non-Goals). Listing members for a team goes through the member
/// read model filtered by <c>team_id</c>.
/// </summary>
public sealed class StudioTeamCurrentStateProjector
    : ICurrentStateProjectionMaterializer<StudioMaterializationContext>
{
    private readonly IProjectionWriteDispatcher<StudioTeamCurrentStateDocument> _writeDispatcher;
    private readonly IProjectionClock _clock;

    public StudioTeamCurrentStateProjector(
        IProjectionWriteDispatcher<StudioTeamCurrentStateDocument> writeDispatcher,
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

        if (!CommittedStateEventEnvelope.TryUnpackState<StudioTeamState>(
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

        var document = new StudioTeamCurrentStateDocument
        {
            Id = context.RootActorId,
            ActorId = context.RootActorId,
            StateVersion = stateEvent.Version,
            LastEventId = stateEvent.EventId ?? string.Empty,
            UpdatedAt = Timestamp.FromDateTimeOffset(updatedAt),
            TeamId = state.TeamId,
            ScopeId = state.ScopeId,
            DisplayName = state.DisplayName,
            Description = state.Description,
            LifecycleStage = TeamLifecycleStageMapper.ToWireName(state.LifecycleStage),
            CreatedAt = state.CreatedAtUtc,
            // member_count is derived from the persisted roster — never trust
            // a counter field that drifts independently of the set of ids.
            MemberCount = state.MemberIds.Count,
        };

        await _writeDispatcher.UpsertAsync(document, ct);
    }
}
