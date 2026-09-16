using System.Reflection;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.GAgentService.Core.Schedules.Authorization;
using Aevatar.GAgentService.Projection.Contexts;
using Aevatar.GAgentService.Projection.Projectors;
using Aevatar.GAgentService.Projection.Queries;
using Aevatar.GAgentService.Projection.ReadModels;
using Aevatar.GAgentService.Tests.Projection;
using Aevatar.GAgentService.Tests.TestSupport;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgentService.Tests.Authorization;

public sealed class NyxIdAuthorizationCatalogLifecycleTests
{
    private static readonly DateTimeOffset ObservedAt = DateTimeOffset.Parse("2026-07-16T00:00:00Z");

    [Fact]
    public async Task CommandHandlers_ShouldOwnRefreshLifecycleAndRecoverAfterInvalidation()
    {
        var owner = Owner();
        var agent = CreateAgent(owner);

        await BeginRefreshAsync(agent, owner, "refresh-1", ObservedAt.AddSeconds(1));
        var observation = ObservationCommand(owner, "refresh-1", ObservedAt.AddMinutes(1));
        await agent.HandleObserveAsync(observation);
        agent.State.ActiveRefreshId.Should().BeEmpty();

        await BeginRefreshAsync(
            agent,
            owner,
            "refresh-failure",
            ObservedAt.AddMinutes(2),
            agent.State.LifecycleFence);
        var refreshFailure = new RecordNyxIdAuthorizationCatalogRefreshFailureCommand
        {
            Owner = owner.Clone(),
            RefreshId = "refresh-failure",
            FailedAt = Timestamp.FromDateTimeOffset(ObservedAt.AddMinutes(2)),
            FailureCode = "provider_unavailable",
        };
        await agent.HandleRefreshFailureAsync(refreshFailure);
        agent.State.LastRefreshFailureCode.Should().Be("provider_unavailable");
        agent.State.ActiveRefreshId.Should().BeEmpty();

        await BeginRefreshAsync(
            agent,
            owner,
            "refresh-invalidated",
            ObservedAt.AddMinutes(3),
            agent.State.LifecycleFence);
        var invalidation = new InvalidateNyxIdAuthorizationCatalogCommand
        {
            Owner = owner.Clone(),
            InvalidatedAt = Timestamp.FromDateTimeOffset(ObservedAt.AddMinutes(3)),
            Reason = "credential_revoked",
        };
        await agent.HandleInvalidateAsync(invalidation);
        await agent.HandleInvalidateAsync(invalidation.Clone());
        agent.State.LifecycleFence.Should().Be(4);
        agent.State.ActiveRefreshId.Should().BeEmpty();

        await BeginRefreshAsync(
            agent,
            owner,
            "refresh-recovery",
            ObservedAt.AddMinutes(4),
            agent.State.LifecycleFence);
        await agent.HandleObserveAsync(
            ObservationCommand(owner, "refresh-recovery", ObservedAt.AddMinutes(5)));

        agent.State.Invalidated.Should().BeFalse();
        agent.State.LifecycleFence.Should().Be(5);
        agent.State.ContractVersion.Should().Be("1");
        agent.State.PolicyVersion.Should().Be("api-key-scope-v1");
        agent.State.EvaluatedAt.Should().Be(Timestamp.FromDateTimeOffset(ObservedAt.AddMinutes(4)));

        var cleanup = new CleanupNyxIdAuthorizationCatalogCommand
        {
            Owner = owner.Clone(),
            CleanedAt = Timestamp.FromDateTimeOffset(ObservedAt.AddMinutes(6)),
            Reason = "owner_unbound",
        };
        await agent.HandleCleanupAsync(cleanup);
        await agent.HandleCleanupAsync(cleanup.Clone());

        agent.State.Cleaned.Should().BeTrue();
        agent.State.CleanupReason.Should().Be("owner_unbound");
        agent.State.Activated.Should().BeFalse();
        agent.State.LifecycleFence.Should().Be(7);
    }

    [Fact]
    public async Task CatalogUnstableRefreshFailure_ShouldNotInvalidateOwnerCatalog()
    {
        var owner = Owner();
        var agent = CreateAgent(owner);

        await BeginRefreshAsync(agent, owner, "refresh-1", ObservedAt.AddSeconds(1));
        await agent.HandleObserveAsync(ObservationCommand(owner, "refresh-1", ObservedAt.AddMinutes(1)));

        await BeginRefreshAsync(
            agent,
            owner,
            "refresh-scoped-miss",
            ObservedAt.AddMinutes(2),
            agent.State.LifecycleFence);
        await agent.HandleRefreshFailureAsync(new RecordNyxIdAuthorizationCatalogRefreshFailureCommand
        {
            Owner = owner.Clone(),
            RefreshId = "refresh-scoped-miss",
            FailedAt = Timestamp.FromDateTimeOffset(ObservedAt.AddMinutes(2)),
            FailureCode = "nyxid_required_service_not_found:svc-missing",
            OutcomeStatus = NyxIdAuthorizationCatalogRefreshOutcomeStatusState.CatalogUnstable,
        });

        agent.State.Invalidated.Should().BeFalse();
        agent.State.InvalidationReason.Should().BeEmpty();
        agent.State.ActiveRefreshId.Should().BeEmpty();
        agent.State.LastRefreshFailureCode.Should().Be("nyxid_required_service_not_found:svc-missing");
        agent.State.Services.Select(static service => service.UserServiceId).Should().Equal("svc-alpha");
    }

    [Fact]
    public async Task RefreshSession_ShouldFenceDelayedOlderBeginWhileNewerRefreshIsActive()
    {
        var owner = Owner();
        var eventStore = new InMemoryEventStore();
        var agent = CreateAgent(owner, eventStore);
        await BeginRefreshAsync(agent, owner, "refresh-newer", ObservedAt.AddSeconds(2));

        await BeginRefreshAsync(agent, owner, "refresh-older", ObservedAt.AddSeconds(1));

        agent.State.ActiveRefreshId.Should().Be("refresh-newer");
        agent.State.ActiveRefreshStartedAt.Should()
            .Be(Timestamp.FromDateTimeOffset(ObservedAt.AddSeconds(2)));
        await AssertLastRefreshOutcomeAsync(
            eventStore,
            agent.Id,
            "refresh-older",
            NyxIdAuthorizationCatalogRefreshOutcomeStatusState.Superseded);
    }

    [Fact]
    public async Task RefreshAcquire_ShouldFenceOldEpochAfterCleanupAndAllowCurrentEpochRecovery()
    {
        var owner = Owner();
        var eventStore = new InMemoryEventStore();
        var agent = CreateAgent(owner, eventStore);
        var oldEpoch = agent.State.LifecycleFence;
        await agent.HandleCleanupAsync(new CleanupNyxIdAuthorizationCatalogCommand
        {
            Owner = owner.Clone(),
            CleanedAt = Timestamp.FromDateTimeOffset(ObservedAt.AddSeconds(1)),
            Reason = "owner_unbound",
        });

        await BeginRefreshAsync(
            agent,
            owner,
            "refresh-old-epoch",
            ObservedAt,
            oldEpoch);
        await agent.HandleObserveAsync(
            ObservationCommand(owner, "refresh-old-epoch", ObservedAt.AddSeconds(2)));
        await agent.HandleRefreshFailureAsync(new RecordNyxIdAuthorizationCatalogRefreshFailureCommand
        {
            Owner = owner.Clone(),
            RefreshId = "refresh-old-epoch",
            FailedAt = Timestamp.FromDateTimeOffset(ObservedAt.AddSeconds(3)),
            FailureCode = "provider_unavailable",
        });

        agent.State.Cleaned.Should().BeTrue();
        agent.State.Activated.Should().BeFalse();
        agent.State.ActiveRefreshId.Should().BeEmpty();
        agent.State.Services.Should().BeEmpty();
        agent.State.LastRefreshFailureCode.Should().BeEmpty();
        (await RefreshOutcomesAsync(eventStore, agent.Id, "refresh-old-epoch"))
            .Should().HaveCount(3)
            .And.OnlyContain(static outcome =>
                outcome.Status == NyxIdAuthorizationCatalogRefreshOutcomeStatusState.Superseded);

        await BeginRefreshAsync(
            agent,
            owner,
            "refresh-current-epoch",
            ObservedAt.AddSeconds(4),
            agent.State.LifecycleFence);
        await agent.HandleObserveAsync(
            ObservationCommand(owner, "refresh-current-epoch", ObservedAt.AddSeconds(5)));

        agent.State.Cleaned.Should().BeFalse();
        agent.State.Activated.Should().BeTrue();
        agent.State.Services.Should().ContainSingle();
        (await RefreshOutcomesAsync(eventStore, agent.Id, "refresh-current-epoch"))
            .Select(static outcome => outcome.Status)
            .Should().Equal(
                NyxIdAuthorizationCatalogRefreshOutcomeStatusState.Started,
                NyxIdAuthorizationCatalogRefreshOutcomeStatusState.Observed);
    }

    [Fact]
    public async Task RefreshAcquire_ShouldAtomicallySupersedeDisplacedRefresh()
    {
        var owner = Owner();
        var eventStore = new InMemoryEventStore();
        var agent = CreateAgent(owner, eventStore);
        await BeginRefreshAsync(agent, owner, "refresh-old", ObservedAt, expectedLifecycleFence: 0);
        var versionBeforeReplacement = await eventStore.GetVersionAsync(agent.Id);

        await BeginRefreshAsync(
            agent,
            owner,
            "refresh-new",
            ObservedAt.AddSeconds(1),
            expectedLifecycleFence: 0);

        var replacementEvents = (await eventStore.GetEventsAsync(agent.Id))
            .Where(evt => evt.Version > versionBeforeReplacement)
            .Select(static evt => evt.EventData)
            .ToArray();
        replacementEvents.Should().HaveCount(3);
        replacementEvents[0].Is(NyxIdAuthorizationCatalogRefreshBeganEvent.Descriptor).Should().BeTrue();
        replacementEvents[0].Unpack<NyxIdAuthorizationCatalogRefreshBeganEvent>()
            .RefreshId.Should().Be("refresh-new");
        replacementEvents[1].Unpack<NyxIdAuthorizationCatalogRefreshOutcomeEvent>()
            .Should().Match<NyxIdAuthorizationCatalogRefreshOutcomeEvent>(outcome =>
                outcome.RefreshId == "refresh-old" &&
                outcome.Status == NyxIdAuthorizationCatalogRefreshOutcomeStatusState.Superseded);
        replacementEvents[2].Unpack<NyxIdAuthorizationCatalogRefreshOutcomeEvent>()
            .Should().Match<NyxIdAuthorizationCatalogRefreshOutcomeEvent>(outcome =>
                outcome.RefreshId == "refresh-new" &&
                outcome.Status == NyxIdAuthorizationCatalogRefreshOutcomeStatusState.Started);
        agent.State.ActiveRefreshId.Should().Be("refresh-new");
    }

