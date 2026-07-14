using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;

namespace Aevatar.GAgents.StudioTeam;

[GAgent("studio.nyxid-catalog-snapshot")]
public sealed class NyxIdCatalogSnapshotGAgent
    : GAgentBase<NyxIdCatalogSnapshotState>, IProjectedActor
{
    public static string ProjectionKind => "studio-nyxid-catalog-snapshot";

    [EventHandler(EndpointName = "observeCatalog")]
    public async Task HandleObserved(NyxIdCatalogSnapshotObservedEvent evt)
    {
        ValidateOwner(evt.Owner);
        if (evt.ObservedAt == null || evt.FreshUntil == null || evt.FreshUntil <= evt.ObservedAt)
            throw new InvalidOperationException("catalog freshness interval is invalid.");
        if (string.IsNullOrWhiteSpace(evt.ContentDigest))
            throw new InvalidOperationException("catalog content digest is required.");
        if (State.Owner != null && !OwnerEquals(State.Owner, evt.Owner))
            throw new InvalidOperationException("catalog snapshot owner cannot change.");
        if (evt.Services.Any(static service =>
                string.IsNullOrWhiteSpace(service.UserServiceId) ||
                !service.NodeGrantsNotRequired && service.Nodes.Count == 0))
        {
            throw new InvalidOperationException("catalog services require exact service and node facts.");
        }

        await PersistDomainEventAsync(evt);
    }

    [EventHandler(EndpointName = "recordRefreshFailure")]
    public async Task HandleRefreshFailed(NyxIdCatalogSnapshotRefreshFailedEvent evt)
    {
        RequireCurrentOwner(evt.Owner);
        await PersistDomainEventAsync(evt);
    }

    [EventHandler(EndpointName = "invalidateCatalog")]
    public async Task HandleInvalidated(NyxIdCatalogSnapshotInvalidatedEvent evt)
    {
        RequireCurrentOwner(evt.Owner);
        if (State.Invalidated && string.Equals(State.InvalidationReason, evt.Reason, StringComparison.Ordinal))
            return;
        await PersistDomainEventAsync(evt);
    }

    protected override NyxIdCatalogSnapshotState TransitionState(
        NyxIdCatalogSnapshotState current,
        IMessage evt) => StateTransitionMatcher
        .Match(current, evt)
        .On<NyxIdCatalogSnapshotObservedEvent>(ApplyObserved)
        .On<NyxIdCatalogSnapshotRefreshFailedEvent>(static (state, _) => state)
        .On<NyxIdCatalogSnapshotInvalidatedEvent>(ApplyInvalidated)
        .OrCurrent();

    private static NyxIdCatalogSnapshotState ApplyObserved(
        NyxIdCatalogSnapshotState state,
        NyxIdCatalogSnapshotObservedEvent evt)
    {
        var next = new NyxIdCatalogSnapshotState
        {
            Owner = evt.Owner.Clone(),
            ObservedAt = evt.ObservedAt,
            FreshUntil = evt.FreshUntil,
            ExternalRevision = evt.ExternalRevision,
            ContentDigest = evt.ContentDigest,
        };
        next.Services.Add(evt.Services.Select(static service => service.Clone()));
        return next;
    }

    private static NyxIdCatalogSnapshotState ApplyInvalidated(
        NyxIdCatalogSnapshotState state,
        NyxIdCatalogSnapshotInvalidatedEvent evt)
    {
        var next = state.Clone();
        next.Invalidated = true;
        next.InvalidationReason = evt.Reason;
        return next;
    }

    private void RequireCurrentOwner(NyxIdCatalogSnapshotOwner owner)
    {
        ValidateOwner(owner);
        if (State.Owner == null || !OwnerEquals(State.Owner, owner))
            throw new InvalidOperationException("catalog snapshot owner mismatch.");
    }

    private static void ValidateOwner(NyxIdCatalogSnapshotOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (string.IsNullOrWhiteSpace(owner.Authority) ||
            owner.OwnerKind == NyxIdCatalogSnapshotOwnerKind.Unspecified ||
            string.IsNullOrWhiteSpace(owner.OwnerSubject))
        {
            throw new InvalidOperationException("catalog owner identity is incomplete.");
        }
    }

    private static bool OwnerEquals(NyxIdCatalogSnapshotOwner left, NyxIdCatalogSnapshotOwner right) =>
        string.Equals(left.Authority, right.Authority, StringComparison.Ordinal) &&
        left.OwnerKind == right.OwnerKind &&
        string.Equals(left.OwnerSubject, right.OwnerSubject, StringComparison.Ordinal);
}
