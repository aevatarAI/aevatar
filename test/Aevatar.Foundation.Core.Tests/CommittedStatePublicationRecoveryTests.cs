using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Persistence;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Aevatar.Foundation.Core.Tests;

public sealed class CommittedStatePublicationRecoveryTests
{
    [Fact]
    public async Task CommitSucceededBeforePublish_ActivationPublishesOriginalCommittedFact()
    {
        const string actorId = "publication-before-publish";
        var fixture = CreateFixture();
        var failingPublisher = new RecordingPublisher(failOnAttempt: 1);
        var first = CreateAgent(actorId, fixture, failingPublisher);
        await first.ActivateAsync();

        var error = await Should.ThrowAsync<CommittedStatePublicationException>(
            () => first.CommitAsync(4));
        error.Stage.ShouldBe(CommittedStatePublicationFailureStage.AdapterAcceptance);
        var committed = (await fixture.EventStore.GetEventsAsync(actorId)).ShouldHaveSingleItem();
        (await fixture.PublicationStore.LoadAsync(actorId))!.PublishedVersion.ShouldBe(0);

        var recoveredPublisher = new RecordingPublisher();
        var recovered = CreateAgent(actorId, fixture, recoveredPublisher);
        await recovered.ActivateAsync();

        recovered.State.Count.ShouldBe(4);
        var recoveredFact = recoveredPublisher.Accepted.ShouldHaveSingleItem();
        recoveredFact.StateEvent.EventId.ShouldBe(committed.EventId);
        recoveredFact.StateEvent.Version.ShouldBe(1);
        recoveredFact.StateRoot.Unpack<CounterState>().Count.ShouldBe(4);
        var checkpoint = await fixture.PublicationStore.LoadAsync(actorId);
        checkpoint!.PublishedVersion.ShouldBe(1);
        checkpoint.PublishedEventId.ShouldBe(committed.EventId);
        checkpoint.Failure.ShouldBeNull();
    }

    [Fact]
    public async Task PublishSucceededBeforeCheckpoint_ActivationDuplicatesStableIdentityAndAdvancesCheckpoint()
    {
        const string actorId = "publication-after-publish";
        var durableStore = new InMemoryCommittedStatePublicationStateStore();
        var failOnceStore = new FailOnceAdvancePublicationStateStore(durableStore);
        var fixture = CreateFixture(failOnceStore);
        var firstPublisher = new RecordingPublisher();
        var first = CreateAgent(actorId, fixture, firstPublisher);
        await first.ActivateAsync();

        var error = await Should.ThrowAsync<CommittedStatePublicationException>(
            () => first.CommitAsync(5));
        error.Stage.ShouldBe(CommittedStatePublicationFailureStage.Checkpoint);
        var acceptedBeforeCrash = firstPublisher.Accepted.ShouldHaveSingleItem();
        (await durableStore.LoadAsync(actorId))!.PublishedVersion.ShouldBe(0);

        var recoveredPublisher = new RecordingPublisher();
        var recovered = CreateAgent(actorId, fixture, recoveredPublisher);
        await recovered.ActivateAsync();

        var duplicate = recoveredPublisher.Accepted.ShouldHaveSingleItem();
        duplicate.StateEvent.EventId.ShouldBe(acceptedBeforeCrash.StateEvent.EventId);
        duplicate.StateEvent.Version.ShouldBe(acceptedBeforeCrash.StateEvent.Version);
        (await durableStore.LoadAsync(actorId))!.PublishedVersion.ShouldBe(1);
    }