    [Fact]
    public async Task RepairRefreshAcquire_WhenActorIsNewerThanMinimum_ShouldUseActorOwnedLifecycleFence()
    {
        var owner = Owner();
        var eventStore = new InMemoryEventStore();
        var agent = CreateAgent(owner, eventStore);
        await BeginRefreshAsync(agent, owner, "refresh-existing", ObservedAt);
        await agent.HandleObserveAsync(
            ObservationCommand(owner, "refresh-existing", ObservedAt.AddSeconds(1)));
        var currentVersion = await eventStore.GetVersionAsync(agent.Id);
        agent.State.LifecycleFence.Should().BeGreaterThan(0);
        currentVersion.Should().BeGreaterThan(1);

        await agent.HandleBeginRepairRefreshAsync(
            new BeginNyxIdAuthorizationCatalogRepairRefreshCommand
            {
                Owner = owner.Clone(),
                RefreshId = "refresh-repair",
                StartedAt = Timestamp.FromDateTimeOffset(ObservedAt.AddSeconds(2)),
                MinimumSourceStateVersion = 1,
                RepairRequestId = "repair-alpha",
            });

        agent.State.ActiveRefreshId.Should().Be("refresh-repair");
        await AssertLastRefreshOutcomeAsync(
            eventStore,
            agent.Id,
            "refresh-repair",
            NyxIdAuthorizationCatalogRefreshOutcomeStatusState.Started);
    }

    [Fact]
    public async Task RepairRefreshAcquire_WhenMinimumExceedsCurrentVersion_ShouldReject()
    {
        var owner = Owner();
        var eventStore = new InMemoryEventStore();
        var agent = CreateAgent(owner, eventStore);
        await BeginRefreshAsync(agent, owner, "refresh-existing", ObservedAt);
        await agent.HandleObserveAsync(
            ObservationCommand(owner, "refresh-existing", ObservedAt.AddSeconds(1)));
        var currentVersion = await eventStore.GetVersionAsync(agent.Id);

        var act = () => agent.HandleBeginRepairRefreshAsync(
            new BeginNyxIdAuthorizationCatalogRepairRefreshCommand
            {
                Owner = owner.Clone(),
                RefreshId = "refresh-repair",
                StartedAt = Timestamp.FromDateTimeOffset(ObservedAt.AddSeconds(2)),
                MinimumSourceStateVersion = currentVersion + 1,
                RepairRequestId = "repair-alpha",
            });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("NyxID authorization catalog repair source version changed.");
        agent.State.ActiveRefreshId.Should().BeEmpty();
    }

    [Fact]
    public async Task RepairRefreshAcquire_WhenRepairRequestIdentityIsMissing_ShouldReject()
    {
        var owner = Owner();
        var eventStore = new InMemoryEventStore();
        var agent = CreateAgent(owner, eventStore);
        await BeginRefreshAsync(agent, owner, "refresh-existing", ObservedAt);
        await agent.HandleObserveAsync(
            ObservationCommand(owner, "refresh-existing", ObservedAt.AddSeconds(1)));

        var act = () => agent.HandleBeginRepairRefreshAsync(
            new BeginNyxIdAuthorizationCatalogRepairRefreshCommand
            {
                Owner = owner.Clone(),
                RefreshId = "refresh-repair",
                StartedAt = Timestamp.FromDateTimeOffset(ObservedAt.AddSeconds(2)),
                MinimumSourceStateVersion = 1,
                RepairRequestId = " ",
            });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Catalog repair refresh identity is required.");
    }

    [Theory]
    [InlineData("observed")]
    [InlineData("failed")]
    public async Task RefreshSession_ShouldAdvanceEpochAfterTerminalAndAllowClockRollback(
        string terminal)
    {
        var owner = Owner();
        var eventStore = new InMemoryEventStore();
        var agent = CreateAgent(owner, eventStore);
        var priorEpoch = agent.State.LifecycleFence;
        await BeginRefreshAsync(
            agent,
            owner,
            "refresh-prior-epoch",
            ObservedAt.AddMinutes(10),
            priorEpoch);
        if (terminal == "observed")
        {
            await agent.HandleObserveAsync(
                ObservationCommand(owner, "refresh-prior-epoch", ObservedAt.AddMinutes(11)));
        }
        else
        {
            await agent.HandleRefreshFailureAsync(new RecordNyxIdAuthorizationCatalogRefreshFailureCommand
            {
                Owner = owner.Clone(),
                RefreshId = "refresh-prior-epoch",
                FailedAt = Timestamp.FromDateTimeOffset(ObservedAt.AddMinutes(11)),
                FailureCode = "provider_unavailable",
            });
        }

        var currentEpoch = agent.State.LifecycleFence;
        await BeginRefreshAsync(
            agent,
            owner,
            "refresh-current-epoch",
            ObservedAt.AddMinutes(1),
            currentEpoch);
        await BeginRefreshAsync(
            agent,
            owner,
            "refresh-delayed-prior-epoch",
            ObservedAt.AddMinutes(12),
            priorEpoch);

        currentEpoch.Should().Be(priorEpoch + 1);
        agent.State.ActiveRefreshId.Should().Be("refresh-current-epoch");
        await AssertLastRefreshOutcomeAsync(
            eventStore,
            agent.Id,
            "refresh-delayed-prior-epoch",
            NyxIdAuthorizationCatalogRefreshOutcomeStatusState.Superseded);
    }

    [Fact]
    public async Task LegacyTerminalReplay_ShouldMigrateEpochAndFenceDelayedPreUpgradeBegin()
    {
        var owner = Owner();
        var actorId = NyxIdAuthorizationCatalogActorIds.Build(owner);
        var eventStore = new InMemoryEventStore();
        var history = new List<StateEvent>();

        void Append(IMessage payload)
        {
            var version = history.Count + 1;
            history.Add(new StateEvent
            {
                AgentId = actorId,
                EventId = $"legacy-event-{version}",
                EventType = payload.Descriptor.FullName,
                EventData = Any.Pack(payload),
                Timestamp = Timestamp.FromDateTimeOffset(ObservedAt.AddSeconds(version)),
                Version = version,
            });
        }

        void AppendOutcome(
            string refreshId,
            NyxIdAuthorizationCatalogRefreshOutcomeStatusState status)
        {
            Append(new NyxIdAuthorizationCatalogRefreshOutcomeEvent
            {
                RefreshId = refreshId,
                Status = status,
                StateVersion = history.Count,
                ObservedAtUtc = Timestamp.FromDateTimeOffset(ObservedAt.AddSeconds(history.Count)),
            });
        }

        NyxIdAuthorizationCatalogObservedEvent LegacyObserved(string refreshId, string digest)
        {
            var observed = Observed(owner, digest);
            observed.RefreshId = refreshId;
            return observed;
        }

        Append(new NyxIdAuthorizationCatalogActivatedEvent
        {
            Owner = owner.Clone(),
            ActivatedAt = Timestamp.FromDateTimeOffset(ObservedAt),
        });
        Append(new NyxIdAuthorizationCatalogRefreshBeganEvent
        {
            Owner = owner.Clone(),
            RefreshId = "legacy-observed-1",
            StartedAt = Timestamp.FromDateTimeOffset(ObservedAt.AddMinutes(10)),
        });
        Append(LegacyObserved("legacy-observed-1", "digest-legacy-1"));
        AppendOutcome(
            "legacy-observed-1",
            NyxIdAuthorizationCatalogRefreshOutcomeStatusState.Observed);
        Append(new NyxIdAuthorizationCatalogRefreshBeganEvent
        {
            Owner = owner.Clone(),
            RefreshId = "legacy-failed-1",
            StartedAt = Timestamp.FromDateTimeOffset(ObservedAt.AddMinutes(20)),
        });
        Append(new NyxIdAuthorizationCatalogRefreshFailedEvent
        {
            Owner = owner.Clone(),
            RefreshId = "legacy-failed-1",
            FailedAt = Timestamp.FromDateTimeOffset(ObservedAt.AddMinutes(21)),
            FailureCode = "legacy_provider_unavailable",
        });
        AppendOutcome(
            "legacy-failed-1",
            NyxIdAuthorizationCatalogRefreshOutcomeStatusState.Failed);
        Append(new NyxIdAuthorizationCatalogRefreshBeganEvent
        {
            Owner = owner.Clone(),
            RefreshId = "legacy-observed-2",
            StartedAt = Timestamp.FromDateTimeOffset(ObservedAt.AddMinutes(30)),
        });
        Append(LegacyObserved("legacy-observed-2", "digest-legacy-2"));
        AppendOutcome(
            "legacy-observed-2",
            NyxIdAuthorizationCatalogRefreshOutcomeStatusState.Observed);
        Append(new NyxIdAuthorizationCatalogInvalidatedEvent
        {
            Owner = owner.Clone(),
            InvalidatedAt = Timestamp.FromDateTimeOffset(ObservedAt.AddMinutes(40)),
            Reason = "legacy_credential_revoked",
        });
        Append(new NyxIdAuthorizationCatalogCleanedEvent
        {
            Owner = owner.Clone(),
            CleanedAt = Timestamp.FromDateTimeOffset(ObservedAt.AddMinutes(50)),
            Reason = "legacy_owner_unbound",
        });
        Append(new NyxIdAuthorizationCatalogActivatedEvent
        {
            Owner = owner.Clone(),
            ActivatedAt = Timestamp.FromDateTimeOffset(ObservedAt.AddMinutes(60)),
        });
        Append(new NyxIdAuthorizationCatalogRefreshBeganEvent
        {
            Owner = owner.Clone(),
            RefreshId = "legacy-failed-2",
            StartedAt = Timestamp.FromDateTimeOffset(ObservedAt.AddMinutes(70)),
        });
        Append(new NyxIdAuthorizationCatalogRefreshFailedEvent
        {
            Owner = owner.Clone(),
            RefreshId = "legacy-failed-2",
            FailedAt = Timestamp.FromDateTimeOffset(ObservedAt.AddMinutes(71)),
            FailureCode = "legacy_provider_timeout",
        });
        AppendOutcome(
            "legacy-failed-2",
            NyxIdAuthorizationCatalogRefreshOutcomeStatusState.Failed);
        await eventStore.AppendAsync(actorId, history, expectedVersion: 0);

        var agent = CreateAgent(owner, eventStore);
        await agent.ActivateAsync();
        var migratedEpoch = agent.State.LifecycleFence;
        await BeginRefreshAsync(
            agent,
            owner,
            "refresh-post-rollback",
            ObservedAt.AddMinutes(1),
            migratedEpoch);
        await BeginRefreshAsync(
            agent,
            owner,
            "refresh-delayed-pre-upgrade",
            ObservedAt.AddMinutes(80),
            expectedLifecycleFence: 0);
        await AssertLastRefreshOutcomeAsync(
            eventStore,
            actorId,
            "refresh-delayed-pre-upgrade",
            NyxIdAuthorizationCatalogRefreshOutcomeStatusState.Superseded);
        await agent.HandleObserveAsync(
            ObservationCommand(owner, "refresh-post-rollback", ObservedAt.AddMinutes(2)));

        migratedEpoch.Should().Be(7);
        agent.State.LifecycleFence.Should().Be(8);
        agent.State.LifecycleFenceSemanticsVersion.Should().Be(
            NyxIdAuthorizationCatalogLifecycleFenceSemanticsVersion.TerminalFactsAdvanceFence);
        agent.State.ActiveRefreshId.Should().BeEmpty();
        agent.State.ObservedAt.Should()
            .Be(Timestamp.FromDateTimeOffset(ObservedAt.AddMinutes(2)));
    }

