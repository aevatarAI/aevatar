using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime;

namespace Aevatar.Foundation.Projection.Runtime;

public sealed class RuntimeFleetCapabilityAuthorityCurrentStateProjector
    : ICurrentStateProjectionMaterializer<RuntimeFleetCapabilityProjectionContext>
{
    private readonly IProjectionWriteDispatcher<RuntimeFleetCapabilityAuthorityCurrentStateDocument>
        _writeDispatcher;
    private readonly IProjectionClock _clock;

    public RuntimeFleetCapabilityAuthorityCurrentStateProjector(
        IProjectionWriteDispatcher<RuntimeFleetCapabilityAuthorityCurrentStateDocument> writeDispatcher,
        IProjectionClock clock)
    {
        _writeDispatcher = writeDispatcher ?? throw new ArgumentNullException(nameof(writeDispatcher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async ValueTask ProjectAsync(
        RuntimeFleetCapabilityProjectionContext context,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(envelope);
        if (!string.Equals(
                context.RootActorId,
                RuntimeFleetCapabilityAuthorityIdentity.ActorId,
                StringComparison.Ordinal) ||
            !string.Equals(
                context.ProjectionKind,
                RuntimeFleetCapabilityProjectionKinds.AuthorityCurrentState,
                StringComparison.Ordinal) ||
            !CommittedStateEventEnvelope.TryUnpackState<RuntimeFleetCapabilityAuthorityState>(
                envelope,
                out _,
                out var stateEvent,
                out var state) ||
            stateEvent == null ||
            state == null ||
            !string.Equals(
                stateEvent.AgentId,
                RuntimeFleetCapabilityAuthorityIdentity.ActorId,
                StringComparison.Ordinal))
        {
            return;
        }

        var document = new RuntimeFleetCapabilityAuthorityCurrentStateDocument
        {
            Id = RuntimeFleetCapabilityAuthorityIdentity.ActorId,
            AuthorityActorId = RuntimeFleetCapabilityAuthorityIdentity.ActorId,
            StateVersion = stateEvent.Version,
            LastEventId = stateEvent.EventId ?? string.Empty,
            UpdatedAt = CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow),
            Membership = state.Membership?.Clone(),
        };
        document.Gates.Add(state.Gates.Select(static gate => gate.Clone()));
        await _writeDispatcher.UpsertAsync(document, ct);
    }
}
