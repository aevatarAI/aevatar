using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Aevatar.GAgentService.Core.AgentProfiles;
using Google.Protobuf;

namespace Aevatar.GAgentService.Projection.AgentProfiles;

public sealed class AgentProfileOwnerCurrentStateProjector
    : ICurrentStateProjectionMaterializer<AgentProfileOwnerCurrentStateProjectionContext>
{
    private readonly IProjectionWriteDispatcher<AgentProfileOwnerDocument> _writeDispatcher;
    private readonly IProjectionClock _clock;
    private readonly IReadOnlyList<IAgentProfileReadModelMaterializationObserver> _materializationObservers;

    public AgentProfileOwnerCurrentStateProjector(
        IProjectionWriteDispatcher<AgentProfileOwnerDocument> writeDispatcher,
        IProjectionClock clock,
        IEnumerable<IAgentProfileReadModelMaterializationObserver>? materializationObservers = null)
    {
        _writeDispatcher = writeDispatcher ?? throw new ArgumentNullException(nameof(writeDispatcher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _materializationObservers = materializationObservers?.ToArray() ?? [];
    }

    public async ValueTask ProjectAsync(
        AgentProfileOwnerCurrentStateProjectionContext context,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(envelope);

        if (!CommittedStateEventEnvelope.TryUnpackState<AgentProfileState>(
                envelope,
                out _,
                out var stateEvent,
                out var state) ||
            stateEvent == null ||
            state?.Identity == null ||
            string.IsNullOrWhiteSpace(state.Identity.ProfileId))
        {
            return;
        }

        var document = new AgentProfileOwnerDocument
        {
            Id = state.Identity.ProfileId,
            ActorId = context.RootActorId,
            StateVersion = stateEvent.Version,
            LastEventId = stateEvent.EventId ?? string.Empty,
            UpdatedAt = CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow),
            Identity = state.Identity.Clone(),
            Draft = state.Draft?.Clone() ?? new AgentProfileContent(),
            DraftRevision = state.DraftRevision,
            DraftSha256 = state.DraftSha256 ?? ByteString.Empty,
            PublishedRevision = state.PublishedRevision,
            PublishedSnapshotSha256 = state.Published?.SnapshotSha256 ?? ByteString.Empty,
            PublishedSourceDraftSha256 = state.Published?.SourceDraftSha256 ?? ByteString.Empty,
        };
        if (state.LastMutation != null)
            document.LastMutation = state.LastMutation.Clone();

        var result = await _writeDispatcher.UpsertAsync(document, ct);
        AgentProfileProjectionWritePolicy.EnsureAccepted(result, document.Id);
        foreach (var observer in _materializationObservers)
            observer.OnAgentProfileReadModelMaterialized();
    }
}
