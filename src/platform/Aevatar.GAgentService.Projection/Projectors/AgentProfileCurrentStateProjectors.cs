using Aevatar.GAgentService.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.GAgentService.Projection.Contexts;
using Aevatar.GAgentService.Projection.ReadModels;

namespace Aevatar.GAgentService.Projection.Projectors;

public sealed class AgentProfileCatalogCurrentStateProjector
    : ICurrentStateProjectionMaterializer<AgentProfileCatalogProjectionContext>
{
    private readonly IProjectionWriteDispatcher<AgentProfileCatalogReadModel> _writes;
    private readonly IProjectionClock _clock;

    public AgentProfileCatalogCurrentStateProjector(
        IProjectionWriteDispatcher<AgentProfileCatalogReadModel> writes,
        IProjectionClock clock)
    {
        _writes = writes ?? throw new ArgumentNullException(nameof(writes));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async ValueTask ProjectAsync(
        AgentProfileCatalogProjectionContext context,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        if (!CommittedStateEventEnvelope.TryUnpackState<AgentProfileNamespaceState>(
                envelope,
                out _,
                out var stateEvent,
                out var state) || stateEvent?.EventData is null || state?.Owner is null)
            return;

        var document = new AgentProfileCatalogReadModel
        {
            Id = context.RootActorId,
            ActorId = context.RootActorId,
            StateVersion = stateEvent.Version,
            LastEventId = stateEvent.EventId ?? string.Empty,
            UpdatedAt = CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow),
            Owner = state.Owner.Clone(),
            LastMutation = state.LastMutation?.Clone(),
        };
        document.Profiles.Add(state.Profiles.Select(static x => x.Clone()));
        document.DefaultBindings.Add(state.DefaultBindings.Select(static x => x.Clone()));
        await _writes.UpsertAsync(document, ct);
    }
}

public sealed class AgentProfileManagementCurrentStateProjector
    : ICurrentStateProjectionMaterializer<AgentProfileCurrentStateProjectionContext>
{
    private readonly IProjectionWriteDispatcher<AgentProfileManagementReadModel> _writes;
    private readonly IProjectionClock _clock;

    public AgentProfileManagementCurrentStateProjector(
        IProjectionWriteDispatcher<AgentProfileManagementReadModel> writes,
        IProjectionClock clock)
    {
        _writes = writes ?? throw new ArgumentNullException(nameof(writes));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async ValueTask ProjectAsync(
        AgentProfileCurrentStateProjectionContext context,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        if (!TryUnpack(envelope, out var stateEvent, out var state))
            return;
        await _writes.UpsertAsync(new AgentProfileManagementReadModel
        {
            Id = context.RootActorId,
            ActorId = context.RootActorId,
            StateVersion = stateEvent!.Version,
            LastEventId = stateEvent.EventId ?? string.Empty,
            UpdatedAt = CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow),
            Identity = state!.Identity.Clone(),
            Draft = state.Draft?.Clone(),
            DraftRevision = state.DraftRevision,
            DraftSha256 = state.DraftSha256,
            Published = state.Published?.Clone(),
            PublishedRevision = state.PublishedRevision,
            LastMutation = state.LastMutation?.Clone(),
        }, ct);
    }

    internal static bool TryUnpack(
        EventEnvelope envelope,
        out StateEvent? stateEvent,
        out AgentProfileState? state) =>
        CommittedStateEventEnvelope.TryUnpackState<AgentProfileState>(
            envelope,
            out _,
            out stateEvent,
            out state) && stateEvent?.EventData is not null && state?.Identity is not null;
}

public sealed class AgentProfileExecutionCurrentStateProjector
    : ICurrentStateProjectionMaterializer<AgentProfileCurrentStateProjectionContext>
{
    private readonly IProjectionWriteDispatcher<AgentProfileExecutionReadModel> _writes;
    private readonly IProjectionClock _clock;

    public AgentProfileExecutionCurrentStateProjector(
        IProjectionWriteDispatcher<AgentProfileExecutionReadModel> writes,
        IProjectionClock clock)
    {
        _writes = writes ?? throw new ArgumentNullException(nameof(writes));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async ValueTask ProjectAsync(
        AgentProfileCurrentStateProjectionContext context,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        if (!AgentProfileManagementCurrentStateProjector.TryUnpack(envelope, out var stateEvent, out var state) ||
            state!.Published is null || state.PublishedRevision <= 0)
            return;
        await _writes.UpsertAsync(new AgentProfileExecutionReadModel
        {
            Id = context.RootActorId,
            ActorId = context.RootActorId,
            StateVersion = stateEvent!.Version,
            LastEventId = stateEvent.EventId ?? string.Empty,
            UpdatedAt = CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow),
            Identity = state.Identity.Clone(),
            Snapshot = state.Published.Clone(),
        }, ct);
    }
}
