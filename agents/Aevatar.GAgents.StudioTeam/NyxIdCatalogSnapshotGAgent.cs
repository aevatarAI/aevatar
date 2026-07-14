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
    public async Task HandleObserved(ObserveNyxIdCatalogSnapshotCommand command)
    {
        ValidateOwner(command.Owner);
        if (command.ObservedAt == null || command.FreshUntil == null || command.FreshUntil <= command.ObservedAt)
            throw new InvalidOperationException("catalog freshness interval is invalid.");
        if (string.IsNullOrWhiteSpace(command.ContentDigest))
            throw new InvalidOperationException("catalog content digest is required.");
        if (State.Owner != null && !OwnerEquals(State.Owner, command.Owner))
            throw new InvalidOperationException("catalog snapshot owner cannot change.");
        if (command.Services.Any(static service =>
                string.IsNullOrWhiteSpace(service.UserServiceId) ||
                !service.NodeGrantsNotRequired && service.Nodes.Count == 0))
        {
            throw new InvalidOperationException("catalog services require exact service and node facts.");
        }

        var evt = new NyxIdCatalogSnapshotObservedEvent
        {
            Owner = command.Owner.Clone(),
            ObservedAt = command.ObservedAt,
            FreshUntil = command.FreshUntil,
            ExternalRevision = command.ExternalRevision,
            ContentDigest = command.ContentDigest,
        };
        evt.Services.Add(command.Services.Select(static service => service.Clone()));
        await PersistDomainEventAsync(evt);
    }

    [EventHandler(EndpointName = "recordRefreshFailure")]
    public async Task HandleRefreshFailed(RecordNyxIdCatalogSnapshotRefreshFailureCommand command)
    {
        RequireCurrentOwner(command.Owner);
        await PersistDomainEventAsync(new NyxIdCatalogSnapshotRefreshFailedEvent
        {
            Owner = command.Owner.Clone(),
            FailedAt = command.FailedAt,
            FailureCode = command.FailureCode,
        });
    }

    [EventHandler(EndpointName = "invalidateCatalog")]
    public async Task HandleInvalidated(InvalidateNyxIdCatalogSnapshotCommand command)
    {
        RequireCurrentOwner(command.Owner);
        if (State.Invalidated && string.Equals(State.InvalidationReason, command.Reason, StringComparison.Ordinal))
            return;
        await PersistDomainEventAsync(new NyxIdCatalogSnapshotInvalidatedEvent
        {
            Owner = command.Owner.Clone(),
            InvalidatedAt = command.InvalidatedAt,
            Reason = command.Reason,
        });
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
