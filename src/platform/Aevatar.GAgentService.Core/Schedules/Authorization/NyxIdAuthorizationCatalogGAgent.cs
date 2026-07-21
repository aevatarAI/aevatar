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

    [EventHandler(EndpointName = "beginCatalogRefresh")]
    public async Task HandleBeginRefreshAsync(BeginNyxIdAuthorizationCatalogRefreshCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateOwner(command.Owner);
        EnsureCurrentOwner(command.Owner);
        if (!State.Activated)
            throw new InvalidOperationException("Catalog must be activated before refresh begins.");
        if (string.IsNullOrWhiteSpace(command.RefreshId) || command.StartedAt == null)
            throw new InvalidOperationException("Catalog refresh identity and start time are required.");

        var refreshId = command.RefreshId.Trim();
        if (string.Equals(State.ActiveRefreshId, refreshId, StringComparison.Ordinal))
        {
            if (State.ActiveRefreshStartedAt?.Equals(command.StartedAt) == true)
                return;
            throw new InvalidOperationException("An active catalog refresh identity cannot change its start time.");
        }

        await PersistDomainEventAsync(new NyxIdAuthorizationCatalogRefreshBeganEvent
        {
            Owner = command.Owner.Clone(),
            RefreshId = refreshId,
            StartedAt = command.StartedAt.Clone(),
        });
    }

    [EventHandler(EndpointName = "observeCatalog")]
    public async Task HandleObserveAsync(ObserveNyxIdAuthorizationCatalogCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateOwner(command.Owner);
        ValidateObservation(command);
        EnsureCurrentOwner(command.Owner);
        EnsureActiveRefresh(command.RefreshId);

        if (State.ObservedAt != null)
        {
            var ordering = command.ObservedAt.CompareTo(State.ObservedAt);
            if (ordering < 0)
                return;
            if (ordering == 0)
            {
                if (string.Equals(State.ContentDigest, command.ContentDigest, StringComparison.Ordinal) &&
                    State.FreshUntil?.Equals(command.FreshUntil) == true &&
                    string.Equals(State.ContractVersion, command.ContractVersion, StringComparison.Ordinal) &&
                    string.Equals(State.PolicyVersion, command.PolicyVersion, StringComparison.Ordinal) &&
                    State.EvaluatedAt?.Equals(command.EvaluatedAt) == true)
                {
                    // A distinct refresh may confirm the same provider snapshot.
                }
                else
                {
                    throw new InvalidOperationException(
                        "A NyxID authorization catalog observation timestamp cannot identify conflicting content.");
                }
            }
        }

        var observed = new NyxIdAuthorizationCatalogObservedEvent
        {
            Owner = command.Owner.Clone(),
            RefreshId = command.RefreshId.Trim(),
            ObservedAt = command.ObservedAt.Clone(),
            FreshUntil = command.FreshUntil.Clone(),
            ContractVersion = command.ContractVersion.Trim(),
            PolicyVersion = command.PolicyVersion.Trim(),
            EvaluatedAt = command.EvaluatedAt.Clone(),
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
        EnsureActiveRefresh(command.RefreshId);
        if (command.FailedAt == null || string.IsNullOrWhiteSpace(command.FailureCode))
            throw new InvalidOperationException("Refresh failure time and code are required.");

        await PersistDomainEventAsync(new NyxIdAuthorizationCatalogRefreshFailedEvent
        {
            Owner = command.Owner.Clone(),
            RefreshId = command.RefreshId.Trim(),
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
        if (!string.IsNullOrWhiteSpace(command.RefreshId))
            EnsureActiveRefresh(command.RefreshId);
        if (command.InvalidatedAt == null || string.IsNullOrWhiteSpace(command.Reason))
            throw new InvalidOperationException("Invalidation time and reason are required.");
        if (State.Invalidated &&
            string.Equals(State.InvalidationReason, command.Reason.Trim(), StringComparison.Ordinal) &&
            string.IsNullOrEmpty(State.ActiveRefreshId))
        {
            return;
        }

        var lifecycleFence = State.Invalidated &&
                             string.Equals(State.InvalidationReason, command.Reason.Trim(), StringComparison.Ordinal)
            ? State.LifecycleFence
            : checked(State.LifecycleFence + 1);

        await PersistDomainEventAsync(new NyxIdAuthorizationCatalogInvalidatedEvent
        {
            Owner = command.Owner.Clone(),
            InvalidatedAt = command.InvalidatedAt.Clone(),
            Reason = command.Reason.Trim(),
            LifecycleFence = lifecycleFence,
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
            string.Equals(State.CleanupReason, command.Reason.Trim(), StringComparison.Ordinal) &&
            string.IsNullOrEmpty(State.ActiveRefreshId))
        {
            return;
        }

        var lifecycleFence = State.Cleaned &&
                             string.Equals(State.CleanupReason, command.Reason.Trim(), StringComparison.Ordinal)
            ? State.LifecycleFence
            : checked(State.LifecycleFence + 1);

        await PersistDomainEventAsync(new NyxIdAuthorizationCatalogCleanedEvent
        {
            Owner = command.Owner.Clone(),
            CleanedAt = command.CleanedAt.Clone(),
            Reason = command.Reason.Trim(),
            LifecycleFence = lifecycleFence,
        });
    }

    protected override NyxIdAuthorizationCatalogState TransitionState(
        NyxIdAuthorizationCatalogState current,
        IMessage evt) => StateTransitionMatcher
        .Match(current, evt)
        .On<NyxIdAuthorizationCatalogActivatedEvent>(ApplyActivated)
        .On<NyxIdAuthorizationCatalogRefreshBeganEvent>(ApplyRefreshBegan)
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

    private static NyxIdAuthorizationCatalogState ApplyRefreshBegan(
        NyxIdAuthorizationCatalogState state,
        NyxIdAuthorizationCatalogRefreshBeganEvent evt)
    {
        var next = state.Clone();
        next.Owner ??= evt.Owner.Clone();
        next.ActiveRefreshId = evt.RefreshId;
        next.ActiveRefreshStartedAt = evt.StartedAt.Clone();
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
            ContractVersion = evt.ContractVersion,
            PolicyVersion = evt.PolicyVersion,
            EvaluatedAt = evt.EvaluatedAt.Clone(),
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
        next.ActiveRefreshId = string.Empty;
        next.ActiveRefreshStartedAt = null;
        if (next.LastRefreshFailedAt == null || evt.FailedAt.CompareTo(next.LastRefreshFailedAt) > 0)
        {
            next.LastRefreshFailedAt = evt.FailedAt.Clone();
            next.LastRefreshFailureCode = evt.FailureCode;
        }
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
        next.ActiveRefreshId = string.Empty;
        next.ActiveRefreshStartedAt = null;
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

    private void EnsureActiveRefresh(string refreshId)
    {
        if (string.IsNullOrWhiteSpace(refreshId) ||
            string.IsNullOrEmpty(State.ActiveRefreshId) ||
            !string.Equals(State.ActiveRefreshId, refreshId.Trim(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Catalog result does not match the active refresh.");
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
        if (command.EvaluatedAt == null ||
            string.IsNullOrWhiteSpace(command.ContractVersion) ||
            !string.Equals(command.ContractVersion, command.ContractVersion.Trim(), StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(command.PolicyVersion) ||
            !string.Equals(command.PolicyVersion, command.PolicyVersion.Trim(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Catalog provider contract evidence is incomplete.");
        }
        if (string.IsNullOrWhiteSpace(command.ContentDigest))
            throw new InvalidOperationException("Catalog content digest is required.");
        if (!string.Equals(
                command.ContentDigest.Trim(),
                NyxIdAuthorizationCatalogIntegrity.ComputeContentDigest(command.Owner, command.Services),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Catalog content digest does not match the typed authorization evidence.");
        }

        string? previousServiceId = null;
        foreach (var service in command.Services)
        {
            ValidateService(service);
            var serviceId = service.UserServiceId;
            if (previousServiceId != null)
            {
                var ordering = string.CompareOrdinal(previousServiceId, serviceId);
                if (ordering == 0)
                    throw new InvalidOperationException("Catalog service identities must be unique.");
                if (ordering > 0)
                    throw new InvalidOperationException("Catalog service identities must be ordinal-sorted.");
            }
            previousServiceId = serviceId;
        }
    }

    private static void ValidateService(NyxIdAuthorizationServiceEvidence service)
    {
        if (string.IsNullOrWhiteSpace(service.UserServiceId) ||
            !string.Equals(service.UserServiceId, service.UserServiceId.Trim(), StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(service.ServiceSlug) ||
            !string.Equals(service.ServiceSlug, service.ServiceSlug.Trim(), StringComparison.Ordinal) ||
            service.Access != NyxIdAuthorizationAccess.Permitted ||
            service.NodeGrantRequirement == AuthorizationGrantRequirement.Unspecified ||
            !Enum.IsDefined(service.NodeGrantRequirement))
        {
            throw new InvalidOperationException("Catalog service authorization evidence is incomplete.");
        }

        if (service.ResourceOwner == null ||
            string.IsNullOrWhiteSpace(service.ResourceOwner.Authority) ||
            !string.Equals(
                service.ResourceOwner.Authority,
                service.ResourceOwner.Authority.Trim(),
                StringComparison.Ordinal) ||
            service.ResourceOwner.OwnerKind == AuthorizationOwnerKind.Unspecified ||
            !Enum.IsDefined(service.ResourceOwner.OwnerKind) ||
            string.IsNullOrWhiteSpace(service.ResourceOwner.OwnerSubject) ||
            !string.Equals(
                service.ResourceOwner.OwnerSubject,
                service.ResourceOwner.OwnerSubject.Trim(),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Catalog resource owner identity is incomplete.");
        }

        string? previousNodeId = null;
        foreach (var nodeId in service.NodeIds)
        {
            if (string.IsNullOrWhiteSpace(nodeId) ||
                !string.Equals(nodeId, nodeId.Trim(), StringComparison.Ordinal) ||
                previousNodeId != null && string.CompareOrdinal(previousNodeId, nodeId) >= 0)
            {
                throw new InvalidOperationException(
                    "Catalog node identities must be ordinal-sorted and unique.");
            }
            previousNodeId = nodeId;
        }

        if (service.NodeGrantRequirement == AuthorizationGrantRequirement.Required &&
            service.NodeIds.Count == 0)
        {
            throw new InvalidOperationException(
                "Node-backed catalog services require at least one node identity.");
        }
        if (service.NodeGrantRequirement == AuthorizationGrantRequirement.NotRequired &&
            service.NodeIds.Count != 0)
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
