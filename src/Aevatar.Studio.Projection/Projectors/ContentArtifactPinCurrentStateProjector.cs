using Aevatar.ContentArtifacts.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Studio.Projection.Orchestration;
using Aevatar.Studio.Projection.ReadModels;

namespace Aevatar.Studio.Projection.Projectors;

public sealed class ContentArtifactPinCurrentStateProjector
    : ICurrentStateProjectionMaterializer<StudioMaterializationContext>
{
    private readonly IProjectionWriteDispatcher<ContentArtifactPinCurrentStateDocument> _writeDispatcher;
    private readonly IProjectionClock _clock;

    public ContentArtifactPinCurrentStateProjector(
        IProjectionWriteDispatcher<ContentArtifactPinCurrentStateDocument> writeDispatcher,
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
        if (!CommittedStateEventEnvelope.TryUnpackState<ContentArtifactPinState>(
                envelope,
                out _,
                out var stateEvent,
                out var state) ||
            stateEvent == null ||
            state == null ||
            string.IsNullOrWhiteSpace(state.ScopeId) ||
            string.IsNullOrWhiteSpace(state.PinKey))
        {
            return;
        }

        await _writeDispatcher.UpsertAsync(
            ToDocument(
                context.RootActorId,
                stateEvent,
                state,
                CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow)),
            ct);
    }

    // Implement (issue #3527):
    //   Behavior: materialize the actor-owned pointer and authoritative pin_version verbatim.
    //   Why this shape: projection is a current-state replica and performs no uniqueness logic.
    public static ContentArtifactPinCurrentStateDocument ToDocument(
        string actorId,
        StateEvent stateEvent,
        ContentArtifactPinState state,
        DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(stateEvent);
        ArgumentNullException.ThrowIfNull(state);
        return new ContentArtifactPinCurrentStateDocument
        {
            Id = actorId,
            ActorId = actorId,
            StateVersion = stateEvent.Version,
            LastEventId = stateEvent.EventId ?? string.Empty,
            UpdatedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(observedAt),
            ScopeId = state.ScopeId,
            PinKey = state.PinKey,
            PinnedArtifactId = state.PinnedArtifactId,
            PinnedByPrincipalId = state.PinnedBy?.PrincipalId ?? string.Empty,
            PinnedByPrincipalKind = state.PinnedBy?.PrincipalKind ?? string.Empty,
            PinVersion = state.PinVersion,
            PinUpdatedAtUtc = state.UpdatedAtUtc?.Clone(),
            LastMutationId = state.LastMutationId,
            LastMutationStatus = ToWireName(state.LastMutationStatus),
            LastRejectionCode = ToWireName(state.LastRejectionCode),
            // Fix (review round 1, F1):
            //   Clear removes pinned_by but its mutation replay still requires requester authorization.
            //   Materialize the actor's committed last requester so the application can authorize that replay.
            LastMutationRequestedByPrincipalId = state.LastMutationRequestedBy?.PrincipalId ?? string.Empty,
            LastMutationRequestedByPrincipalKind = state.LastMutationRequestedBy?.PrincipalKind ?? string.Empty,
        };
    }

    private static string ToWireName(ContentArtifactPinMutationStatus status) => status switch
    {
        ContentArtifactPinMutationStatus.Succeeded => "succeeded",
        ContentArtifactPinMutationStatus.Rejected => "rejected",
        _ => string.Empty,
    };

    private static string ToWireName(ContentArtifactPinRejectionCode code) => code switch
    {
        ContentArtifactPinRejectionCode.PinVersionConflict => "pin_version_conflict",
        _ => string.Empty,
    };
}