    [Fact]
    public async Task LegacySnapshotActivation_ShouldCommitAndProjectFenceMigrationBeforeServingBegins()
    {
        const string migrationEventType =
            "aevatar.gagentservice.schedules.authorization.state." +
            "NyxIdAuthorizationCatalogLifecycleFenceSemanticsMigratedEvent";
        var owner = Owner();
        var actorId = NyxIdAuthorizationCatalogActorIds.Build(owner);
        var eventStore = new InMemoryEventStore();
        var snapshotStore = new InMemoryEventSourcingSnapshotStore<NyxIdAuthorizationCatalogState>();
        var legacyRefreshStartedAt = ObservedAt.AddMinutes(-2);
        await eventStore.AppendAsync(actorId,
        [
            new StateEvent
            {
                AgentId = actorId,
                EventId = "legacy-refresh-began",
                EventType = NyxIdAuthorizationCatalogRefreshBeganEvent.Descriptor.FullName,
                EventData = Any.Pack(new NyxIdAuthorizationCatalogRefreshBeganEvent
                {
                    Owner = owner.Clone(),
                    RefreshId = "refresh-pre-upgrade",
                    StartedAt = Timestamp.FromDateTimeOffset(legacyRefreshStartedAt),
                }),
                Timestamp = Timestamp.FromDateTimeOffset(legacyRefreshStartedAt),
                Version = 1,
            },
        ], expectedVersion: 0);
        var legacyState = new NyxIdAuthorizationCatalogState
        {
            Owner = owner.Clone(),
            Activated = true,
            ActivatedAt = Timestamp.FromDateTimeOffset(ObservedAt.AddMinutes(-3)),
            LifecycleFence = 0,
            ActiveRefreshId = "refresh-pre-upgrade",
            ActiveRefreshStartedAt = Timestamp.FromDateTimeOffset(legacyRefreshStartedAt),
            ContentDigest = "legacy-digest",
            ContractVersion = "legacy-contract",
            PolicyVersion = "legacy-policy",
        };
        var wireLegacyState = NyxIdAuthorizationCatalogState.Parser.ParseFrom(legacyState.ToByteArray());
        await snapshotStore.SaveAsync(
            actorId,
            new EventSourcingSnapshot<NyxIdAuthorizationCatalogState>(wireLegacyState, Version: 1));
        var publications = new RecordingPublicationHook();
        var agent = CreateSnapshotAgent(owner, eventStore, snapshotStore, publications);

        await agent.ActivateAsync();

        var migratedEvents = (await eventStore.GetEventsAsync(actorId)).Skip(1).ToArray();
        migratedEvents.Should().HaveCount(2);
        migratedEvents[0].EventType.Should().Be(migrationEventType);
        migratedEvents[0].EventData
            .Is(NyxIdAuthorizationCatalogLifecycleFenceSemanticsMigratedEvent.Descriptor)
            .Should().BeTrue();
        var migration = migratedEvents[0].EventData
            .Unpack<NyxIdAuthorizationCatalogLifecycleFenceSemanticsMigratedEvent>();
        migration.SemanticsVersion.Should().Be(
            NyxIdAuthorizationCatalogLifecycleFenceSemanticsVersion.TerminalFactsAdvanceFence);
        migration.LifecycleFence.Should().Be(1);
        var displacedOutcome = migratedEvents[1].EventData
            .Unpack<NyxIdAuthorizationCatalogRefreshOutcomeEvent>();
        displacedOutcome.RefreshId.Should().Be("refresh-pre-upgrade");
        displacedOutcome.Status.Should().Be(
            NyxIdAuthorizationCatalogRefreshOutcomeStatusState.Superseded);
        displacedOutcome.StateVersion.Should().Be(2);
        agent.State.LifecycleFence.Should().Be(1);
        agent.State.LifecycleFenceSemanticsVersion.Should().Be(
            NyxIdAuthorizationCatalogLifecycleFenceSemanticsVersion.TerminalFactsAdvanceFence);
        agent.State.ActiveRefreshId.Should().BeEmpty();
        agent.State.ActiveRefreshStartedAt.Should().BeNull();
        agent.State.ContentDigest.Should().Be("legacy-digest");
        agent.State.ContractVersion.Should().Be("legacy-contract");
        agent.State.PolicyVersion.Should().Be("legacy-policy");
        NyxIdAuthorizationCatalogState.Descriptor
            .FindFieldByName("lifecycle_fence_semantics_version")
            .Should().NotBeNull();

        var migrationPublication = publications.Contexts.Single(context =>
            string.Equals(
                context.Published.StateEvent.EventType,
                migrationEventType,
                StringComparison.Ordinal));
        var documentStore = new RecordingDocumentStore<NyxIdAuthorizationCatalogDocument>(
            static document => document.Id);
        var projector = new NyxIdAuthorizationCatalogCurrentStateProjector(
            documentStore,
            new FixedProjectionClock(ObservedAt));
        await projector.ProjectAsync(
            new NyxIdAuthorizationCatalogProjectionContext
            {
                RootActorId = actorId,
                ProjectionKind = NyxIdAuthorizationCatalogGAgent.ProjectionKind,
            },
            new EventEnvelope
            {
                Id = migrationPublication.Published.StateEvent.EventId,
                Timestamp = Timestamp.FromDateTimeOffset(ObservedAt),
                Payload = Any.Pack(migrationPublication.Published),
            });
        var projected = await documentStore.GetAsync(actorId);
        projected.Should().NotBeNull();
        projected!.StateVersion.Should().Be(2);
        projected.LifecycleFence.Should().Be(1);

        var versionBeforeStaleBegin = await eventStore.GetVersionAsync(actorId);
        await BeginRefreshAsync(
            agent,
            owner,
            "refresh-delayed-pre-upgrade",
            ObservedAt.AddMinutes(1),
            expectedLifecycleFence: 0);
        var staleBeginEvents = (await eventStore.GetEventsAsync(actorId))
            .Where(evt => evt.Version > versionBeforeStaleBegin)
            .ToArray();
        staleBeginEvents.Should().ContainSingle();
        staleBeginEvents.Should().NotContain(static evt =>
            evt.EventData.Is(NyxIdAuthorizationCatalogRefreshBeganEvent.Descriptor));
        staleBeginEvents[0].EventData.Unpack<NyxIdAuthorizationCatalogRefreshOutcomeEvent>()
            .Status.Should().Be(NyxIdAuthorizationCatalogRefreshOutcomeStatusState.Superseded);

        var versionBeforeReactivation = await eventStore.GetVersionAsync(actorId);
        var reactivationPublications = new RecordingPublicationHook();
        var reactivated = CreateSnapshotAgent(
            owner,
            eventStore,
            snapshotStore,
            reactivationPublications);
        await reactivated.ActivateAsync();

        (await eventStore.GetVersionAsync(actorId)).Should().Be(versionBeforeReactivation);
        reactivationPublications.Contexts.Should().BeEmpty();
        reactivated.State.LifecycleFence.Should().Be(1);
        reactivated.State.LifecycleFenceSemanticsVersion.Should().Be(
            NyxIdAuthorizationCatalogLifecycleFenceSemanticsVersion.TerminalFactsAdvanceFence);
    }

    [Fact]
    public async Task FreshActivation_ShouldNotCreateMigrationOrAdvanceInitialFence()
    {
        var owner = Owner();
        var eventStore = new InMemoryEventStore();
        var snapshotStore = new InMemoryEventSourcingSnapshotStore<NyxIdAuthorizationCatalogState>();
        var publications = new RecordingPublicationHook();
        var agent = CreateSnapshotAgent(owner, eventStore, snapshotStore, publications);

        await agent.ActivateAsync();

        (await eventStore.GetEventsAsync(agent.Id)).Should().BeEmpty();
        publications.Contexts.Should().BeEmpty();
        agent.State.LifecycleFence.Should().Be(0);

        await BeginRefreshAsync(
            agent,
            owner,
            "refresh-fresh",
            ObservedAt,
            expectedLifecycleFence: 0);

        agent.State.ActiveRefreshId.Should().Be("refresh-fresh");
        agent.State.LifecycleFence.Should().Be(0);
        agent.State.LifecycleFenceSemanticsVersion.Should().Be(
            NyxIdAuthorizationCatalogLifecycleFenceSemanticsVersion.TerminalFactsAdvanceFence);
        (await RefreshOutcomesAsync(eventStore, agent.Id, "refresh-fresh"))
            .Should().ContainSingle()
            .Which.Status.Should().Be(NyxIdAuthorizationCatalogRefreshOutcomeStatusState.Started);
    }

    [Fact]
    public async Task RefreshFailure_AfterClockRollback_ShouldReplacePriorEpochFailureFacts()
    {
        var owner = Owner();
        var agent = CreateAgent(owner);
        await BeginRefreshAsync(
            agent,
            owner,
            "refresh-prior-epoch",
            ObservedAt.AddMinutes(10));
        await agent.HandleRefreshFailureAsync(new RecordNyxIdAuthorizationCatalogRefreshFailureCommand
        {
            Owner = owner.Clone(),
            RefreshId = "refresh-prior-epoch",
            FailedAt = Timestamp.FromDateTimeOffset(ObservedAt.AddMinutes(11)),
            FailureCode = "prior-provider-failure",
        });

        await BeginRefreshAsync(
            agent,
            owner,
            "refresh-current-epoch",
            ObservedAt.AddMinutes(1),
            agent.State.LifecycleFence);
        await agent.HandleRefreshFailureAsync(new RecordNyxIdAuthorizationCatalogRefreshFailureCommand
        {
            Owner = owner.Clone(),
            RefreshId = "refresh-current-epoch",
            FailedAt = Timestamp.FromDateTimeOffset(ObservedAt.AddMinutes(2)),
            FailureCode = "current-provider-failure",
        });

        agent.State.LastRefreshFailedAt.Should()
            .Be(Timestamp.FromDateTimeOffset(ObservedAt.AddMinutes(2)));
        agent.State.LastRefreshFailureCode.Should().Be("current-provider-failure");
    }

    [Fact]
    public async Task RefreshObservation_AfterClockRollback_ShouldReplacePriorEpochSnapshot()
    {
        var owner = Owner();
        var agent = CreateAgent(owner);
        await BeginRefreshAsync(
            agent,
            owner,
            "refresh-prior-epoch",
            ObservedAt.AddMinutes(10));
        await agent.HandleObserveAsync(
            ObservationCommand(owner, "refresh-prior-epoch", ObservedAt.AddMinutes(11)));

        await BeginRefreshAsync(
            agent,
            owner,
            "refresh-current-epoch",
            ObservedAt.AddMinutes(1),
            agent.State.LifecycleFence);
        await agent.HandleObserveAsync(
            ObservationCommand(owner, "refresh-current-epoch", ObservedAt.AddMinutes(2)));

        agent.State.ObservedAt.Should()
            .Be(Timestamp.FromDateTimeOffset(ObservedAt.AddMinutes(2)));
        agent.State.ActiveRefreshId.Should().BeEmpty();
    }

