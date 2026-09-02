using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Foundation.Abstractions;

namespace Aevatar.GAgents.Channel.Identity;

public sealed class ManagedCodexCredentialProjector(
    IProjectionWriteDispatcher<ManagedCodexCredentialDocument> writeDispatcher,
    IProjectionClock clock)
    : ICurrentStateProjectionMaterializer<ManagedCodexCredentialMaterializationContext>
{
    private readonly IProjectionWriteDispatcher<ManagedCodexCredentialDocument> _writeDispatcher =
        writeDispatcher ?? throw new ArgumentNullException(nameof(writeDispatcher));
    private readonly IProjectionClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    public async ValueTask ProjectAsync(
        ManagedCodexCredentialMaterializationContext context,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        if (!CommittedStateEventEnvelope.TryUnpackState<ManagedCodexCredentialState>(
                envelope,
                out _,
                out var stateEvent,
                out var state) ||
            stateEvent is null ||
            state is null)
        {
            return;
        }

        var document = new ManagedCodexCredentialDocument
        {
            Id = context.RootActorId,
            StateVersion = stateEvent.Version,
            LastEventId = stateEvent.EventId ?? string.Empty,
            UpdatedAt = CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow),
            Credential = state.Credential?.Clone(),
            RevokedAt = state.RevokedAt?.Clone(),
        };
        document.PendingRevocations.Add(state.PendingRevocations.Select(static item => item.Clone()));
        await _writeDispatcher.UpsertAsync(document, ct);
    }
}
