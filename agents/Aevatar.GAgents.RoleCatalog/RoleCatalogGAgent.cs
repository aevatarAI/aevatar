using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.RoleCatalog;

/// <summary>
/// Singleton actor that owns the role catalog and draft.
/// Replaces the chrono-storage backed <c>ChronoStorageRoleCatalogStore</c>
/// for remote persistence concerns.
///
/// Actor ID: <c>role-catalog</c> (cluster-scoped singleton).
///
/// After each state change, pushes the current state to the paired
/// <see cref="RoleCatalogReadModelGAgent"/> via <c>SendToAsync</c>.
/// </summary>
public sealed class RoleCatalogGAgent : GAgentBase<RoleCatalogState>
{
    [EventHandler(EndpointName = "saveCatalog")]
    public async Task HandleCatalogSaved(RoleCatalogSavedEvent evt)
    {
        await PersistDomainEventAsync(evt);
        await PushToReadModelAsync();
    }

    [EventHandler(EndpointName = "saveDraft")]
    public async Task HandleDraftSaved(RoleDraftSavedEvent evt)
    {
        await PersistDomainEventAsync(evt);
        await PushToReadModelAsync();
    }

    [EventHandler(EndpointName = "deleteDraft")]
    public async Task HandleDraftDeleted(RoleDraftDeletedEvent evt)
    {
        if (State.Draft is null)
            return;

        await PersistDomainEventAsync(evt);
        await PushToReadModelAsync();
    }

    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        await base.OnActivateAsync(ct);
        await PushToReadModelAsync();
    }

    protected override RoleCatalogState TransitionState(
        RoleCatalogState current, IMessage evt)
    {
        return StateTransitionMatcher
            .Match(current, evt)
            .On<RoleCatalogSavedEvent>(ApplyCatalogSaved)
            .On<RoleDraftSavedEvent>(ApplyDraftSaved)
            .On<RoleDraftDeletedEvent>(ApplyDraftDeleted)
            .OrCurrent();
    }

    private static RoleCatalogState ApplyCatalogSaved(
        RoleCatalogState state, RoleCatalogSavedEvent evt)
    {
        var next = state.Clone();
        next.Roles.Clear();
        next.Roles.AddRange(evt.Roles);
        return next;
    }

    private static RoleCatalogState ApplyDraftSaved(
        RoleCatalogState state, RoleDraftSavedEvent evt)
    {
        var next = state.Clone();
        next.Draft = new RoleDraftEntry
        {
            Draft = evt.Draft?.Clone(),
            UpdatedAtUtc = evt.UpdatedAtUtc,
        };
        return next;
    }

    private static RoleCatalogState ApplyDraftDeleted(
        RoleCatalogState state, RoleDraftDeletedEvent _)
    {
        var next = state.Clone();
        next.Draft = null;
        return next;
    }

    private async Task PushToReadModelAsync()
    {
        var readModelActorId = Id + "-readmodel";
        var update = new RoleCatalogReadModelUpdateEvent { Snapshot = State.Clone() };
        await SendToAsync(readModelActorId, update);
    }
}
