using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Aevatar.GAgentService.Core.AgentProfiles;

namespace Aevatar.GAgentService.Projection.AgentProfiles;

public sealed class AgentProfileNamespaceCurrentStateProjector
    : ICurrentStateProjectionMaterializer<AgentProfileNamespaceCurrentStateProjectionContext>
{
    private readonly IProjectionWriteDispatcher<AgentProfileNamespaceCatalogDocument> _writeDispatcher;
    private readonly IProjectionClock _clock;
    private readonly IReadOnlyList<IAgentProfileReadModelMaterializationObserver> _materializationObservers;

    public AgentProfileNamespaceCurrentStateProjector(
        IProjectionWriteDispatcher<AgentProfileNamespaceCatalogDocument> writeDispatcher,
        IProjectionClock clock,
        IEnumerable<IAgentProfileReadModelMaterializationObserver>? materializationObservers = null)
    {
        _writeDispatcher = writeDispatcher ?? throw new ArgumentNullException(nameof(writeDispatcher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _materializationObservers = materializationObservers?.ToArray() ?? [];
    }

    public async ValueTask ProjectAsync(
        AgentProfileNamespaceCurrentStateProjectionContext context,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(envelope);

        if (!CommittedStateEventEnvelope.TryUnpackState<AgentProfileNamespaceState>(
                envelope,
                out _,
                out var stateEvent,
                out var state) ||
            stateEvent == null ||
            state == null)
        {
            return;
        }

        var document = new AgentProfileNamespaceCatalogDocument
        {
            Id = context.RootActorId,
            ActorId = context.RootActorId,
            StateVersion = stateEvent.Version,
            LastEventId = stateEvent.EventId ?? string.Empty,
            UpdatedAt = CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow),
        };
        foreach (var source in state.Profiles
                     .Where(IsActiveSafeEntry)
                     .OrderBy(entry => entry.Identity.Reference.OwnerHandle, StringComparer.Ordinal)
                     .ThenBy(entry => entry.Identity.Reference.ProfileSlug, StringComparer.Ordinal))
        {
            var entry = new AgentProfileCatalogEntryDocument
            {
                ProfileId = source.Identity.ProfileId,
                Reference = source.Identity.Reference.Clone(),
                Owner = source.Identity.Owner.Clone(),
                OwningScopeId = source.Identity.OwningScopeId,
                Status = source.Status,
            };
            if (source.PublishedSummary != null)
                entry.PublishedSummary = source.PublishedSummary.Clone();
            document.Entries.Add(entry);
        }

        var result = await _writeDispatcher.UpsertAsync(document, ct);
        AgentProfileProjectionWritePolicy.EnsureAccepted(result, document.Id);
        foreach (var observer in _materializationObservers)
            observer.OnAgentProfileReadModelMaterialized();
    }

    private static bool IsActiveSafeEntry(AgentProfileNamespaceEntryState entry) =>
        entry.Status == AgentProfileProvisioningStatus.Active &&
        entry.Identity != null &&
        !string.IsNullOrWhiteSpace(entry.Identity.ProfileId) &&
        entry.Identity.Reference != null &&
        entry.Identity.Owner != null;
}