    [Fact]
    public async Task RefreshSession_ShouldAllowCurrentEpochAfterCleanup()
    {
        var owner = Owner();
        var eventStore = new InMemoryEventStore();
        var agent = CreateAgent(owner, eventStore);
        await BeginRefreshAsync(agent, owner, "refresh-newer", ObservedAt.AddSeconds(2));
        await agent.HandleCleanupAsync(new CleanupNyxIdAuthorizationCatalogCommand
        {
            Owner = owner.Clone(),
            CleanedAt = Timestamp.FromDateTimeOffset(ObservedAt.AddSeconds(3)),
            Reason = "owner_unbound",
        });
        var currentEpoch = agent.State.LifecycleFence;

        await BeginRefreshAsync(
            agent,
            owner,
            "refresh-current-epoch",
            ObservedAt.AddSeconds(1),
            currentEpoch);

        agent.State.ActiveRefreshId.Should().Be("refresh-current-epoch");
        await AssertLastRefreshOutcomeAsync(
            eventStore,
            agent.Id,
            "refresh-current-epoch",
            NyxIdAuthorizationCatalogRefreshOutcomeStatusState.Started);
    }

    [Theory]
    [InlineData("invalidated")]
    [InlineData("cleaned")]
    public async Task LifecycleMutation_WhenSameReasonRepeats_ShouldFencePreviouslyIssuedRefresh(
        string lifecycleState)
    {
        var owner = Owner();
        var eventStore = new InMemoryEventStore();
        var agent = CreateAgent(owner, eventStore);

        async Task ApplyLifecycleAsync(DateTimeOffset occurredAt)
        {
            if (lifecycleState == "invalidated")
            {
                await agent.HandleInvalidateAsync(new InvalidateNyxIdAuthorizationCatalogCommand
                {
                    Owner = owner.Clone(),
                    InvalidatedAt = Timestamp.FromDateTimeOffset(occurredAt),
                    Reason = "credential_revoked",
                });
                return;
            }

            await agent.HandleCleanupAsync(new CleanupNyxIdAuthorizationCatalogCommand
            {
                Owner = owner.Clone(),
                CleanedAt = Timestamp.FromDateTimeOffset(occurredAt),
                Reason = "owner_unbound",
            });
        }

        await ApplyLifecycleAsync(ObservedAt.AddSeconds(1));
        var issuedFence = agent.State.LifecycleFence;
        await ApplyLifecycleAsync(ObservedAt.AddSeconds(2));

        await BeginRefreshAsync(
            agent,
            owner,
            "refresh-issued-before-repeat",
            ObservedAt.AddSeconds(3),
            issuedFence);

        agent.State.LifecycleFence.Should().Be(issuedFence + 1);
        agent.State.ActiveRefreshId.Should().BeEmpty();
        await AssertLastRefreshOutcomeAsync(
            eventStore,
            agent.Id,
            "refresh-issued-before-repeat",
            NyxIdAuthorizationCatalogRefreshOutcomeStatusState.Superseded);
    }

    [Fact]
    public async Task RefreshRecoveryAfterInvalidation_ShouldAllowPriorEpochClockRollback()
    {
        var owner = Owner();
        var eventStore = new InMemoryEventStore();
        var agent = CreateAgent(owner, eventStore);
        await BeginRefreshAsync(agent, owner, "refresh-prior-epoch", ObservedAt.AddMinutes(10));
        await agent.HandleObserveAsync(
            ObservationCommand(owner, "refresh-prior-epoch", ObservedAt.AddMinutes(11)));
        await agent.HandleInvalidateAsync(new InvalidateNyxIdAuthorizationCatalogCommand
        {
            Owner = owner.Clone(),
            InvalidatedAt = Timestamp.FromDateTimeOffset(ObservedAt.AddMinutes(12)),
            Reason = "credential_revoked",
        });

        await BeginRefreshAsync(
            agent,
            owner,
            "refresh-current-epoch",
            ObservedAt.AddMinutes(1),
            agent.State.LifecycleFence);
        await agent.HandleObserveAsync(
            ObservationCommand(owner, "refresh-current-epoch", ObservedAt.AddMinutes(2)));

        agent.State.Invalidated.Should().BeFalse();
        agent.State.ObservedAt.Should().Be(Timestamp.FromDateTimeOffset(ObservedAt.AddMinutes(2)));
        (await RefreshOutcomesAsync(eventStore, agent.Id, "refresh-current-epoch"))
            .Select(static outcome => outcome.Status)
            .Should().Equal(
                NyxIdAuthorizationCatalogRefreshOutcomeStatusState.Started,
                NyxIdAuthorizationCatalogRefreshOutcomeStatusState.Observed);
    }

    [Fact]
    public async Task RefreshSession_ShouldResolveEqualStartTimesByOrdinalRefreshIdentity()
    {
        var owner = Owner();
        var firstAgent = CreateAgent(owner);
        var secondAgent = CreateAgent(owner);
        var startedAt = ObservedAt.AddSeconds(1);

        await BeginRefreshAsync(firstAgent, owner, "refresh-z", startedAt);
        await BeginRefreshAsync(firstAgent, owner, "refresh-a", startedAt);
        await BeginRefreshAsync(secondAgent, owner, "refresh-a", startedAt);
        await BeginRefreshAsync(secondAgent, owner, "refresh-z", startedAt);

        firstAgent.State.ActiveRefreshId.Should().Be("refresh-z");
        secondAgent.State.ActiveRefreshId.Should().Be("refresh-z");
    }

    [Fact]
    public async Task RefreshBegin_WhenOutcomeBatchCommitFails_ShouldNotPersistPartialLease()
    {
        var owner = Owner();
        var eventStore = new InMemoryEventStore();
        var rejectingStore = new RefreshOutcomeRejectingEventStore(eventStore)
        {
            RejectRefreshOutcomes = true,
        };
        var agent = CreateAgent(owner, eventStore);
        agent.EventSourcingBehaviorFactory =
            new DefaultEventSourcingBehaviorFactory<NyxIdAuthorizationCatalogState>(
                rejectingStore);
        var versionBefore = await eventStore.GetVersionAsync(agent.Id);
        var stateBefore = agent.State.ToByteArray();

        var act = () => BeginRefreshAsync(
            agent,
            owner,
            "refresh-atomic",
            ObservedAt.AddSeconds(1));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("event-store-batch-failure");
        (await eventStore.GetVersionAsync(agent.Id)).Should().Be(versionBefore);
        agent.State.ToByteArray().Should().Equal(stateBefore);
    }

    [Theory]
    [InlineData("observed")]
    [InlineData("failed")]
    [InlineData("invalidated")]
    [InlineData("cleaned")]
    public async Task RefreshTerminal_WhenOutcomeBatchCommitFails_ShouldNotPersistPartialMutation(
        string terminal)
    {
        var owner = Owner();
        var eventStore = new InMemoryEventStore();
        var rejectingStore = new RefreshOutcomeRejectingEventStore(eventStore);
        var agent = CreateAgent(owner, eventStore);
        agent.EventSourcingBehaviorFactory =
            new DefaultEventSourcingBehaviorFactory<NyxIdAuthorizationCatalogState>(
                rejectingStore);
        await BeginRefreshAsync(agent, owner, "refresh-atomic", ObservedAt.AddSeconds(1));
        var versionBefore = await eventStore.GetVersionAsync(agent.Id);
        var stateBefore = agent.State.ToByteArray();
        rejectingStore.RejectRefreshOutcomes = true;

        Func<Task> act = terminal switch
        {
            "observed" => () => agent.HandleObserveAsync(
                ObservationCommand(owner, "refresh-atomic", ObservedAt.AddMinutes(1))),
            "failed" => () => agent.HandleRefreshFailureAsync(
                new RecordNyxIdAuthorizationCatalogRefreshFailureCommand
                {
                    Owner = owner.Clone(),
                    RefreshId = "refresh-atomic",
                    FailedAt = Timestamp.FromDateTimeOffset(ObservedAt.AddMinutes(1)),
                    FailureCode = "provider_unavailable",
                }),
            "invalidated" => () => agent.HandleInvalidateAsync(
                new InvalidateNyxIdAuthorizationCatalogCommand
                {
                    Owner = owner.Clone(),
                    RefreshId = "refresh-atomic",
                    InvalidatedAt = Timestamp.FromDateTimeOffset(ObservedAt.AddMinutes(1)),
                    Reason = "api_key_scope_plan_denied",
                    OutcomeStatus =
                        NyxIdAuthorizationCatalogRefreshOutcomeStatusState.AccessDenied,
                }),
            "cleaned" => () => agent.HandleCleanupAsync(
                new CleanupNyxIdAuthorizationCatalogCommand
                {
                    Owner = owner.Clone(),
                    CleanedAt = Timestamp.FromDateTimeOffset(ObservedAt.AddMinutes(1)),
                    Reason = "owner_unbound",
                }),
            _ => throw new ArgumentOutOfRangeException(nameof(terminal), terminal, null),
        };

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("event-store-batch-failure");
        (await eventStore.GetVersionAsync(agent.Id)).Should().Be(versionBefore);
        agent.State.ToByteArray().Should().Equal(stateBefore);
    }

    [Theory]
    [InlineData("observed")]
    [InlineData("failed")]
    [InlineData("invalidated")]
    public async Task RefreshSession_ShouldCommitSupersededOutcomeForStaleTerminalCommand(
        string terminal)
    {
        var owner = Owner();
        var eventStore = new InMemoryEventStore();
        var agent = CreateAgent(owner, eventStore);
        await BeginRefreshAsync(agent, owner, "refresh-old", ObservedAt.AddSeconds(1));
        await BeginRefreshAsync(agent, owner, "refresh-current", ObservedAt.AddSeconds(2));

        Func<Task> act = terminal switch
        {
            "observed" => () => agent.HandleObserveAsync(
                ObservationCommand(owner, "refresh-old", ObservedAt.AddMinutes(1))),
            "failed" => () => agent.HandleRefreshFailureAsync(
                new RecordNyxIdAuthorizationCatalogRefreshFailureCommand
                {
                    Owner = owner.Clone(),
                    RefreshId = "refresh-old",
                    FailedAt = Timestamp.FromDateTimeOffset(ObservedAt.AddMinutes(1)),
                    FailureCode = "provider_unavailable",
                }),
            "invalidated" => () => agent.HandleInvalidateAsync(
                new InvalidateNyxIdAuthorizationCatalogCommand
                {
                    Owner = owner.Clone(),
                    RefreshId = "refresh-old",
                    InvalidatedAt = Timestamp.FromDateTimeOffset(ObservedAt.AddMinutes(1)),
                    Reason = "api_key_scope_plan_denied",
                }),
            _ => throw new ArgumentOutOfRangeException(nameof(terminal), terminal, null),
        };

        await act.Should().NotThrowAsync();

        agent.State.ActiveRefreshId.Should().Be("refresh-current");
        agent.State.ContentDigest.Should().BeEmpty();
        agent.State.LastRefreshFailureCode.Should().BeEmpty();
        agent.State.Invalidated.Should().BeFalse();
        await AssertLastRefreshOutcomeAsync(
            eventStore,
            agent.Id,
            "refresh-old",
            NyxIdAuthorizationCatalogRefreshOutcomeStatusState.Superseded);
    }