    [Fact]
    public async Task CheckpointSucceeded_ActivationDoesNotScanConfirmedRangeAgain()
    {
        const string actorId = "publication-checkpointed";
        var fixture = CreateFixture();
        var first = CreateAgent(actorId, fixture, new RecordingPublisher());
        await first.ActivateAsync();
        await first.CommitAsync(2);

        var recoveredPublisher = new RecordingPublisher();
        var recovered = CreateAgent(actorId, fixture, recoveredPublisher);
        await recovered.ActivateAsync();

        recovered.State.Count.ShouldBe(2);
        recoveredPublisher.Accepted.ShouldBeEmpty();
        (await fixture.PublicationStore.LoadAsync(actorId))!.PublishedVersion.ShouldBe(1);
    }

    [Fact]
    public async Task SnapshotAndCompaction_CannotPassUnpublishedVersion()
    {
        const string actorId = "publication-compaction-boundary";
        var fixture = CreateFixture(snapshotInterval: 1, enableCompaction: true);
        var agent = CreateAgent(actorId, fixture, new RecordingPublisher(failOnAttempt: 1));
        await agent.ActivateAsync();
        await Should.ThrowAsync<CommittedStatePublicationException>(() => agent.CommitAsync(3));

        await agent.EventSourcing!.PersistSnapshotAsync(agent.State);

        (await fixture.SnapshotStore.LoadAsync(actorId)).ShouldBeNull();
        (await fixture.EventStore.GetEventsAsync(actorId)).ShouldHaveSingleItem();
        (await fixture.PublicationStore.LoadAsync(actorId))!.PublishedVersion.ShouldBe(0);
    }

    [Fact]
    public async Task PartialBatchPublication_ActivationRecoversOnlyMissingVersionsInOrder()
    {
        const string actorId = "publication-partial-batch";
        var fixture = CreateFixture();
        var firstPublisher = new RecordingPublisher(failOnAttempt: 2);
        var first = CreateAgent(actorId, fixture, firstPublisher);
        await first.ActivateAsync();

        await Should.ThrowAsync<CommittedStatePublicationException>(
            () => first.CommitAsync(1, 2, 3));
        var committed = await fixture.EventStore.GetEventsAsync(actorId);
        committed.Select(static x => x.Version).ShouldBe([1, 2, 3]);
        (await fixture.PublicationStore.LoadAsync(actorId))!.PublishedVersion.ShouldBe(1);

        var recoveredPublisher = new RecordingPublisher();
        var recovered = CreateAgent(actorId, fixture, recoveredPublisher);
        await recovered.ActivateAsync();

        recoveredPublisher.Accepted.Select(static x => x.StateEvent.Version).ShouldBe([2, 3]);
        recoveredPublisher.Accepted.Select(static x => x.StateEvent.EventId)
            .ShouldBe(committed.Skip(1).Select(static x => x.EventId));
        recoveredPublisher.Accepted.Select(static x => x.StateRoot.Unpack<CounterState>().Count)
            .ShouldBe([3, 6]);
        recovered.State.Count.ShouldBe(6);
        (await fixture.PublicationStore.LoadAsync(actorId))!.PublishedVersion.ShouldBe(3);
    }

    [Fact]
    public async Task MissingCommittedVersion_ProducesExplicitRecoveryFailure()
    {
        const string actorId = "publication-poison-gap";
        var eventStore = new InMemoryEventStore();
        var publicationStore = new InMemoryCommittedStatePublicationStateStore();
        await publicationStore.InitializeAsync(actorId, 0);
        await eventStore.AppendAsync(
            actorId,
            [BuildStateEvent(actorId, "event-1", version: 1, amount: 1),
             BuildStateEvent(actorId, "event-2", version: 2, amount: 2)],
            expectedVersion: 0);
        await eventStore.DeleteEventsUpToAsync(actorId, 1);
        var fixture = CreateFixture(publicationStore, eventStore);
        var agent = CreateAgent(actorId, fixture, new RecordingPublisher());

        var error = await Should.ThrowAsync<CommittedStatePublicationRecoveryException>(
            () => agent.ActivateAsync());

        error.PublishedVersion.ShouldBe(0);
        error.StoreVersion.ShouldBe(2);
        error.Reason.ShouldContain("version 1");
    }

