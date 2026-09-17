using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
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

        await BeginRefreshCoreAsync(
            command.Owner,
            command.RefreshId,
            command.StartedAt,
            command.ExpectedLifecycleFence);
    }

    [EventHandler(EndpointName = "beginCatalogRepairRefresh")]
    public async Task HandleBeginRepairRefreshAsync(
        BeginNyxIdAuthorizationCatalogRepairRefreshCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateOwner(command.Owner);
        EnsureCurrentOwner(command.Owner);
        if (string.IsNullOrWhiteSpace(command.RefreshId) || command.StartedAt == null)
            throw new InvalidOperationException("Catalog refresh identity and start time are required.");
        if (string.IsNullOrWhiteSpace(command.RepairRequestId))
            throw new InvalidOperationException("Catalog repair refresh identity is required.");
        if (command.MinimumSourceStateVersion <= 0)
            throw new InvalidOperationException("Catalog repair minimum source version is invalid.");
        if (CurrentStateVersion() < command.MinimumSourceStateVersion)
            throw new InvalidOperationException("NyxID authorization catalog repair source version changed.");

        await BeginRefreshCoreAsync(
            command.Owner,
            command.RefreshId,
            command.StartedAt,
            State.LifecycleFence);
    }

    private async Task BeginRefreshCoreAsync(
        AuthorizationOwnerIdentity owner,
        string refreshIdentity,
        Google.Protobuf.WellKnownTypes.Timestamp startedAt,
        long expectedLifecycleFence)
    {
        var refreshId = refreshIdentity.Trim();
        if (expectedLifecycleFence != State.LifecycleFence)
        {
            await PersistRefreshOutcomeAsync(
                refreshId,
                NyxIdAuthorizationCatalogRefreshOutcomeStatusState.Superseded,
                startedAt,
                RefreshSupersededFailureCode);
            return;
        }

        if (string.Equals(State.ActiveRefreshId, refreshId, StringComparison.Ordinal))
        {
            if (State.ActiveRefreshStartedAt?.Equals(startedAt) != true)
                throw new InvalidOperationException("A catalog refresh identity cannot change its start time.");

            await PersistRefreshOutcomeAsync(
                refreshId,
                NyxIdAuthorizationCatalogRefreshOutcomeStatusState.Started,
                startedAt,
                string.Empty);
            return;
        }

        if (!string.IsNullOrEmpty(State.ActiveRefreshId) &&
            State.ActiveRefreshStartedAt != null &&
            CompareRefreshOrder(
                startedAt,
                refreshId,
                State.ActiveRefreshStartedAt,
                State.ActiveRefreshId) < 0)
        {
            await PersistRefreshOutcomeAsync(
                refreshId,
                NyxIdAuthorizationCatalogRefreshOutcomeStatusState.Superseded,
                startedAt,
                RefreshSupersededFailureCode);
            return;
        }

        var transitionEvents = new List<IMessage>();
        if (!State.Activated)
        {
            transitionEvents.Add(new NyxIdAuthorizationCatalogActivatedEvent
            {
                Owner = owner.Clone(),
                ActivatedAt = startedAt.Clone(),
                LifecycleFence = State.LifecycleFence,
                LifecycleFenceSemanticsVersion = CurrentLifecycleFenceSemanticsVersion,
            });
        }

        transitionEvents.Add(new NyxIdAuthorizationCatalogRefreshBeganEvent
        {
            Owner = owner.Clone(),
            RefreshId = refreshId,
            StartedAt = startedAt.Clone(),
        });
        var mutationStateVersion = checked(CurrentStateVersion() + transitionEvents.Count);
        if (!string.IsNullOrEmpty(State.ActiveRefreshId))
        {
            transitionEvents.Add(BuildRefreshOutcome(
                State.ActiveRefreshId,
                NyxIdAuthorizationCatalogRefreshOutcomeStatusState.Superseded,
                mutationStateVersion,
                startedAt,
                RefreshSupersededFailureCode));
        }
        transitionEvents.Add(BuildRefreshOutcome(
            refreshId,
            NyxIdAuthorizationCatalogRefreshOutcomeStatusState.Started,
            mutationStateVersion,
            startedAt,
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

        var coverageKind = ResolveObservationCoverageKind(command.CoverageKind);
        var contentDigest = coverageKind == NyxIdAuthorizationCatalogObservationCoverageKind.RequiredServiceSubset
            ? NyxIdAuthorizationCatalogIntegrity.ComputeContentDigest(
                command.Owner,
                MergeServices(State.Services, command.Services),
                command.GatewayLlmTarget ?? State.GatewayLlmTarget)
            : command.ContentDigest.Trim();
        var observed = new NyxIdAuthorizationCatalogObservedEvent
        {
            Owner = command.Owner.Clone(),
            RefreshId = refreshId,
            ObservedAt = command.ObservedAt.Clone(),
            FreshUntil = command.FreshUntil.Clone(),
            ContractVersion = command.ContractVersion.Trim(),
            PolicyVersion = command.PolicyVersion.Trim(),
            EvaluatedAt = command.EvaluatedAt.Clone(),
            ContentDigest = contentDigest,
            LifecycleFence = checked(State.LifecycleFence + 1),
            LifecycleFenceSemanticsVersion = CurrentLifecycleFenceSemanticsVersion,
            CoverageKind = coverageKind,
        };
        observed.Services.Add(command.Services.Select(static service => service.Clone()));
        if (command.GatewayLlmTarget != null)
            observed.GatewayLlmTarget = command.GatewayLlmTarget.Clone();
        observed.CoveredUserServiceIds.Add(command.CoveredUserServiceIds);
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
        var outcomeStatus = ResolveRefreshFailureOutcomeStatus(command.OutcomeStatus);
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
            outcomeStatus,
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
        var coverageKind = ResolveObservationCoverageKind(evt.CoverageKind);
        var next = coverageKind == NyxIdAuthorizationCatalogObservationCoverageKind.RequiredServiceSubset
            ? state.Clone()
            : new NyxIdAuthorizationCatalogState();
        var updatesOwnerCatalogStamp = coverageKind == NyxIdAuthorizationCatalogObservationCoverageKind.FullOwner ||
                                       !HasOwnerCatalogStamp(state);
        next.Owner = evt.Owner.Clone();
        next.ObservedAt = updatesOwnerCatalogStamp ? evt.ObservedAt.Clone() : state.ObservedAt?.Clone();
        next.FreshUntil = updatesOwnerCatalogStamp ? evt.FreshUntil.Clone() : state.FreshUntil?.Clone();
        next.ContractVersion = updatesOwnerCatalogStamp ? evt.ContractVersion : state.ContractVersion;
        next.PolicyVersion = updatesOwnerCatalogStamp ? evt.PolicyVersion : state.PolicyVersion;
        next.EvaluatedAt = updatesOwnerCatalogStamp ? evt.EvaluatedAt.Clone() : state.EvaluatedAt?.Clone();
        next.ContentDigest = evt.ContentDigest;
        next.Invalidated = false;
        next.InvalidationReason = string.Empty;
        next.LastRefreshFailedAt = state.LastRefreshFailedAt?.Clone();
        next.LastRefreshFailureCode = state.LastRefreshFailureCode;
        next.LifecycleFence = AdvanceTerminalLifecycleFence(state.LifecycleFence, evt.LifecycleFence);
        next.LifecycleFenceSemanticsVersion = LatestLifecycleFenceSemanticsVersion(
            state.LifecycleFenceSemanticsVersion,
            evt.LifecycleFenceSemanticsVersion);
        next.Activated = true;
        next.ActivatedAt = state.ActivatedAt?.Clone() ?? evt.ObservedAt.Clone();
        next.Cleaned = false;
        next.CleanupReason = string.Empty;
        next.ActiveRefreshId = string.Empty;
        next.ActiveRefreshStartedAt = null;
        next.Services.Clear();
        next.Services.Add(coverageKind == NyxIdAuthorizationCatalogObservationCoverageKind.RequiredServiceSubset
            ? MergeServices(state.Services, evt.Services)
            : evt.Services.Select(static service => service.Clone()));
        next.GatewayLlmTarget = coverageKind == NyxIdAuthorizationCatalogObservationCoverageKind.RequiredServiceSubset
            ? evt.GatewayLlmTarget?.Clone() ?? state.GatewayLlmTarget?.Clone()
            : evt.GatewayLlmTarget?.Clone();
        return next;
    }

    private static bool HasOwnerCatalogStamp(NyxIdAuthorizationCatalogState state) =>
        state.ObservedAt != null &&
        state.FreshUntil != null &&
        !string.IsNullOrWhiteSpace(state.ContractVersion) &&
        !string.IsNullOrWhiteSpace(state.PolicyVersion) &&
        state.EvaluatedAt != null;

    private static IReadOnlyList<NyxIdAuthorizationServiceEvidence> MergeServices(
        IEnumerable<NyxIdAuthorizationServiceEvidence> existingServices,
        IEnumerable<NyxIdAuthorizationServiceEvidence> observedServices)
    {
        var services = existingServices
            .Select(static service => service.Clone())
            .ToDictionary(static service => service.UserServiceId.Trim(), StringComparer.Ordinal);
        foreach (var service in observedServices)
            services[service.UserServiceId.Trim()] = service.Clone();
        return services.Values
            .OrderBy(static service => service.UserServiceId, StringComparer.Ordinal)
            .ToArray();
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

    private static NyxIdAuthorizationCatalogRefreshOutcomeStatusState ResolveRefreshFailureOutcomeStatus(
        NyxIdAuthorizationCatalogRefreshOutcomeStatusState outcomeStatus) => outcomeStatus switch
    {
        NyxIdAuthorizationCatalogRefreshOutcomeStatusState.Unspecified =>
            NyxIdAuthorizationCatalogRefreshOutcomeStatusState.Failed,
        NyxIdAuthorizationCatalogRefreshOutcomeStatusState.Failed =>
            NyxIdAuthorizationCatalogRefreshOutcomeStatusState.Failed,
        NyxIdAuthorizationCatalogRefreshOutcomeStatusState.CatalogUnstable =>
            NyxIdAuthorizationCatalogRefreshOutcomeStatusState.CatalogUnstable,
        _ => throw new InvalidOperationException("Refresh failure outcome status is invalid."),
    };

    private static NyxIdAuthorizationCatalogObservationCoverageKind ResolveObservationCoverageKind(
        NyxIdAuthorizationCatalogObservationCoverageKind coverageKind) =>
        coverageKind == NyxIdAuthorizationCatalogObservationCoverageKind.Unspecified
            ? NyxIdAuthorizationCatalogObservationCoverageKind.FullOwner
            : coverageKind;

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
        var coverageKind = ResolveObservationCoverageKind(command.CoverageKind);
        if (coverageKind == NyxIdAuthorizationCatalogObservationCoverageKind.FullOwner)
        {
            if (command.CoveredUserServiceIds.Count != 0)
                throw new InvalidOperationException("Full catalog observations cannot carry a covered service subset.");
            if (string.IsNullOrWhiteSpace(command.ContentDigest))
                throw new InvalidOperationException("Catalog content digest is required.");
            if (!string.Equals(
                    command.ContentDigest.Trim(),
                    NyxIdAuthorizationCatalogIntegrity.ComputeContentDigest(
                        command.Owner,
                        command.Services,
                        command.GatewayLlmTarget),
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Catalog content digest does not match the typed authorization evidence.");
            }
        }
        else
        {
            if (command.CoveredUserServiceIds.Count == 0 && command.Services.Count == 0)
            {
                if (command.GatewayLlmTarget == null)
                    throw new InvalidOperationException(
                        "Targeted catalog observations require service or Gateway LLM evidence.");
            }
            else
            {
                ValidateCoveredServiceIds(command.CoveredUserServiceIds, command.Services);
            }
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
        if (command.GatewayLlmTarget != null)
            ValidateLLMTarget(command.GatewayLlmTarget, null);
    }

    private static void ValidateCoveredServiceIds(
        IEnumerable<string> coveredUserServiceIds,
        IEnumerable<NyxIdAuthorizationServiceEvidence> services)
    {
        var covered = coveredUserServiceIds.ToArray();
        if (covered.Length == 0)
            throw new InvalidOperationException("Targeted catalog observations require covered service identities.");
        string? previousCoveredServiceId = null;
        foreach (var serviceId in covered)
        {
            if (string.IsNullOrWhiteSpace(serviceId) ||
                !string.Equals(serviceId, serviceId.Trim(), StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Covered catalog service identities must be normalized.");
            }
            if (previousCoveredServiceId != null &&
                string.CompareOrdinal(previousCoveredServiceId, serviceId) >= 0)
            {
                throw new InvalidOperationException("Covered catalog service identities must be ordinal-sorted and unique.");
            }
            previousCoveredServiceId = serviceId;
        }

        var observed = services.Select(static service => service.UserServiceId).ToArray();
        if (!covered.SequenceEqual(observed, StringComparer.Ordinal))
            throw new InvalidOperationException("Targeted catalog observations must cover exactly the observed services.");
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

        if (NyxIdAuthorizationCatalogIntegrity.HasServiceAuthorityStamp(service))
        {
            var authorityWindow =
                NyxIdAuthorizationCatalogIntegrity.ResolveServiceAuthorityStamp(service);
            if (authorityWindow.Status ==
                NyxIdAuthorizationServiceAuthorityWindowStatus.Incomplete)
            {
                throw new InvalidOperationException(
                    "Catalog service authority evidence is incomplete.");
            }
            if (!authorityWindow.Ready)
            {
                throw new InvalidOperationException(
                    "Catalog service authority evidence is invalid.");
            }
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

        if (service.LlmTarget != null)
            ValidateLLMTarget(service.LlmTarget, service);
    }

    private static void ValidateLLMTarget(
        NyxIdAuthorizationLLMTargetEvidence target,
        NyxIdAuthorizationServiceEvidence? parentService)
    {
        if (target.ModelCatalog == null)
            throw new InvalidOperationException("LLM target model catalog is required.");
        LLMSelectionPolicy.ValidateCatalog(target.ModelCatalog);

        if (target.ObservedAt == null ||
            target.FreshUntil == null ||
            target.FreshUntil.CompareTo(target.ObservedAt) <= 0 ||
            target.EvaluatedAt == null ||
            string.IsNullOrWhiteSpace(target.AuthorityContractVersion) ||
            !string.Equals(
                target.AuthorityContractVersion,
                target.AuthorityContractVersion.Trim(),
                StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(target.AuthorityPolicyVersion) ||
            !string.Equals(
                target.AuthorityPolicyVersion,
                target.AuthorityPolicyVersion.Trim(),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("LLM target authority evidence is incomplete.");
        }

        switch (target.RouteKind)
        {
            case LLMRouteKind.Gateway when parentService == null:
                if (!string.Equals(
                        target.RouteValue,
                        LLMSelectionPolicy.GatewayRoute,
                        StringComparison.Ordinal) ||
                    target.NyxIdUserServiceId.Length != 0 ||
                    target.ServiceSlugSnapshot.Length != 0)
                {
                    throw new InvalidOperationException("Gateway LLM target identity is invalid.");
                }
                return;
            case LLMRouteKind.NyxIdUserService when parentService != null:
                if (!string.Equals(
                        target.NyxIdUserServiceId,
                        parentService.UserServiceId,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        target.ServiceSlugSnapshot,
                        parentService.ServiceSlug,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        target.RouteValue,
                        $"/api/v1/proxy/s/{parentService.ServiceSlug}",
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Service LLM target identity does not match its parent service.");
                }
                return;
            default:
                throw new InvalidOperationException("LLM target route kind is invalid for its catalog owner.");
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