    [Fact]
    public async Task Invalidation_ShouldEndActiveRefreshAndAdvanceEpochForSameReason()
    {
        var owner = Owner();
        var eventStore = new InMemoryEventStore();
        var agent = CreateAgent(owner, eventStore);
        var invalidation = new InvalidateNyxIdAuthorizationCatalogCommand
        {
            Owner = owner.Clone(),
            InvalidatedAt = Timestamp.FromDateTimeOffset(ObservedAt.AddMinutes(1)),
            Reason = "credential_revoked",
        };
        await agent.HandleInvalidateAsync(invalidation);
        await BeginRefreshAsync(
            agent,
            owner,
            "refresh-after-invalidation",
            ObservedAt.AddMinutes(2),
            agent.State.LifecycleFence);

        invalidation.InvalidatedAt = Timestamp.FromDateTimeOffset(ObservedAt.AddMinutes(3));
        await agent.HandleInvalidateAsync(invalidation);

        agent.State.ActiveRefreshId.Should().BeEmpty();
        agent.State.LifecycleFence.Should().Be(2);
        await AssertLastRefreshOutcomeAsync(
            eventStore,
            agent.Id,
            "refresh-after-invalidation",
            NyxIdAuthorizationCatalogRefreshOutcomeStatusState.Superseded);
    }

    [Fact]
    public async Task Cleanup_ShouldCommitSupersededOutcomeForActiveRefresh()
    {
        var owner = Owner();
        var eventStore = new InMemoryEventStore();
        var agent = CreateAgent(owner, eventStore);
        await BeginRefreshAsync(agent, owner, "refresh-active", ObservedAt.AddSeconds(1));

        await agent.HandleCleanupAsync(new CleanupNyxIdAuthorizationCatalogCommand
        {
            Owner = owner.Clone(),
            CleanedAt = Timestamp.FromDateTimeOffset(ObservedAt.AddSeconds(2)),
            Reason = "owner_unbound",
        });

        agent.State.ActiveRefreshId.Should().BeEmpty();
        await AssertLastRefreshOutcomeAsync(
            eventStore,
            agent.Id,
            "refresh-active",
            NyxIdAuthorizationCatalogRefreshOutcomeStatusState.Superseded);
    }

    [Theory]
    [InlineData(NyxIdAuthorizationCatalogRefreshOutcomeStatusState.AccessDenied)]
    [InlineData(NyxIdAuthorizationCatalogRefreshOutcomeStatusState.CatalogUnstable)]
    public async Task RefreshInvalidation_ShouldCommitTypedTerminalOutcome(
        NyxIdAuthorizationCatalogRefreshOutcomeStatusState outcomeStatus)
    {
        var owner = Owner();
        var eventStore = new InMemoryEventStore();
        var agent = CreateAgent(owner, eventStore);
        await BeginRefreshAsync(agent, owner, "refresh-invalidated", ObservedAt.AddSeconds(1));

        await agent.HandleInvalidateAsync(new InvalidateNyxIdAuthorizationCatalogCommand
        {
            Owner = owner.Clone(),
            RefreshId = "refresh-invalidated",
            InvalidatedAt = Timestamp.FromDateTimeOffset(ObservedAt.AddSeconds(2)),
            Reason = "provider_rejected",
            OutcomeStatus = outcomeStatus,
        });

        agent.State.Invalidated.Should().BeTrue();
        agent.State.ActiveRefreshId.Should().BeEmpty();
        await AssertLastRefreshOutcomeAsync(
            eventStore,
            agent.Id,
            "refresh-invalidated",
            outcomeStatus);
    }

    [Theory]
    [InlineData("duplicate_service", "*service identities must be unique*")]
    [InlineData("resource_owner_missing", "*resource owner identity is incomplete*")]
    [InlineData("resource_owner_other_authority", "*resource owner identity must use NyxID authority*")]
    [InlineData("resource_owner_uppercase_authority", "*resource owner identity must use NyxID authority*")]
    [InlineData("resource_owner_trailing_space_authority", "*resource owner identity is incomplete*")]
    [InlineData("required_without_nodes", "*require at least one node identity*")]
    [InlineData("direct_with_nodes", "*cannot carry node authorization evidence*")]
    [InlineData("duplicate_nodes", "*node identities must be ordinal-sorted and unique*")]
    [InlineData("unsorted_nodes", "*node identities must be ordinal-sorted and unique*")]
    [InlineData("partial_authority_stamp", "*service authority evidence is incomplete*")]
    public async Task ObserveHandler_ShouldRejectInvalidTypedPermissionSets(
        string scenario,
        string expectedMessage)
    {
        var owner = Owner();
        var eventStore = new InMemoryEventStore();
        var agent = CreateAgent(owner, eventStore);
        await BeginRefreshAsync(agent, owner, "refresh-invalid", ObservedAt.AddSeconds(1));
        var command = ObservationCommand(owner, "refresh-invalid", ObservedAt.AddMinutes(1));

        switch (scenario)
        {
            case "duplicate_service":
                command.Services.Add(command.Services[0].Clone());
                break;
            case "resource_owner_missing":
                command.Services[0].ResourceOwner = null;
                break;
            case "resource_owner_other_authority":
                command.Services[0].ResourceOwner.Authority = "other-authority";
                break;
            case "resource_owner_uppercase_authority":
                command.Services[0].ResourceOwner.Authority = "NYXID";
                break;
            case "resource_owner_trailing_space_authority":
                command.Services[0].ResourceOwner.Authority = "nyxid ";
                break;
            case "required_without_nodes":
                command.Services[0].NodeIds.Clear();
                break;
            case "direct_with_nodes":
                command.Services[0].NodeGrantRequirement = AuthorizationGrantRequirement.NotRequired;
                break;
            case "duplicate_nodes":
                command.Services[0].NodeIds.Add("node-z");
                break;
            case "unsorted_nodes":
                command.Services[0].NodeIds.Clear();
                command.Services[0].NodeIds.Add("node-z");
                command.Services[0].NodeIds.Add("node-a");
                break;
            case "partial_authority_stamp":
                command.Services[0].ObservedAt = command.ObservedAt.Clone();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
        }
        command.ContentDigest = NyxIdAuthorizationCatalogIntegrity.ComputeContentDigest(
            command.Owner,
            command.Services);
        var versionBefore = await eventStore.GetVersionAsync(agent.Id);
        var stateBefore = agent.State.ToByteArray();
        var observedEventCountBefore = (await eventStore.GetEventsAsync(agent.Id))
            .Count(static evt => evt.EventData.Is(NyxIdAuthorizationCatalogObservedEvent.Descriptor));

        var act = () => agent.HandleObserveAsync(command);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage(expectedMessage);
        (await eventStore.GetVersionAsync(agent.Id)).Should().Be(versionBefore);
        agent.State.ToByteArray().Should().Equal(stateBefore);
        (await eventStore.GetEventsAsync(agent.Id))
            .Count(static evt => evt.EventData.Is(NyxIdAuthorizationCatalogObservedEvent.Descriptor))
            .Should().Be(observedEventCountBefore);
    }

    [Fact]
    public async Task ObserveHandler_ShouldAcceptCompleteServiceAuthorityStamp()
    {
        var owner = Owner();
        var agent = CreateAgent(owner);
        await BeginRefreshAsync(agent, owner, "refresh-stamped", ObservedAt.AddSeconds(1));
        var command = ObservationCommand(owner, "refresh-stamped", ObservedAt.AddMinutes(1));
        var service = command.Services[0];
        service.ObservedAt = command.ObservedAt.Clone();
        service.FreshUntil = command.FreshUntil.Clone();
        service.EvaluatedAt = command.EvaluatedAt.Clone();
        service.AuthorityContractVersion = command.ContractVersion;
        service.AuthorityPolicyVersion = command.PolicyVersion;
        command.ContentDigest = NyxIdAuthorizationCatalogIntegrity.ComputeContentDigest(
            command.Owner,
            command.Services);

        await agent.HandleObserveAsync(command);

        agent.State.Services.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(service);
        agent.State.ActiveRefreshId.Should().BeEmpty();
    }

    [Fact]
    public void ComputeContentDigest_ShouldBindGatewayAndExactServiceModelEvidence()
    {
        var service = ServiceEvidence("us-alpha", "chrono-llm-public");
        service.LlmTarget = ServiceTarget("us-alpha", "chrono-llm-public", "gpt-5.5");
        var gateway = GatewayTarget("gateway-model-a");
        var first = NyxIdAuthorizationCatalogIntegrity.ComputeContentDigest(
            Owner(),
            [service],
            gateway);

        var changedGateway = NyxIdAuthorizationCatalogIntegrity.ComputeContentDigest(
            Owner(),
            [service],
            GatewayTarget("gateway-model-b"));
        var changedService = service.Clone();
        changedService.LlmTarget = ServiceTarget("us-alpha", "chrono-llm-public", "gpt-5.6");

        changedGateway.Should().NotBe(first);
        NyxIdAuthorizationCatalogIntegrity.ComputeContentDigest(
                Owner(),
                [changedService],
                gateway)
            .Should().NotBe(first);
    }

    [Fact]
    public async Task ObserveHandler_ShouldRoundTripGatewayAndServiceLLMEvidence()
    {
        var owner = Owner();
        var agent = CreateAgent(owner);
        await BeginRefreshAsync(agent, owner, "refresh-llm", ObservedAt.AddSeconds(1));
        var command = ObservationCommand(owner, "refresh-llm", ObservedAt.AddMinutes(1));
        command.Services[0].LlmTarget = ServiceTarget("svc-alpha", "calendar", "gpt-5.5");
        command.GatewayLlmTarget = GatewayTarget("gateway-model-a");
        command.ContentDigest = NyxIdAuthorizationCatalogIntegrity.ComputeContentDigest(
            owner,
            command.Services,
            command.GatewayLlmTarget);

        await agent.HandleObserveAsync(command);

        agent.State.Services.Should().ContainSingle();
        agent.State.Services[0].LlmTarget.Should().BeEquivalentTo(command.Services[0].LlmTarget);
        agent.State.Services[0].LlmTarget.Should().NotBeSameAs(command.Services[0].LlmTarget);
        agent.State.GatewayLlmTarget.Should().BeEquivalentTo(command.GatewayLlmTarget);
        agent.State.GatewayLlmTarget.Should().NotBeSameAs(command.GatewayLlmTarget);
    }

