using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Google.Protobuf;

namespace Aevatar.GAgentService.Core.Schedules.Authorization;

[GAgent("gagent.service.nyxid-authorization-catalog")]
public sealed class NyxIdAuthorizationCatalogGAgent
    : GAgentBase<NyxIdAuthorizationCatalogState>, IProjectedActor
{
    public const string DurableProjectionKind = "nyxid-authorization-catalog";

    public static string ProjectionKind => DurableProjectionKind;

    [EventHandler(EndpointName = "activateCatalog")]
    public async Task HandleActivateAsync(ActivateNyxIdAuthorizationCatalogCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateOwner(command.Owner);
        EnsureCurrentOwner(command.Owner);
        if (command.ActivatedAt == null)
            throw new InvalidOperationException("Catalog activation time is required.");
        if (State.Activated)
            return;

        await PersistDomainEventAsync(new NyxIdAuthorizationCatalogActivatedEvent
        {
            Owner = command.Owner.Clone(),
            ActivatedAt = command.ActivatedAt.Clone(),
            LifecycleFence = State.LifecycleFence,
        });
    }

    [EventHandler(EndpointName = "observeCatalog")]
    public async Task HandleObserveAsync(ObserveNyxIdAuthorizationCatalogCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateOwner(command.Owner);
        ValidateObservation(command);
        EnsureCurrentOwner(command.Owner);
        EnsureLifecycleFence(State.LifecycleFence, command.ExpectedLifecycleFence);

        if (State.ObservedAt != null)
        {
            var ordering = command.ObservedAt.CompareTo(State.ObservedAt);
            if (ordering < 0)
                return;
            if (ordering == 0)
            {
                if (string.Equals(State.ContentDigest, command.ContentDigest, StringComparison.Ordinal) &&
                    State.FreshUntil?.Equals(command.FreshUntil) == true &&
                    string.Equals(State.ExternalRevision, command.ExternalRevision, StringComparison.Ordinal))
                {
                    return;
                }

                throw new InvalidOperationException(
                    "A NyxID authorization catalog observation timestamp cannot identify conflicting content.");
            }
        }

        var observed = new NyxIdAuthorizationCatalogObservedEvent
        {
            Owner = command.Owner.Clone(),
            ObservedAt = command.ObservedAt.Clone(),
            FreshUntil = command.FreshUntil.Clone(),
            ExternalRevision = command.ExternalRevision.Trim(),
            ContentDigest = command.ContentDigest.Trim(),
            LifecycleFence = State.LifecycleFence,
        };
        observed.Services.Add(command.Services.Select(static service => service.Clone()));
        await PersistDomainEventAsync(observed);
    }

    [EventHandler(EndpointName = "recordRefreshFailure")]
    public async Task HandleRefreshFailureAsync(RecordNyxIdAuthorizationCatalogRefreshFailureCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateOwner(command.Owner);
        EnsureCurrentOwner(command.Owner);
        if (command.FailedAt == null || string.IsNullOrWhiteSpace(command.FailureCode))
            throw new InvalidOperationException("Refresh failure time and code are required.");
        if (State.LastRefreshFailedAt != null && command.FailedAt.CompareTo(State.LastRefreshFailedAt) <= 0)
            return;

        await PersistDomainEventAsync(new NyxIdAuthorizationCatalogRefreshFailedEvent
        {
            Owner = command.Owner.Clone(),
            FailedAt = command.FailedAt.Clone(),
            FailureCode = command.FailureCode.Trim(),
        });
    }

    [EventHandler(EndpointName = "invalidateCatalog")]
    public async Task HandleInvalidateAsync(InvalidateNyxIdAuthorizationCatalogCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateOwner(command.Owner);
        EnsureCurrentOwner(command.Owner);
        if (command.InvalidatedAt == null || string.IsNullOrWhiteSpace(command.Reason))
            throw new InvalidOperationException("Invalidation time and reason are required.");
        if (State.Invalidated &&
            string.Equals(State.InvalidationReason, command.Reason.Trim(), StringComparison.Ordinal))
        {
            return;
        }

        await PersistDomainEventAsync(new NyxIdAuthorizationCatalogInvalidatedEvent
        {
            Owner = command.Owner.Clone(),
            InvalidatedAt = command.InvalidatedAt.Clone(),
            Reason = command.Reason.Trim(),
            LifecycleFence = checked(State.LifecycleFence + 1),
        });
    }

    [EventHandler(EndpointName = "cleanupCatalog")]
    public async Task HandleCleanupAsync(CleanupNyxIdAuthorizationCatalogCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateOwner(command.Owner);
        EnsureCurrentOwner(command.Owner);
        if (command.CleanedAt == null || string.IsNullOrWhiteSpace(command.Reason))
            throw new InvalidOperationException("Cleanup time and reason are required.");
        if (State.Cleaned &&
            string.Equals(State.CleanupReason, command.Reason.Trim(), StringComparison.Ordinal))
        {
            return;
        }

        await PersistDomainEventAsync(new NyxIdAuthorizationCatalogCleanedEvent
        {
            Owner = command.Owner.Clone(),
            CleanedAt = command.CleanedAt.Clone(),
            Reason = command.Reason.Trim(),
            LifecycleFence = checked(State.LifecycleFence + 1),
        });
    }

    protected override NyxIdAuthorizationCatalogState TransitionState(
        NyxIdAuthorizationCatalogState current,
        IMessage evt) => StateTransitionMatcher
        .Match(current, evt)
        .On<NyxIdAuthorizationCatalogActivatedEvent>(ApplyActivated)
        .On<NyxIdAuthorizationCatalogObservedEvent>(ApplyObserved)
        .On<NyxIdAuthorizationCatalogRefreshFailedEvent>(ApplyRefreshFailed)
        .On<NyxIdAuthorizationCatalogInvalidatedEvent>(ApplyInvalidated)
        .On<NyxIdAuthorizationCatalogCleanedEvent>(ApplyCleaned)
        .OrCurrent();

    private static NyxIdAuthorizationCatalogState ApplyActivated(
        NyxIdAuthorizationCatalogState state,
        NyxIdAuthorizationCatalogActivatedEvent evt)
    {
        var next = state.Clone();
        next.Owner ??= evt.Owner.Clone();
        next.Activated = true;
        next.ActivatedAt = evt.ActivatedAt.Clone();
        next.LifecycleFence = evt.LifecycleFence;
        return next;
    }

    private static NyxIdAuthorizationCatalogState ApplyObserved(
        NyxIdAuthorizationCatalogState state,
        NyxIdAuthorizationCatalogObservedEvent evt)
    {
        var next = new NyxIdAuthorizationCatalogState
        {
            Owner = evt.Owner.Clone(),
            ObservedAt = evt.ObservedAt.Clone(),
            FreshUntil = evt.FreshUntil.Clone(),
            ExternalRevision = evt.ExternalRevision,
            ContentDigest = evt.ContentDigest,
            Invalidated = false,
            InvalidationReason = string.Empty,
            LastRefreshFailedAt = state.LastRefreshFailedAt?.Clone(),
            LastRefreshFailureCode = state.LastRefreshFailureCode,
            LifecycleFence = evt.LifecycleFence,
            Activated = true,
            ActivatedAt = state.ActivatedAt?.Clone() ?? evt.ObservedAt.Clone(),
            Cleaned = false,
            CleanupReason = string.Empty,
        };
        next.Services.Add(evt.Services.Select(static service => service.Clone()));
        return next;
    }

    private static NyxIdAuthorizationCatalogState ApplyRefreshFailed(
        NyxIdAuthorizationCatalogState state,
        NyxIdAuthorizationCatalogRefreshFailedEvent evt)
    {
        var next = state.Clone();
        next.Owner ??= evt.Owner.Clone();
        next.LastRefreshFailedAt = evt.FailedAt.Clone();
        next.LastRefreshFailureCode = evt.FailureCode;
        return next;
    }

    private static NyxIdAuthorizationCatalogState ApplyInvalidated(
        NyxIdAuthorizationCatalogState state,
        NyxIdAuthorizationCatalogInvalidatedEvent evt)
    {
        var next = state.Clone();
        next.Owner ??= evt.Owner.Clone();
        next.Invalidated = true;
        next.InvalidationReason = evt.Reason;
        next.InvalidatedAt = evt.InvalidatedAt.Clone();
        next.LifecycleFence = evt.LifecycleFence;
        return next;
    }

    private static NyxIdAuthorizationCatalogState ApplyCleaned(
        NyxIdAuthorizationCatalogState state,
        NyxIdAuthorizationCatalogCleanedEvent evt) => new()
    {
        Owner = evt.Owner.Clone(),
        Invalidated = true,
        InvalidationReason = evt.Reason,
        InvalidatedAt = evt.CleanedAt.Clone(),
        LifecycleFence = evt.LifecycleFence,
        Activated = false,
        Cleaned = true,
        CleanedAt = evt.CleanedAt.Clone(),
        CleanupReason = evt.Reason,
    };

    private void EnsureCurrentOwner(AuthorizationOwnerIdentity owner)
    {
        if (State.Owner != null && !OwnerEquals(State.Owner, owner))
            throw new InvalidOperationException("NyxID authorization catalog owner cannot change.");
    }

    internal static void EnsureLifecycleFence(long currentLifecycleFence, long expectedLifecycleFence)
    {
        if (expectedLifecycleFence < 0 || expectedLifecycleFence != currentLifecycleFence)
        {
            throw new InvalidOperationException(
                "NyxID authorization catalog observation was superseded by a lifecycle change.");
        }
    }

    private static void ValidateObservation(ObserveNyxIdAuthorizationCatalogCommand command)
    {
        if (command.ObservedAt == null ||
            command.FreshUntil == null ||
            command.FreshUntil.CompareTo(command.ObservedAt) <= 0)
        {
            throw new InvalidOperationException("Catalog freshness interval is invalid.");
        }
        if (string.IsNullOrWhiteSpace(command.ContentDigest))
            throw new InvalidOperationException("Catalog content digest is required.");
        if (!string.Equals(
                command.ContentDigest.Trim(),
                NyxIdAuthorizationCatalogIntegrity.ComputeContentDigest(command.Owner, command.Services),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Catalog content digest does not match the typed owner topology.");
        }

        var serviceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var service in command.Services)
        {
            ValidateService(service);
            if (!serviceIds.Add(service.UserServiceId.Trim()))
                throw new InvalidOperationException("Catalog service identities must be unique.");
        }
    }

    private static void ValidateService(NyxIdAuthorizationServiceEvidence service)
    {
        if (string.IsNullOrWhiteSpace(service.UserServiceId) ||
            string.IsNullOrWhiteSpace(service.ServiceSlug) ||
            service.Access == NyxIdAuthorizationAccess.Unspecified ||
            !Enum.IsDefined(service.Access) ||
            service.NodeGrantRequirement == AuthorizationGrantRequirement.Unspecified ||
            !Enum.IsDefined(service.NodeGrantRequirement))
        {
            throw new InvalidOperationException("Catalog service authorization evidence is incomplete.");
        }

        var primaryNodeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in service.Nodes)
        {
            if (string.IsNullOrWhiteSpace(node.NodeId) ||
                node.Role == NyxIdNodeRole.Unspecified ||
                !Enum.IsDefined(node.Role) ||
                node.EdgeKind == NyxIdNodeEdgeKind.Unspecified ||
                !Enum.IsDefined(node.EdgeKind) ||
                node.EdgeKind == NyxIdNodeEdgeKind.NodeBinding &&
                string.IsNullOrWhiteSpace(node.BindingId) ||
                node.EdgeKind == NyxIdNodeEdgeKind.UserServicePrimary &&
                (!string.IsNullOrWhiteSpace(node.BindingId) || node.Role != NyxIdNodeRole.Primary))
            {
                throw new InvalidOperationException("Catalog node authorization evidence is invalid.");
            }
            if (node.Role == NyxIdNodeRole.Primary)
                primaryNodeIds.Add(node.NodeId.Trim());
        }

        if (service.NodeGrantRequirement == AuthorizationGrantRequirement.Required &&
            (service.Nodes.Count == 0 || primaryNodeIds.Count != 1))
        {
            throw new InvalidOperationException(
                "Node-backed catalog services require exactly one primary node and exact fallback identities.");
        }
        if (service.NodeGrantRequirement == AuthorizationGrantRequirement.NotRequired &&
            service.Nodes.Count != 0)
        {
            throw new InvalidOperationException("Direct catalog services cannot carry node authorization evidence.");
        }
    }

    private static void ValidateOwner(AuthorizationOwnerIdentity owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (string.IsNullOrWhiteSpace(owner.Authority) ||
            owner.OwnerKind == AuthorizationOwnerKind.Unspecified ||
            !Enum.IsDefined(owner.OwnerKind) ||
            string.IsNullOrWhiteSpace(owner.OwnerSubject))
        {
            throw new InvalidOperationException("Catalog owner identity is incomplete.");
        }
    }

    private static bool OwnerEquals(AuthorizationOwnerIdentity left, AuthorizationOwnerIdentity right) =>
        string.Equals(left.Authority.Trim(), right.Authority.Trim(), StringComparison.Ordinal) &&
        left.OwnerKind == right.OwnerKind &&
        string.Equals(left.OwnerSubject.Trim(), right.OwnerSubject.Trim(), StringComparison.Ordinal);
}
