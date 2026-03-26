using Aevatar.GroupChat.Abstractions;
using Aevatar.GroupChat.Projection.Contexts;
using Aevatar.GroupChat.Projection.ReadModels;

namespace Aevatar.GroupChat.Projection.Projectors;

public sealed class SourceCatalogCurrentStateProjector
    : ICurrentStateProjectionMaterializer<SourceCatalogProjectionContext>
{
    private readonly IProjectionWriteDispatcher<SourceCatalogReadModel> _writeDispatcher;
    private readonly IProjectionClock _clock;

    public SourceCatalogCurrentStateProjector(
        IProjectionWriteDispatcher<SourceCatalogReadModel> writeDispatcher,
        IProjectionClock clock)
    {
        _writeDispatcher = writeDispatcher ?? throw new ArgumentNullException(nameof(writeDispatcher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async ValueTask ProjectAsync(
        SourceCatalogProjectionContext context,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        if (!CommittedStateEventEnvelope.TryUnpackState<GroupSourceRegistryState>(
                envelope,
                out _,
                out var stateEvent,
                out var state) ||
            stateEvent == null ||
            state == null)
        {
            return;
        }

        var document = new SourceCatalogReadModel
        {
            Id = context.RootActorId,
            ActorId = context.RootActorId,
            StateVersion = stateEvent.Version,
            LastEventId = stateEvent.EventId ?? string.Empty,
            UpdatedAt = CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow),
            SourceId = state.SourceId,
            SourceKindValue = (int)state.SourceKind,
            CanonicalLocator = state.CanonicalLocator,
            AuthorityClassValue = (int)state.AuthorityClass,
            VerificationStatusValue = (int)state.VerificationStatus,
        };

        await _writeDispatcher.UpsertAsync(document, ct);
    }
}