    [Theory]
    [InlineData("enumerated_empty", "*bounded non-empty model list*")]
    [InlineData("non_enumerated_models", "*non-enumerated catalog cannot expose selectable model IDs*")]
    [InlineData("duplicate_models", "*ordinal-sorted and distinct*")]
    [InlineData("unsorted_models", "*ordinal-sorted and distinct*")]
    [InlineData("service_id_mismatch", "*does not match its parent service*")]
    [InlineData("service_slug_mismatch", "*does not match its parent service*")]
    [InlineData("service_route_mismatch", "*does not match its parent service*")]
    [InlineData("gateway_service_identity", "*Gateway LLM target identity is invalid*")]
    public async Task ObserveHandler_ShouldRejectInvalidLLMEvidence(
        string scenario,
        string expectedMessage)
    {
        var owner = Owner();
        var eventStore = new InMemoryEventStore();
        var agent = CreateAgent(owner, eventStore);
        await BeginRefreshAsync(agent, owner, "refresh-invalid-llm", ObservedAt.AddSeconds(1));
        var command = ObservationCommand(owner, "refresh-invalid-llm", ObservedAt.AddMinutes(1));
        command.Services[0].LlmTarget = ServiceTarget("svc-alpha", "calendar", "gpt-5.5");

        switch (scenario)
        {
            case "enumerated_empty":
                command.Services[0].LlmTarget.ModelCatalog = new LLMModelCatalog
                {
                    Certainty = LLMModelCatalogCertainty.Enumerated,
                };
                break;
            case "non_enumerated_models":
                command.Services[0].LlmTarget.ModelCatalog = new LLMModelCatalog
                {
                    Certainty = LLMModelCatalogCertainty.NotVerifiable,
                    DiagnosticKind = LLMModelCatalogDiagnosticKind.NotPublished,
                    ModelIds = { "gpt-5.5" },
                };
                break;
            case "duplicate_models":
                command.Services[0].LlmTarget.ModelCatalog.ModelIds.Add("gpt-5.5");
                break;
            case "unsorted_models":
                command.Services[0].LlmTarget.ModelCatalog.ModelIds.Clear();
                command.Services[0].LlmTarget.ModelCatalog.ModelIds.Add("gpt-z");
                command.Services[0].LlmTarget.ModelCatalog.ModelIds.Add("gpt-a");
                command.Services[0].LlmTarget.ModelCatalog.DefaultModelId = "gpt-a";
                break;
            case "service_id_mismatch":
                command.Services[0].LlmTarget.NyxIdUserServiceId = "us-other";
                break;
            case "service_slug_mismatch":
                command.Services[0].LlmTarget.ServiceSlugSnapshot = "other-service";
                break;
            case "service_route_mismatch":
                command.Services[0].LlmTarget.RouteValue = "/api/v1/proxy/s/other-service";
                break;
            case "gateway_service_identity":
                command.Services[0].LlmTarget = null;
                command.GatewayLlmTarget = GatewayTarget("gateway-model-a");
                command.GatewayLlmTarget.NyxIdUserServiceId = "us-alpha";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
        }
        command.ContentDigest = NyxIdAuthorizationCatalogIntegrity.ComputeContentDigest(
            owner,
            command.Services,
            command.GatewayLlmTarget);
        var versionBefore = await eventStore.GetVersionAsync(agent.Id);

        var act = () => agent.HandleObserveAsync(command);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage(expectedMessage);
        (await eventStore.GetVersionAsync(agent.Id)).Should().Be(versionBefore);
    }

    [Fact]
    public void StateTransition_ShouldPreserveCatalogFactsAndClearInvalidationOnNewObservation()
    {
        var agent = new NyxIdAuthorizationCatalogGAgent();
        var owner = Owner();
        var initial = Transition(agent, new NyxIdAuthorizationCatalogState(), Observed(owner, "digest-1"));
        var failed = Transition(agent, initial, new NyxIdAuthorizationCatalogRefreshFailedEvent
        {
            Owner = owner.Clone(),
            FailedAt = Timestamp.FromDateTimeOffset(ObservedAt.AddMinutes(1)),
            FailureCode = "provider_unavailable",
        });
        var invalidated = Transition(agent, failed, new NyxIdAuthorizationCatalogInvalidatedEvent
        {
            Owner = owner.Clone(),
            InvalidatedAt = Timestamp.FromDateTimeOffset(ObservedAt.AddMinutes(2)),
            Reason = "credential_revoked",
        });
        var refreshed = Transition(agent, invalidated, Observed(owner, "digest-2"));

        failed.ContentDigest.Should().Be("digest-1");
        failed.Services.Should().ContainSingle();
        failed.LastRefreshFailureCode.Should().Be("provider_unavailable");
        invalidated.Invalidated.Should().BeTrue();
        invalidated.InvalidationReason.Should().Be("credential_revoked");
        refreshed.Invalidated.Should().BeFalse();
        refreshed.InvalidationReason.Should().BeEmpty();
        refreshed.ContentDigest.Should().Be("digest-2");
        refreshed.LastRefreshFailureCode.Should().Be("provider_unavailable");
    }

    [Fact]
    public void RefreshFailureTransition_WhenLegacyEventOmitsFence_ShouldAdvanceReplayedFence()
    {
        var agent = new NyxIdAuthorizationCatalogGAgent();
        var owner = Owner();
        var state = new NyxIdAuthorizationCatalogState
        {
            Owner = owner.Clone(),
            LifecycleFence = 7,
            ActiveRefreshId = "refresh-legacy",
            ActiveRefreshStartedAt = Timestamp.FromDateTimeOffset(ObservedAt),
        };

        var failed = Transition(agent, state, new NyxIdAuthorizationCatalogRefreshFailedEvent
        {
            Owner = owner.Clone(),
            RefreshId = "refresh-legacy",
            FailedAt = Timestamp.FromDateTimeOffset(ObservedAt.AddMinutes(1)),
            FailureCode = "provider_unavailable",
        });

        failed.LifecycleFence.Should().Be(8);
        failed.ActiveRefreshId.Should().BeEmpty();
        failed.LastRefreshFailureCode.Should().Be("provider_unavailable");
    }

    [Fact]
    public void CleanupTransition_ShouldClearCatalogFactsAndAdvanceFence()
    {
        var agent = new NyxIdAuthorizationCatalogGAgent();
        var owner = Owner();
        var observed = Transition(agent, new NyxIdAuthorizationCatalogState(), Observed(owner, "digest-1"));

        var cleaned = Transition(agent, observed, new NyxIdAuthorizationCatalogCleanedEvent
        {
            Owner = owner.Clone(),
            CleanedAt = Timestamp.FromDateTimeOffset(ObservedAt.AddMinutes(2)),
            Reason = "account_removed",
            LifecycleFence = 1,
        });

        cleaned.Owner.Should().BeEquivalentTo(owner);
        cleaned.Services.Should().BeEmpty();
        cleaned.ObservedAt.Should().BeNull();
        cleaned.FreshUntil.Should().BeNull();
        cleaned.ContentDigest.Should().BeEmpty();
        cleaned.Invalidated.Should().BeTrue();
        cleaned.Cleaned.Should().BeTrue();
        cleaned.CleanupReason.Should().Be("account_removed");
        cleaned.LifecycleFence.Should().Be(2);
    }

    [Fact]
    public async Task Projector_ShouldPublishNeverObservedInvalidationTombstone()
    {
        var owner = Owner();
        var actorId = NyxIdAuthorizationCatalogActorIds.Build(owner);
        var store = new RecordingDocumentStore<NyxIdAuthorizationCatalogDocument>(static document => document.Id);
        var projector = new NyxIdAuthorizationCatalogCurrentStateProjector(
            store,
            new FixedProjectionClock(ObservedAt.AddMinutes(3)));
        var state = Transition(
            new NyxIdAuthorizationCatalogGAgent(),
            new NyxIdAuthorizationCatalogState(),
            new NyxIdAuthorizationCatalogInvalidatedEvent
            {
                Owner = owner.Clone(),
                InvalidatedAt = Timestamp.FromDateTimeOffset(ObservedAt),
                Reason = "credential_revoked",
                LifecycleFence = 1,
            });

        await projector.ProjectAsync(
            new NyxIdAuthorizationCatalogProjectionContext
            {
                RootActorId = actorId,
                ProjectionKind = NyxIdAuthorizationCatalogGAgent.ProjectionKind,
            },
            CommittedEnvelope(state, 1, "evt-tombstone"));
        var snapshot = await new ProjectionNyxIdAuthorizationCatalogQueryPort(store).GetAsync(owner);

        snapshot.Should().NotBeNull();
        snapshot!.Invalidated.Should().BeTrue();
        snapshot.InvalidationReason.Should().Be("credential_revoked");
        snapshot.LifecycleFence.Should().Be(1);
        snapshot.Services.Should().BeEmpty();
        snapshot.ObservedAtUtc.Should().Be(default);
    }

