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
    private const string RefreshSupersededFailureCode = "nyxid_catalog_refresh_superseded";
    private const NyxIdAuthorizationCatalogLifecycleFenceSemanticsVersion
        CurrentLifecycleFenceSemanticsVersion =
            NyxIdAuthorizationCatalogLifecycleFenceSemanticsVersion.TerminalFactsAdvanceFence;

    public static string ProjectionKind => DurableProjectionKind;

    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        if (State.Owner != null &&
            CurrentStateVersion() > 0 &&
            (int)State.LifecycleFenceSemanticsVersion <
            (int)CurrentLifecycleFenceSemanticsVersion)
        {
            var displacedRefreshId = State.ActiveRefreshId;
            var displacedRefreshStartedAt = State.ActiveRefreshStartedAt?.Clone()
                                            ?? State.ActivatedAt?.Clone()
                                            ?? new Google.Protobuf.WellKnownTypes.Timestamp();
            var migrationStateVersion = checked(CurrentStateVersion() + 1);
            var migrationEvents = new List<IMessage>
            {
                new NyxIdAuthorizationCatalogLifecycleFenceSemanticsMigratedEvent
                {
                    Owner = State.Owner.Clone(),
                    SemanticsVersion = CurrentLifecycleFenceSemanticsVersion,
                    LifecycleFence = checked(State.LifecycleFence + 1),
                },
            };
            if (!string.IsNullOrEmpty(displacedRefreshId))
            {
                migrationEvents.Add(BuildRefreshOutcome(
                    displacedRefreshId,
                    NyxIdAuthorizationCatalogRefreshOutcomeStatusState.Superseded,
                    migrationStateVersion,
                    displacedRefreshStartedAt,
                    RefreshSupersededFailureCode));
            }

            await PersistDomainEventsAsync(migrationEvents, ct);
        }

        await base.OnActivateAsync(ct);
    }

    [EventHandler(EndpointName = "beginCatalogRefresh")]
    public async Task HandleBeginRefreshAsync(BeginNyxIdAuthorizationCatalogRefreshCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateOwner(command.Owner);
        EnsureCurrentOwner(command.Owner);
        if (string.IsNullOrWhiteSpace(command.RefreshId) || command.StartedAt == null)
            throw new InvalidOperationException("Catalog refresh identity and start time are required.");
        if (command.ExpectedLifecycleFence < 0)
            throw new InvalidOperationException("Catalog refresh lifecycle fence is invalid.");

        var refreshId = command.RefreshId.Trim();
        if (command.ExpectedLifecycleFence != State.LifecycleFence)
        {
            await PersistRefreshOutcomeAsync(
                refreshId,
                NyxIdAuthorizationCatalogRefreshOutcomeStatusState.Superseded,
                command.StartedAt,
                RefreshSupersededFailureCode);
            return;
        }

        if (string.Equals(State.ActiveRefreshId, refreshId, StringComparison.Ordinal))
        {
            if (State.ActiveRefreshStartedAt?.Equals(command.StartedAt) != true)
                throw new InvalidOperationException("A catalog refresh identity cannot change its start time.");

            await PersistRefreshOutcomeAsync(
                refreshId,
                NyxIdAuthorizationCatalogRefreshOutcomeStatusState.Started,
                command.StartedAt,
                string.Empty);
            return;
        }

        if (!string.IsNullOrEmpty(State.ActiveRefreshId) &&
            State.ActiveRefreshStartedAt != null &&
            CompareRefreshOrder(
                command.StartedAt,
                refreshId,
                State.ActiveRefreshStartedAt,
                State.ActiveRefreshId) < 0)
        {
            await PersistRefreshOutcomeAsync(
                refreshId,
                NyxIdAuthorizationCatalogRefreshOutcomeStatusState.Superseded,
                command.StartedAt,
                RefreshSupersededFailureCode);
            return;
        }

        var transitionEvents = new List<IMessage>();
        if (!State.Activated)
        {
            transitionEvents.Add(new NyxIdAuthorizationCatalogActivatedEvent
            {
                Owner = command.Owner.Clone(),
                ActivatedAt = command.StartedAt.Clone(),
                LifecycleFence = State.LifecycleFence,
                LifecycleFenceSemanticsVersion = CurrentLifecycleFenceSemanticsVersion,
            });
        }

        transitionEvents.Add(new NyxIdAuthorizationCatalogRefreshBeganEvent
        {
            Owner = command.Owner.Clone(),
            RefreshId = refreshId,
            StartedAt = command.StartedAt.Clone(),
        });
        var mutationStateVersion = checked(CurrentStateVersion() + transitionEvents.Count);
        if (!string.IsNullOrEmpty(State.ActiveRefreshId))
        {
            transitionEvents.Add(BuildRefreshOutcome(
                State.ActiveRefreshId,
                NyxIdAuthorizationCatalogRefreshOutcomeStatusState.Superseded,
                mutationStateVersion,
                command.StartedAt,
                RefreshSupersededFailureCode));
        }
        transitionEvents.Add(BuildRefreshOutcome(
            refreshId,
            NyxIdAuthorizationCatalogRefreshOutcomeStatusState.Started,
            mutationStateVersion,
            command.StartedAt,
            string.Empty));
        await PersistDomainEventsAsync(transitionEvents);
    }

    [EventHandler(EndpointName = "observeCatalog")]
    public async Task HandleObserveAsync(ObserveNyxIdAuthorizationCatalogCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateOwner(command.Owner);
        EnsureCurrentOwner(command.Owner);
        var refreshId = NormalizeRefreshIdentity(command.RefreshId);
        if (command.ObservedAt == null)
            throw new InvalidOperationException("Catalog observation time is required.");
        if (!IsActiveRefresh(refreshId))
        {
            await PersistRefreshOutcomeAsync(
                refreshId,
                NyxIdAuthorizationCatalogRefreshOutcomeStatusState.Superseded,
                command.ObservedAt,
                RefreshSupersededFailureCode);
            return;
        }

        ValidateObservation(command);

        var observed = new NyxIdAuthorizationCatalogObservedEvent
        {
            Owner = command.Owner.Clone(),
            RefreshId = refreshId,
            ObservedAt = command.ObservedAt.Clone(),
            FreshUntil = command.FreshUntil.Clone(),
            ContractVersion = command.ContractVersion.Trim(),
            PolicyVersion = command.PolicyVersion.Trim(),
            EvaluatedAt = command.EvaluatedAt.Clone(),
            ContentDigest = command.ContentDigest.Trim(),
            LifecycleFence = checked(State.LifecycleFence + 1),
            LifecycleFenceSemanticsVersion = CurrentLifecycleFenceSemanticsVersion,
        };
        observed.Services.Add(command.Services.Select(static service => service.Clone()));
        await PersistRefreshTransitionAsync(
            observed,
            refreshId,
            NyxIdAuthorizationCatalogRefreshOutcomeStatusState.Observed,
            command.ObservedAt);
    }

    [EventHandler(EndpointName = "recordRefreshFailure")]
    public async Task HandleRefreshFailureAsync(RecordNyxIdAuthorizationCatalogRefreshFailureCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateOwner(command.Owner);
        EnsureCurrentOwner(command.Owner);
        if (command.FailedAt == null || string.IsNullOrWhiteSpace(command.FailureCode))
            throw new InvalidOperationException("Refresh failure time and code are required.");
        var refreshId = NormalizeRefreshIdentity(command.RefreshId);
        if (!IsActiveRefresh(refreshId))
        {
            await PersistRefreshOutcomeAsync(
                refreshId,
                NyxIdAuthorizationCatalogRefreshOutcomeStatusState.Superseded,
                command.FailedAt,
                RefreshSupersededFailureCode);
            return;
        }

        await PersistRefreshTransitionAsync(
            new NyxIdAuthorizationCatalogRefreshFailedEvent
            {
                Owner = command.Owner.Clone(),
                RefreshId = refreshId,
                FailedAt = command.FailedAt.Clone(),
                FailureCode = command.FailureCode.Trim(),
                LifecycleFence = checked(State.LifecycleFence + 1),
                LifecycleFenceSemanticsVersion = CurrentLifecycleFenceSemanticsVersion,
            },
            refreshId,
            NyxIdAuthorizationCatalogRefreshOutcomeStatusState.Failed,
            command.FailedAt,
            command.FailureCode.Trim());
    }

    [EventHandler(EndpointName = "invalidateCatalog")]
    public async Task HandleInvalidateAsync(InvalidateNyxIdAuthorizationCatalogCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateOwner(command.Owner);
        EnsureCurrentOwner(command.Owner);
        if (command.InvalidatedAt == null || string.IsNullOrWhiteSpace(command.Reason))
            throw new InvalidOperationException("Invalidation time and reason are required.");
        var refreshId = string.IsNullOrWhiteSpace(command.RefreshId)
            ? string.Empty
            : command.RefreshId.Trim();
        var displacedRefreshId = string.IsNullOrEmpty(refreshId)
            ? State.ActiveRefreshId
            : string.Empty;
        if (!string.IsNullOrEmpty(refreshId) && !IsActiveRefresh(refreshId))
        {
            await PersistRefreshOutcomeAsync(
                refreshId,
                NyxIdAuthorizationCatalogRefreshOutcomeStatusState.Superseded,
                command.InvalidatedAt,
                RefreshSupersededFailureCode);
            return;
        }
        if (!string.IsNullOrEmpty(refreshId) &&
            command.OutcomeStatus is not (
                NyxIdAuthorizationCatalogRefreshOutcomeStatusState.AccessDenied or
                NyxIdAuthorizationCatalogRefreshOutcomeStatusState.CatalogUnstable))
        {
            throw new InvalidOperationException("Refresh invalidation outcome status is invalid.");
        }
        var lifecycleFence = checked(State.LifecycleFence + 1);

        var invalidated = new NyxIdAuthorizationCatalogInvalidatedEvent
        {
            Owner = command.Owner.Clone(),
            InvalidatedAt = command.InvalidatedAt.Clone(),
            Reason = command.Reason.Trim(),
            LifecycleFence = lifecycleFence,
            LifecycleFenceSemanticsVersion = CurrentLifecycleFenceSemanticsVersion,
        };
        if (!string.IsNullOrEmpty(refreshId))
        {
            await PersistRefreshTransitionAsync(
                invalidated,
                refreshId,
                command.OutcomeStatus,
                command.InvalidatedAt,
                command.Reason.Trim());
        }
        else if (!string.IsNullOrEmpty(displacedRefreshId))
        {
            await PersistRefreshTransitionAsync(
                invalidated,
                displacedRefreshId,
                NyxIdAuthorizationCatalogRefreshOutcomeStatusState.Superseded,
                command.InvalidatedAt,
                RefreshSupersededFailureCode);
        }
        else
        {
            await PersistDomainEventAsync(invalidated);
        }
    }

    [EventHandler(EndpointName = "cleanupCatalog")]
    public async Task HandleCleanupAsync(CleanupNyxIdAuthorizationCatalogCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateOwner(command.Owner);
        EnsureCurrentOwner(command.Owner);
        if (command.CleanedAt == null || string.IsNullOrWhiteSpace(command.Reason))
            throw new InvalidOperationException("Cleanup time and reason are required.");
        var displacedRefreshId = State.ActiveRefreshId;
        var lifecycleFence = checked(State.LifecycleFence + 1);

        var cleaned = new NyxIdAuthorizationCatalogCleanedEvent
        {
            Owner = command.Owner.Clone(),
            CleanedAt = command.CleanedAt.Clone(),
            Reason = command.Reason.Trim(),
            LifecycleFence = lifecycleFence,
            LifecycleFenceSemanticsVersion = CurrentLifecycleFenceSemanticsVersion,
        };
        if (!string.IsNullOrEmpty(displacedRefreshId))
        {
            await PersistRefreshTransitionAsync(
                cleaned,
                displacedRefreshId,
                NyxIdAuthorizationCatalogRefreshOutcomeStatusState.Superseded,
                command.CleanedAt,
                RefreshSupersededFailureCode);
        }
        else
        {
            await PersistDomainEventAsync(cleaned);
        }
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
        .On<NyxIdAuthorizationCatalogLifecycleFenceSemanticsMigratedEvent>(ApplyLifecycleFenceSemanticsMigrated)
        .On<NyxIdAuthorizationCatalogRefreshOutcomeEvent>(ApplyRefreshOutcome)
        .OrCurrent();

    private static NyxIdAuthorizationCatalogState ApplyActivated(
        NyxIdAuthorizationCatalogState state,
        NyxIdAuthorizationCatalogActivatedEvent evt)
    {
        var next = state.Clone();
        next.Owner ??= evt.Owner.Clone();
        next.Activated = true;
        next.ActivatedAt = evt.ActivatedAt.Clone();
        next.LifecycleFence = Math.Max(state.LifecycleFence, evt.LifecycleFence);
        next.LifecycleFenceSemanticsVersion = LatestLifecycleFenceSemanticsVersion(
            state.LifecycleFenceSemanticsVersion,
            evt.LifecycleFenceSemanticsVersion);
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
            LifecycleFence = AdvanceTerminalLifecycleFence(state.LifecycleFence, evt.LifecycleFence),
            LifecycleFenceSemanticsVersion = LatestLifecycleFenceSemanticsVersion(
                state.LifecycleFenceSemanticsVersion,
                evt.LifecycleFenceSemanticsVersion),
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
        next.LifecycleFence = AdvanceTerminalLifecycleFence(state.LifecycleFence, evt.LifecycleFence);
        next.LifecycleFenceSemanticsVersion = LatestLifecycleFenceSemanticsVersion(
            state.LifecycleFenceSemanticsVersion,
            evt.LifecycleFenceSemanticsVersion);
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
        next.LifecycleFence = AdvanceTerminalLifecycleFence(state.LifecycleFence, evt.LifecycleFence);
        next.LifecycleFenceSemanticsVersion = LatestLifecycleFenceSemanticsVersion(
            state.LifecycleFenceSemanticsVersion,
            evt.LifecycleFenceSemanticsVersion);
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
        LifecycleFence = AdvanceTerminalLifecycleFence(state.LifecycleFence, evt.LifecycleFence),
        LifecycleFenceSemanticsVersion = LatestLifecycleFenceSemanticsVersion(
            state.LifecycleFenceSemanticsVersion,
            evt.LifecycleFenceSemanticsVersion),
        Activated = false,
        Cleaned = true,
        CleanedAt = evt.CleanedAt.Clone(),
        CleanupReason = evt.Reason,
    };

    private static long AdvanceTerminalLifecycleFence(long currentFence, long eventFence) =>
        Math.Max(eventFence, checked(currentFence + 1));

    private static NyxIdAuthorizationCatalogState ApplyLifecycleFenceSemanticsMigrated(
        NyxIdAuthorizationCatalogState state,
        NyxIdAuthorizationCatalogLifecycleFenceSemanticsMigratedEvent evt)
    {
        var next = state.Clone();
        next.Owner ??= evt.Owner.Clone();
        next.LifecycleFence = Math.Max(state.LifecycleFence, evt.LifecycleFence);
        next.LifecycleFenceSemanticsVersion = LatestLifecycleFenceSemanticsVersion(
            state.LifecycleFenceSemanticsVersion,
            evt.SemanticsVersion);
        next.ActiveRefreshId = string.Empty;
        next.ActiveRefreshStartedAt = null;
        return next;
    }

    private static NyxIdAuthorizationCatalogLifecycleFenceSemanticsVersion
        LatestLifecycleFenceSemanticsVersion(
            NyxIdAuthorizationCatalogLifecycleFenceSemanticsVersion current,
            NyxIdAuthorizationCatalogLifecycleFenceSemanticsVersion candidate) =>
            (int)candidate > (int)current ? candidate : current;

    private static NyxIdAuthorizationCatalogState ApplyRefreshOutcome(
        NyxIdAuthorizationCatalogState state,
        NyxIdAuthorizationCatalogRefreshOutcomeEvent evt)
    {
        if (evt.Status != NyxIdAuthorizationCatalogRefreshOutcomeStatusState.Superseded ||
            !string.Equals(state.ActiveRefreshId, evt.RefreshId, StringComparison.Ordinal))
        {
            return state;
        }

        var next = state.Clone();
        next.ActiveRefreshId = string.Empty;
        next.ActiveRefreshStartedAt = null;
        return next;
    }

    private void EnsureCurrentOwner(AuthorizationOwnerIdentity owner)
    {
        if (State.Owner != null && !OwnerEquals(State.Owner, owner))
            throw new InvalidOperationException("NyxID authorization catalog owner cannot change.");
    }

    private bool IsActiveRefresh(string refreshId) =>
        !string.IsNullOrEmpty(State.ActiveRefreshId) &&
        string.Equals(State.ActiveRefreshId, refreshId, StringComparison.Ordinal);

    private static string NormalizeRefreshIdentity(string refreshId)
    {
        if (string.IsNullOrWhiteSpace(refreshId))
            throw new InvalidOperationException("Catalog refresh identity is required.");
        return refreshId.Trim();
    }

    private static int CompareRefreshOrder(
        Google.Protobuf.WellKnownTypes.Timestamp leftStartedAt,
        string leftRefreshId,
        Google.Protobuf.WellKnownTypes.Timestamp rightStartedAt,
        string rightRefreshId)
    {
        var timeOrdering = leftStartedAt.CompareTo(rightStartedAt);
        return timeOrdering != 0
            ? timeOrdering
            : string.CompareOrdinal(leftRefreshId, rightRefreshId);
    }

    private Task PersistRefreshOutcomeAsync(
        string refreshId,
        NyxIdAuthorizationCatalogRefreshOutcomeStatusState status,
        Google.Protobuf.WellKnownTypes.Timestamp observedAt,
        string failureCode = "") => PersistDomainEventAsync(BuildRefreshOutcome(
        refreshId,
        status,
        CurrentStateVersion(),
        observedAt,
        failureCode));

    private Task PersistRefreshTransitionAsync(
        IMessage stateEvent,
        string refreshId,
        NyxIdAuthorizationCatalogRefreshOutcomeStatusState status,
        Google.Protobuf.WellKnownTypes.Timestamp observedAt,
        string failureCode = "")
    {
        ArgumentNullException.ThrowIfNull(stateEvent);
        var mutationStateVersion = checked(CurrentStateVersion() + 1);
        return PersistDomainEventsAsync(
        [
            stateEvent,
            BuildRefreshOutcome(
                refreshId,
                status,
                mutationStateVersion,
                observedAt,
                failureCode),
        ]);
    }

    private long CurrentStateVersion() =>
        (EventSourcing ?? throw new InvalidOperationException(
            "Event sourcing must be configured before observing a catalog refresh."))
        .CurrentVersion;

    private static NyxIdAuthorizationCatalogRefreshOutcomeEvent BuildRefreshOutcome(
        string refreshId,
        NyxIdAuthorizationCatalogRefreshOutcomeStatusState status,
        long stateVersion,
        Google.Protobuf.WellKnownTypes.Timestamp observedAt,
        string failureCode) => new()
    {
        RefreshId = refreshId,
        Status = status,
        StateVersion = stateVersion,
        FailureCode = failureCode,
        ObservedAtUtc = observedAt.Clone(),
    };

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
        if (!string.Equals(
                service.ResourceOwner.Authority,
                NyxIdAuthorizationAuthorities.NyxId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Catalog resource owner identity must use NyxID authority.");
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