    [Fact]
    public async Task MissingTrailingCommittedVersions_CannotUseVersionDriftRecoveryToSkipPublication()
    {
        const string actorId = "publication-poison-trailing-gap";
        var eventStore = new InMemoryEventStore();
        var publicationStore = new InMemoryCommittedStatePublicationStateStore();
        await publicationStore.InitializeAsync(actorId, 0);
        await eventStore.AppendAsync(
            actorId,
            [BuildStateEvent(actorId, "event-1", version: 1, amount: 1),
             BuildStateEvent(actorId, "event-2", version: 2, amount: 2)],
            expectedVersion: 0);
        await eventStore.DeleteEventsUpToAsync(actorId, 2);
        var fixture = CreateFixture(
            publicationStore,
            eventStore,
            recoverFromVersionDriftOnReplay: true);
        var agent = CreateAgent(actorId, fixture, new RecordingPublisher());

        var error = await Should.ThrowAsync<CommittedStatePublicationRecoveryException>(
            () => agent.ActivateAsync());

        error.PublishedVersion.ShouldBe(0);
        error.StoreVersion.ShouldBe(2);
        error.Reason.ShouldContain("version 1");
    }

    [Fact]
    public async Task RuntimePublicationRetry_RecoversPendingFactWithoutReexecutingBusinessHandler()
    {
        const string actorId = "publication-runtime-retry";
        var fixture = CreateFixture();
        var publisher = new RecordingPublisher(failOnAttempt: 1);
        var agent = CreateAgent(actorId, fixture, publisher);
        await agent.ActivateAsync();
        var original = TestHelper.Envelope(new IncrementEvent { Amount = 9 });

        await Should.ThrowAsync<CommittedStatePublicationException>(
            () => agent.HandleEventAsync(original));
        var retry = original.Clone();
        retry.EnsureRuntime().Retry = new EnvelopeRetryContext
        {
            OriginEventId = original.Id,
            Attempt = 1,
            LastErrorType = nameof(CommittedStatePublicationException),
        };

        await agent.HandleEventAsync(retry);

        agent.State.Count.ShouldBe(9);
        (await fixture.EventStore.GetEventsAsync(actorId)).Count.ShouldBe(1);
        publisher.Accepted.ShouldHaveSingleItem();
        (await fixture.PublicationStore.LoadAsync(actorId))!.PublishedVersion.ShouldBe(1);
    }

    private static StateEvent BuildStateEvent(
        string actorId,
        string eventId,
        long version,
        int amount) =>
        new()
        {
            AgentId = actorId,
            EventId = eventId,
            Version = version,
            EventType = IncrementEvent.Descriptor.FullName,
            EventData = Google.Protobuf.WellKnownTypes.Any.Pack(
                new IncrementEvent { Amount = amount }),
        };

    private static RecoveryFixture CreateFixture(
        ICommittedStatePublicationStateStore? publicationStore = null,
        InMemoryEventStore? eventStore = null,
        int snapshotInterval = 200,
        bool enableCompaction = false,
        bool recoverFromVersionDriftOnReplay = false)
    {
        eventStore ??= new InMemoryEventStore();
        publicationStore ??= new InMemoryCommittedStatePublicationStateStore();
        var snapshotStore = new InMemoryEventSourcingSnapshotStore<CounterState>();
        var services = new ServiceCollection()
            .AddRuntimeScheduler()
            .AddSingleton<IEventStore>(eventStore)
            .AddSingleton<ICommittedStatePublicationStateStore>(publicationStore)
            .AddSingleton<IEventSourcingSnapshotStore<CounterState>>(snapshotStore)
            .AddSingleton(new EventSourcingRuntimeOptions
            {
                EnableSnapshots = true,
                SnapshotInterval = snapshotInterval,
                EnableEventCompaction = enableCompaction,
                RetainedEventsAfterSnapshot = 0,
                RecoverFromVersionDriftOnReplay = recoverFromVersionDriftOnReplay,
            })
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
            .AddSingleton<IStateEventApplier<CounterState>, CounterIncrementApplier>()
            .BuildServiceProvider();
        return new RecoveryFixture(eventStore, publicationStore, snapshotStore, services);
    }