    [Fact]
    public void ActorIds_ShouldIsolateAuthorityOwnerKindAndSubject()
    {
        var personal = Owner();
        var organization = personal.Clone();
        organization.OwnerKind = AuthorizationOwnerKind.Organization;
        var otherAuthority = personal.Clone();
        otherAuthority.Authority = "other-authority";
        var otherSubject = personal.Clone();
        otherSubject.OwnerSubject = "owner-beta";

        new[]
            {
                NyxIdAuthorizationCatalogActorIds.Build(personal),
                NyxIdAuthorizationCatalogActorIds.Build(organization),
                NyxIdAuthorizationCatalogActorIds.Build(otherAuthority),
                NyxIdAuthorizationCatalogActorIds.Build(otherSubject),
            }
            .Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void CatalogPersistenceContracts_ShouldNotContainBearerOrTokenFields()
    {
        var persistedDescriptors = new[]
        {
            NyxIdAuthorizationCatalogState.Descriptor,
            BeginNyxIdAuthorizationCatalogRefreshCommand.Descriptor,
            BeginNyxIdAuthorizationCatalogRepairRefreshCommand.Descriptor,
            ObserveNyxIdAuthorizationCatalogCommand.Descriptor,
            RecordNyxIdAuthorizationCatalogRefreshFailureCommand.Descriptor,
            InvalidateNyxIdAuthorizationCatalogCommand.Descriptor,
            CleanupNyxIdAuthorizationCatalogCommand.Descriptor,
            NyxIdAuthorizationCatalogRefreshBeganEvent.Descriptor,
            NyxIdAuthorizationCatalogObservedEvent.Descriptor,
            NyxIdAuthorizationCatalogRefreshFailedEvent.Descriptor,
            NyxIdAuthorizationCatalogInvalidatedEvent.Descriptor,
            NyxIdAuthorizationCatalogCleanedEvent.Descriptor,
            NyxIdAuthorizationCatalogLifecycleFenceSemanticsMigratedEvent.Descriptor,
            NyxIdAuthorizationCatalogRefreshOutcomeEvent.Descriptor,
            NyxIdAuthorizationCatalogDocument.Descriptor,
        };

        persistedDescriptors
            .SelectMany(static descriptor => descriptor.Fields.InDeclarationOrder())
            .Select(static field => field.Name)
            .Should().NotContain(static name =>
                name.Contains("bearer", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("token", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("normalized_grant_digest", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AuthorizationContracts_ShouldPublishPermissionSetsAndReserveRemovedTopologyFields()
    {
        ScheduledInvocationAuthorizationContractVersions.Schema.Should()
            .Be("scheduled-invocation-authorization/v3");
        ScheduledInvocationAuthorizationContractVersions.CredentialPolicy.Should()
            .Be("nyxid-api-key/scheduled-invocation/v2");

        var evidence = NyxIdAuthorizationServiceEvidence.Descriptor;
        evidence.FindFieldByName("resource_owner").Should().NotBeNull();
        evidence.FindFieldByName("node_ids").Should().NotBeNull();
        evidence.FindFieldByName("nodes").Should().BeNull();
        evidence.FindFieldByNumber(6).Should().BeNull();
        evidence.ToProto().ReservedName.Should().Contain("nodes");
        evidence.ToProto().ReservedRange.Should().Contain(static range => range.Start == 6 && range.End == 7);

        var serviceGrant = NyxIdServiceGrant.Descriptor;
        serviceGrant.FindFieldByName("resource_owner").Should().NotBeNull();
        serviceGrant.FindFieldByName("node_grant_requirement").Should().NotBeNull();
        serviceGrant.FindFieldByName("node_ids").Should().NotBeNull();

        var plan = ScheduledInvocationAuthorizationPlan.Descriptor;
        plan.FindFieldByName("nyx_id_node_grants").Should().BeNull();
        plan.FindFieldByNumber(5).Should().BeNull();
        plan.ToProto().ReservedName.Should().Contain("nyx_id_node_grants");
        plan.ToProto().ReservedRange.Should().Contain(static range => range.Start == 5 && range.End == 6);

        var authority = NyxIdCatalogAuthorityStamp.Descriptor;
        authority.FindFieldByName("external_revision").Should().BeNull();
        authority.FindFieldByName("contract_version").Should().NotBeNull();
        authority.FindFieldByName("policy_version").Should().NotBeNull();
        authority.FindFieldByName("evaluated_at").Should().NotBeNull();
        authority.ToProto().ReservedName.Should().Contain("external_revision");

        var state = NyxIdAuthorizationCatalogState.Descriptor;
        state.FindFieldByName("lifecycle_fence_semantics_version").Should().NotBeNull();
        state.FindFieldByName("active_refresh_id").Should().NotBeNull();
        state.FindFieldByName("active_refresh_started_at").Should().NotBeNull();
        state.FindFieldByName("newest_refresh_id").Should().BeNull();
        state.FindFieldByName("newest_refresh_started_at").Should().BeNull();
        state.FindFieldByNumber(23).Should().BeNull();
        state.FindFieldByNumber(24).Should().BeNull();
        state.ToProto().ReservedName.Should().Contain(new[]
        {
            "newest_refresh_id",
            "newest_refresh_started_at",
        });
        state.ToProto().ReservedRange.Should().Contain(static range => range.Start == 23 && range.End == 25);
        state.DescriptorForType("BeginNyxIdAuthorizationCatalogRefreshCommand")!
            .FindFieldByName("expected_lifecycle_fence").Should().NotBeNull();
        state.DescriptorForType("BeginNyxIdAuthorizationCatalogRepairRefreshCommand")!
            .FindFieldByName("minimum_source_state_version").Should().NotBeNull();
        state.DescriptorForType("NyxIdAuthorizationCatalogRefreshBeganEvent").Should().NotBeNull();
        state.DescriptorForType("NyxIdAuthorizationCatalogRefreshFailedEvent")!
            .FindFieldByName("lifecycle_fence").Should().NotBeNull();
        state.DescriptorForType("NyxIdAuthorizationCatalogLifecycleFenceSemanticsMigratedEvent")
            .Should().NotBeNull();
        state.DescriptorForType("NyxIdAuthorizationCatalogRefreshOutcomeEvent").Should().NotBeNull();

        NyxIdAuthorizationCatalogDocument.Descriptor.FindFieldByName("active_refresh_id").Should().BeNull();
        NyxIdAuthorizationCatalogDocument.Descriptor.FindFieldByName("active_refresh_started_at").Should().BeNull();
    }

    [Fact]
    public async Task ProjectorAndQuery_ShouldRoundTripOwnerScopedCommittedState()
    {
        var owner = Owner();
        var actorId = NyxIdAuthorizationCatalogActorIds.Build(owner);
        var store = new RecordingDocumentStore<NyxIdAuthorizationCatalogDocument>(static document => document.Id);
        var projector = new NyxIdAuthorizationCatalogCurrentStateProjector(
            store,
            new FixedProjectionClock(ObservedAt.AddMinutes(3)));
        var observed = Observed(owner, "digest-19");
        observed.Services[0].LlmTarget = ServiceTarget("svc-alpha", "calendar", "gpt-5.5");
        observed.GatewayLlmTarget = GatewayTarget("gateway-model-a");
        var state = Transition(
            new NyxIdAuthorizationCatalogGAgent(),
            new NyxIdAuthorizationCatalogState(),
            observed);

        await projector.ProjectAsync(
            new NyxIdAuthorizationCatalogProjectionContext
            {
                RootActorId = actorId,
                ProjectionKind = NyxIdAuthorizationCatalogGAgent.ProjectionKind,
            },
            CommittedEnvelope(state, 19, "evt-19"));
        var snapshot = await new ProjectionNyxIdAuthorizationCatalogQueryPort(store).GetAsync(owner);

        snapshot.Should().NotBeNull();
        snapshot!.Owner.Should().BeEquivalentTo(owner);
        snapshot.StateVersion.Should().Be(19);
        snapshot.ContentDigest.Should().Be("digest-19");
        snapshot.ContractVersion.Should().Be("1");
        snapshot.PolicyVersion.Should().Be("api-key-scope-v1");
        snapshot.EvaluatedAtUtc.Should().Be(ObservedAt.AddMinutes(-1));
        var service = snapshot.Services.Should().ContainSingle().Subject;
        service.UserServiceId.Should().Be("svc-alpha");
        service.ResourceOwner.Should().BeEquivalentTo(ResourceOwner());
        service.NodeIds.Should().Equal("node-a", "node-z");
        service.LlmTarget.Should().BeEquivalentTo(observed.Services[0].LlmTarget);
        service.LlmTarget.Should().NotBeSameAs(observed.Services[0].LlmTarget);
        snapshot.GatewayLLMTarget.Should().BeEquivalentTo(observed.GatewayLlmTarget);
        snapshot.GatewayLLMTarget.Should().NotBeSameAs(observed.GatewayLlmTarget);
    }

    [Fact]
    public async Task FirstRequiredServiceSubsetObservation_ShouldEstablishOwnerCatalogStamp()
    {
        var owner = Owner();
        var agent = CreateAgent(owner);
        await BeginRefreshAsync(agent, owner, "refresh-subset", ObservedAt);
        agent.State.Activated.Should().BeTrue();
        agent.State.ObservedAt.Should().BeNull();

        var subsetObservation = ObservationCommand(
            owner,
            "refresh-subset",
            ObservedAt.AddMinutes(1));
        subsetObservation.CoverageKind =
            NyxIdAuthorizationCatalogObservationCoverageKind.RequiredServiceSubset;
        subsetObservation.CoveredUserServiceIds.Add("svc-alpha");
        subsetObservation.ContentDigest = string.Empty;

        await agent.HandleObserveAsync(subsetObservation);

        agent.State.ObservedAt.Should().Be(subsetObservation.ObservedAt);
        agent.State.FreshUntil.Should().Be(subsetObservation.FreshUntil);
        agent.State.ContractVersion.Should().Be(subsetObservation.ContractVersion);
        agent.State.PolicyVersion.Should().Be(subsetObservation.PolicyVersion);
        agent.State.EvaluatedAt.Should().Be(subsetObservation.EvaluatedAt);
        agent.State.ContentDigest.Should().Be(
            NyxIdAuthorizationCatalogIntegrity.ComputeContentDigest(owner, agent.State.Services));
    }

    [Fact]
    public async Task RequiredServiceSubsetObservation_ShouldMergeIntoOwnerCatalogWithoutDroppingOtherServices()
    {
        var owner = Owner();
        var eventStore = new InMemoryEventStore();
        var agent = CreateAgent(owner, eventStore);
        await BeginRefreshAsync(agent, owner, "refresh-full", ObservedAt.AddSeconds(1));
        var fullObservation = ObservationCommand(owner, "refresh-full", ObservedAt.AddMinutes(1));
        fullObservation.Services.Add(ServiceEvidence("svc-beta", "mail"));
        fullObservation.ContentDigest = NyxIdAuthorizationCatalogIntegrity.ComputeContentDigest(
            fullObservation.Owner,
            fullObservation.Services);
        await agent.HandleObserveAsync(fullObservation);

        await BeginRefreshAsync(
            agent,
            owner,
            "refresh-subset",
            ObservedAt.AddMinutes(2),
            agent.State.LifecycleFence);
        var subsetObservation = ObservationCommand(owner, "refresh-subset", ObservedAt.AddMinutes(3));
        subsetObservation.CoverageKind =
            NyxIdAuthorizationCatalogObservationCoverageKind.RequiredServiceSubset;
        subsetObservation.CoveredUserServiceIds.Add("svc-alpha");
        subsetObservation.ContentDigest = string.Empty;
        subsetObservation.Services[0].DisplayName = "Calendar Updated";
        await agent.HandleObserveAsync(subsetObservation);

        agent.State.Services.Select(static service => service.UserServiceId)
            .Should().Equal("svc-alpha", "svc-beta");
        agent.State.Services.Single(static service => service.UserServiceId == "svc-alpha")
            .DisplayName.Should().Be("Calendar Updated");
        agent.State.Services.Single(static service => service.UserServiceId == "svc-beta")
            .DisplayName.Should().Be("Mail");
        agent.State.ObservedAt.Should().Be(Timestamp.FromDateTimeOffset(ObservedAt.AddMinutes(1)));
        agent.State.FreshUntil.Should().Be(Timestamp.FromDateTimeOffset(ObservedAt.AddMinutes(16)));
        agent.State.ContentDigest.Should().Be(
            NyxIdAuthorizationCatalogIntegrity.ComputeContentDigest(owner, agent.State.Services));

        var actorId = NyxIdAuthorizationCatalogActorIds.Build(owner);
        var store = new RecordingDocumentStore<NyxIdAuthorizationCatalogDocument>(static document => document.Id);
        var projector = new NyxIdAuthorizationCatalogCurrentStateProjector(
            store,
            new FixedProjectionClock(ObservedAt.AddMinutes(4)));
        await projector.ProjectAsync(
            new NyxIdAuthorizationCatalogProjectionContext
            {
                RootActorId = actorId,
                ProjectionKind = NyxIdAuthorizationCatalogGAgent.ProjectionKind,
            },
            CommittedEnvelope(agent.State, await eventStore.GetVersionAsync(actorId), "evt-subset"));
        var snapshot = await new ProjectionNyxIdAuthorizationCatalogQueryPort(store).GetAsync(owner);

        snapshot.Should().NotBeNull();
        snapshot!.Services.Select(static service => service.UserServiceId)
            .Should().Equal("svc-alpha", "svc-beta");
        snapshot.ContentDigest.Should().Be(agent.State.ContentDigest);
    }

    private static AuthorizationOwnerIdentity Owner() => new()
    {
        Authority = NyxIdAuthorizationAuthorities.NyxId,
        OwnerKind = AuthorizationOwnerKind.Personal,
        OwnerSubject = "owner-alpha",
    };

    private static AuthorizationOwnerIdentity ResourceOwner() => new()
    {
        Authority = NyxIdAuthorizationAuthorities.NyxId,
        OwnerKind = AuthorizationOwnerKind.Organization,
        OwnerSubject = "org-alpha",
    };

    private static NyxIdAuthorizationCatalogGAgent CreateAgent(AuthorizationOwnerIdentity owner) =>
        CreateAgent(owner, new InMemoryEventStore());

    private static NyxIdAuthorizationCatalogGAgent CreateAgent(
        AuthorizationOwnerIdentity owner,
        InMemoryEventStore eventStore) =>
        GAgentServiceTestKit.CreateStatefulAgent<
            NyxIdAuthorizationCatalogGAgent,
            NyxIdAuthorizationCatalogState>(
            eventStore,
            NyxIdAuthorizationCatalogActorIds.Build(owner),
            static () => new NyxIdAuthorizationCatalogGAgent());

    private static NyxIdAuthorizationCatalogGAgent CreateSnapshotAgent(
        AuthorizationOwnerIdentity owner,
        InMemoryEventStore eventStore,
        InMemoryEventSourcingSnapshotStore<NyxIdAuthorizationCatalogState> snapshotStore,
        RecordingPublicationHook publications)
    {
        var agent = GAgentServiceTestKit.CreateStatefulAgent<
            NyxIdAuthorizationCatalogGAgent,
            NyxIdAuthorizationCatalogState>(
            eventStore,
            NyxIdAuthorizationCatalogActorIds.Build(owner),
            static () => new NyxIdAuthorizationCatalogGAgent(),
            services => services.AddSingleton<ICommittedStatePublicationHook>(publications));
        agent.EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<
            NyxIdAuthorizationCatalogState>(
            eventStore,
            new EventSourcingRuntimeOptions
            {
                EnableSnapshots = true,
                SnapshotInterval = 1,
                EnableEventCompaction = false,
            },
            snapshotStore);
        return agent;
    }

    private static ObserveNyxIdAuthorizationCatalogCommand ObservationCommand(
        AuthorizationOwnerIdentity owner,
        string refreshId,
        DateTimeOffset observedAt)
    {
        var command = new ObserveNyxIdAuthorizationCatalogCommand
        {
            Owner = owner.Clone(),
            RefreshId = refreshId,
            ObservedAt = Timestamp.FromDateTimeOffset(observedAt),
            FreshUntil = Timestamp.FromDateTimeOffset(observedAt.AddMinutes(15)),
            ContractVersion = "1",
            PolicyVersion = "api-key-scope-v1",
            EvaluatedAt = Timestamp.FromDateTimeOffset(observedAt.AddMinutes(-1)),
        };
        command.Services.Add(ServiceEvidence());
        command.ContentDigest = NyxIdAuthorizationCatalogIntegrity.ComputeContentDigest(
            command.Owner,
            command.Services);
        return command;
    }

    private static NyxIdAuthorizationCatalogObservedEvent Observed(
        AuthorizationOwnerIdentity owner,
        string digest)
    {
        var observed = new NyxIdAuthorizationCatalogObservedEvent
        {
            Owner = owner.Clone(),
            RefreshId = "refresh-transition",
            ObservedAt = Timestamp.FromDateTimeOffset(ObservedAt),
            FreshUntil = Timestamp.FromDateTimeOffset(ObservedAt.AddMinutes(15)),
            ContractVersion = "1",
            PolicyVersion = "api-key-scope-v1",
            EvaluatedAt = Timestamp.FromDateTimeOffset(ObservedAt.AddMinutes(-1)),
            ContentDigest = digest,
        };
        observed.Services.Add(ServiceEvidence());
        return observed;
    }

    private static NyxIdAuthorizationServiceEvidence ServiceEvidence(
        string userServiceId = "svc-alpha",
        string serviceSlug = "calendar")
    {
        var service = new NyxIdAuthorizationServiceEvidence
        {
            UserServiceId = userServiceId,
            ServiceSlug = serviceSlug,
            DisplayName = ToDisplayName(serviceSlug),
            Access = NyxIdAuthorizationAccess.Permitted,
            NodeGrantRequirement = AuthorizationGrantRequirement.Required,
            ResourceOwner = ResourceOwner(),
        };
        service.NodeIds.Add("node-a");
        service.NodeIds.Add("node-z");
        return service;
    }

    private static NyxIdAuthorizationLLMTargetEvidence ServiceTarget(
        string userServiceId,
        string serviceSlug,
        string modelId) => new()
        {
            RouteKind = LLMRouteKind.NyxIdUserService,
            RouteValue = $"/api/v1/proxy/s/{serviceSlug}",
            NyxIdUserServiceId = userServiceId,
            ServiceSlugSnapshot = serviceSlug,
            ModelCatalog = EnumeratedCatalog(modelId),
            ObservedAt = Timestamp.FromDateTimeOffset(ObservedAt),
            FreshUntil = Timestamp.FromDateTimeOffset(ObservedAt.AddMinutes(15)),
            EvaluatedAt = Timestamp.FromDateTimeOffset(ObservedAt.AddMinutes(-1)),
            AuthorityContractVersion = "1",
            AuthorityPolicyVersion = "llm-model-catalog-v1",
        };

    private static NyxIdAuthorizationLLMTargetEvidence GatewayTarget(string modelId) => new()
    {
        RouteKind = LLMRouteKind.Gateway,
        RouteValue = LLMSelectionPolicy.GatewayRoute,
        ModelCatalog = EnumeratedCatalog(modelId),
        ObservedAt = Timestamp.FromDateTimeOffset(ObservedAt),
        FreshUntil = Timestamp.FromDateTimeOffset(ObservedAt.AddMinutes(15)),
        EvaluatedAt = Timestamp.FromDateTimeOffset(ObservedAt.AddMinutes(-1)),
        AuthorityContractVersion = "1",
        AuthorityPolicyVersion = "llm-model-catalog-v1",
    };

    private static LLMModelCatalog EnumeratedCatalog(string modelId) => new()
    {
        Certainty = LLMModelCatalogCertainty.Enumerated,
        ModelIds = { modelId },
        DefaultModelId = modelId,
    };

    private static string ToDisplayName(string serviceSlug) => serviceSlug switch
    {
        "calendar" => "Calendar",
        "mail" => "Mail",
        _ => serviceSlug,
    };

    private static async Task BeginRefreshAsync(
        NyxIdAuthorizationCatalogGAgent agent,
        AuthorizationOwnerIdentity owner,
        string refreshId,
        DateTimeOffset startedAt,
        long expectedLifecycleFence = 0)
    {
        if (agent.EventSourcing == null)
            await agent.ActivateAsync();

        await agent.HandleBeginRefreshAsync(new BeginNyxIdAuthorizationCatalogRefreshCommand
        {
            Owner = owner.Clone(),
            RefreshId = refreshId,
            StartedAt = Timestamp.FromDateTimeOffset(startedAt),
            ExpectedLifecycleFence = expectedLifecycleFence,
        });
    }

    private static async Task<IReadOnlyList<NyxIdAuthorizationCatalogRefreshOutcomeEvent>> RefreshOutcomesAsync(
        InMemoryEventStore eventStore,
        string actorId,
        string refreshId) => (await eventStore.GetEventsAsync(actorId))
        .Where(static evt => evt.EventData.Is(NyxIdAuthorizationCatalogRefreshOutcomeEvent.Descriptor))
        .Select(static evt => evt.EventData.Unpack<NyxIdAuthorizationCatalogRefreshOutcomeEvent>())
        .Where(outcome => string.Equals(outcome.RefreshId, refreshId, StringComparison.Ordinal))
        .ToArray();

    private static NyxIdAuthorizationCatalogState Transition(
        NyxIdAuthorizationCatalogGAgent agent,
        NyxIdAuthorizationCatalogState state,
        IMessage evt)
    {
        var method = typeof(NyxIdAuthorizationCatalogGAgent).GetMethod(
            "TransitionState",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("TransitionState method not found.");
        return (NyxIdAuthorizationCatalogState)method.Invoke(agent, [state, evt])!;
    }

    private static EventEnvelope CommittedEnvelope(
        NyxIdAuthorizationCatalogState state,
        long version,
        string eventId) => new()
    {
        Id = eventId,
        Timestamp = Timestamp.FromDateTimeOffset(ObservedAt),
        Payload = Any.Pack(new CommittedStateEventPublished
        {
            StateEvent = new StateEvent
            {
                EventId = eventId,
                Version = version,
                EventData = Any.Pack(Observed(Owner(), "digest-19")),
                Timestamp = Timestamp.FromDateTimeOffset(ObservedAt),
            },
            StateRoot = Any.Pack(state),
        }),
    };

    private static async Task AssertLastRefreshOutcomeAsync(
        InMemoryEventStore eventStore,
        string actorId,
        string refreshId,
        NyxIdAuthorizationCatalogRefreshOutcomeStatusState expectedStatus)
    {
        var payload = (await eventStore.GetEventsAsync(actorId))[^1].EventData;
        payload.Is(NyxIdAuthorizationCatalogRefreshOutcomeEvent.Descriptor).Should().BeTrue();
        var outcome = payload.Unpack<NyxIdAuthorizationCatalogRefreshOutcomeEvent>();
        outcome.RefreshId.Should().Be(refreshId);
        outcome.Status.Should().Be(expectedStatus);
        outcome.StateVersion.Should().Be((await eventStore.GetEventsAsync(actorId))[^1].Version - 1);
    }

    private sealed class RecordingPublicationHook : ICommittedStatePublicationHook
    {
        public List<CommittedStatePublicationContext> Contexts { get; } = [];

        public Task BeforePublishAsync(
            CommittedStatePublicationContext context,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Contexts.Add(context);
            return Task.CompletedTask;
        }
    }

    private sealed class RefreshOutcomeRejectingEventStore(InMemoryEventStore inner) : IEventStore
    {
        public bool RejectRefreshOutcomes { get; set; }

        public Task<EventStoreCommitResult> AppendAsync(
            string agentId,
            IEnumerable<StateEvent> events,
            long expectedVersion,
            CancellationToken ct = default)
        {
            var batch = events.ToArray();
            if (RejectRefreshOutcomes && batch.Any(static stateEvent =>
                    stateEvent.EventData?.Is(
                        NyxIdAuthorizationCatalogRefreshOutcomeEvent.Descriptor) == true))
            {
                throw new InvalidOperationException("event-store-batch-failure");
            }

            return inner.AppendAsync(agentId, batch, expectedVersion, ct);
        }

        public Task<IReadOnlyList<StateEvent>> GetEventsAsync(
            string agentId,
            long? fromVersion = null,
            CancellationToken ct = default) =>
            inner.GetEventsAsync(agentId, fromVersion, ct);

        public Task<long> GetVersionAsync(
            string agentId,
            CancellationToken ct = default) =>
            inner.GetVersionAsync(agentId, ct);

        public Task<long> DeleteEventsUpToAsync(
            string agentId,
            long toVersion,
            CancellationToken ct = default) =>
            inner.DeleteEventsUpToAsync(agentId, toVersion, ct);
    }
}

file static class ProtobufDescriptorTestExtensions
{
    public static Google.Protobuf.Reflection.MessageDescriptor? DescriptorForType(
        this Google.Protobuf.Reflection.MessageDescriptor descriptor,
        string name) => descriptor.File.MessageTypes.SingleOrDefault(candidate => candidate.Name == name);
}
