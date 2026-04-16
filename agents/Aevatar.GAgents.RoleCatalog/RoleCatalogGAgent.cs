using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;

namespace Aevatar.GAgents.RoleCatalog;

/// <summary>
/// Singleton actor that owns the role catalog and draft.
/// Replaces the chrono-storage backed <c>ChronoStorageRoleCatalogStore</c>
/// for remote persistence concerns.
///
/// Actor ID: <c>role-catalog</c> (cluster-scoped singleton).
/// </summary>
public sealed class RoleCatalogGAgent : GAgentBase<RoleCatalogState>, IProjectedActor
{
    public static string ProjectionKind => "role-catalog";


    [EventHandler(EndpointName = "saveCatalog")]
    public async Task HandleCatalogSaved(RoleCatalogSavedEvent evt)
    {
        await PersistDomainEventAsync(evt);
    }

    [EventHandler(EndpointName = "saveDraft")]
    public async Task HandleDraftSaved(RoleDraftSavedEvent evt)
    {
        await PersistDomainEventAsync(evt);
    }

    [EventHandler(EndpointName = "deleteDraft")]
    public async Task HandleDraftDeleted(RoleDraftDeletedEvent evt)
    {
        if (State.Draft is null)
            return;

        await PersistDomainEventAsync(evt);
    }

    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        await base.OnActivateAsync(ct);
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

}