    private static RecoveryAgent CreateAgent(
        string actorId,
        RecoveryFixture fixture,
        RecordingPublisher publisher)
    {
        var agent = new RecoveryAgent
        {
            Services = fixture.Services,
            CommittedStateEventPublisher = publisher,
            EventSourcingBehaviorFactory =
                fixture.Services.GetRequiredService<IEventSourcingBehaviorFactory<CounterState>>(),
        };
        agent.SetId(actorId);
        return agent;
    }

    private sealed record RecoveryFixture(
        InMemoryEventStore EventStore,
        ICommittedStatePublicationStateStore PublicationStore,
        InMemoryEventSourcingSnapshotStore<CounterState> SnapshotStore,
        ServiceProvider Services);

    private sealed class RecoveryAgent : TestGAgentBase<CounterState>
    {
        [EventHandler]
        public Task HandleIncrement(IncrementEvent evt) => PersistDomainEventAsync(evt);

        public Task CommitAsync(params int[] amounts) =>
            PersistDomainEventsAsync(
                amounts.Select(static amount => (IMessage)new IncrementEvent { Amount = amount }));
    }

    private sealed class CounterIncrementApplier
        : StateEventApplierBase<CounterState, IncrementEvent>
    {
        protected override CounterState Apply(CounterState current, IncrementEvent evt) =>
            new()
            {
                Count = current.Count + evt.Amount,
                Name = current.Name,
            };
    }

    private sealed class RecordingPublisher(int failOnAttempt = 0) : ICommittedStateEventPublisher
    {
        private int _attempts;

        public List<CommittedStateEventPublished> Accepted { get; } = [];

        public Task PublishAsync(
            CommittedStateEventPublished evt,
            ObserverAudience audience = ObserverAudience.CommittedFacts,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
        {
            _ = audience;
            _ = sourceEnvelope;
            _ = options;
            ct.ThrowIfCancellationRequested();
            _attempts++;
            if (_attempts == failOnAttempt)
                throw new InvalidOperationException("Injected publication failure.");

            Accepted.Add(evt.Clone());
            return Task.CompletedTask;
        }
    }

    private sealed class FailOnceAdvancePublicationStateStore(
        ICommittedStatePublicationStateStore inner)
        : ICommittedStatePublicationStateStore
    {
        private bool _failed;

        public Task<CommittedStatePublicationState?> LoadAsync(
            string actorId,
            CancellationToken ct = default) =>
            inner.LoadAsync(actorId, ct);

        public Task<CommittedStatePublicationState> InitializeAsync(
            string actorId,
            long baselinePublishedVersion,
            CancellationToken ct = default) =>
            inner.InitializeAsync(actorId, baselinePublishedVersion, ct);

        public Task<CommittedStatePublicationState> AdvanceAsync(
            string actorId,
            long expectedPublishedVersion,
            StateEvent publishedEvent,
            CancellationToken ct = default)
        {
            if (!_failed)
            {
                _failed = true;
                throw new InvalidOperationException("Injected checkpoint failure.");
            }

            return inner.AdvanceAsync(actorId, expectedPublishedVersion, publishedEvent, ct);
        }

        public Task<CommittedStatePublicationState> RecordFailureAsync(
            string actorId,
            long expectedPublishedVersion,
            StateEvent failedEvent,
            CommittedStatePublicationFailureStage stage,
            Exception error,
            CancellationToken ct = default) =>
            inner.RecordFailureAsync(actorId, expectedPublishedVersion, failedEvent, stage, error, ct);
    }
}
