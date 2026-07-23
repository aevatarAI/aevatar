using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Core.AgentProfiles;

namespace Aevatar.GAgentService.Projection.AgentProfiles;

public sealed class AgentProfileExecutionCurrentStateProjector
    : ICurrentStateProjectionMaterializer<AgentProfileExecutionCurrentStateProjectionContext>
{
    private readonly IProjectionWriteDispatcher<AgentProfileExecutionDocument> _writeDispatcher;
    private readonly IProjectionClock _clock;

    public AgentProfileExecutionCurrentStateProjector(
        IProjectionWriteDispatcher<AgentProfileExecutionDocument> writeDispatcher,
        IProjectionClock clock)
    {
        _writeDispatcher = writeDispatcher ?? throw new ArgumentNullException(nameof(writeDispatcher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async ValueTask ProjectAsync(
        AgentProfileExecutionCurrentStateProjectionContext context,
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
            string.IsNullOrWhiteSpace(state.Identity.ProfileId) ||
            state.Published == null)
        {
            return;
        }

        var document = new AgentProfileExecutionDocument
        {
            Id = state.Identity.ProfileId,
            ActorId = context.RootActorId,
            StateVersion = stateEvent.Version,
            LastEventId = stateEvent.EventId ?? string.Empty,
            UpdatedAt = CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow),
            Snapshot = state.Published.Clone(),
        };

        var result = await _writeDispatcher.UpsertAsync(document, ct);
        AgentProfileProjectionWritePolicy.EnsureAccepted(result, document.Id);
    }
}
