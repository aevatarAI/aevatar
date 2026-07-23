using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Maintenance;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Runtime.Hosting.Maintenance;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.Device;
using Aevatar.GAgents.Scheduled;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Capabilities.Tests;

public sealed class RetiredActorCleanupHostedServiceTests
{
    private const string RetiredSkillRunnerAgentType = "skill_runner";

    [Fact]
    public async Task StartAsync_ShouldDestroyRetiredActors_RemoveRelays_AndResetEventStreams()
    {
        var eventStore = new InMemoryEventStore();
        await AppendSingleEventAsync(eventStore, SyntheticRetiredActorSpec.ActorBodyId);
        await AppendSingleEventAsync(eventStore, SyntheticRetiredActorSpec.ProjectionScopeId);
        var kindProbe = new StubActorKindProbe(new Dictionary<string, string?>
        {
            [SyntheticRetiredActorSpec.ActorBodyId] = SyntheticRetiredActorSpec.ActorBodyKind,
            [SyntheticRetiredActorSpec.ProjectionScopeId] = SyntheticRetiredActorSpec.ProjectionScopeKind,
        });
        var runtime = new RecordingActorRuntime();
        var streamProvider = new RecordingStreamProvider();
        streamProvider.SeedRelay(SyntheticRetiredActorSpec.ActorBodyId, "stale-child-stream");
        var service = CreateService(kindProbe, runtime, streamProvider, eventStore, new SyntheticRetiredActorSpec());

        await RunStartupCleanupAsync(service);

        runtime.DestroyedActorIds.Should().Contain(SyntheticRetiredActorSpec.ActorBodyId);
        runtime.DestroyedActorIds.Should().Contain(SyntheticRetiredActorSpec.ProjectionScopeId);
        streamProvider.RemovedRelays.Should().Contain((
            SyntheticRetiredActorSpec.ActorBodyId,
            SyntheticRetiredActorSpec.ProjectionScopeId));
        streamProvider.RemovedRelays.Should().Contain((SyntheticRetiredActorSpec.ActorBodyId, "stale-child-stream"));
        (await eventStore.GetVersionAsync(SyntheticRetiredActorSpec.ActorBodyId)).Should().Be(0);
        (await eventStore.GetVersionAsync(SyntheticRetiredActorSpec.ProjectionScopeId)).Should().Be(0);
    }

    [Fact]
    public async Task StartAsync_ShouldNotDestroyActor_WhenRuntimeKindIsCurrent()
    {
        var eventStore = new InMemoryEventStore();
        await AppendSingleEventAsync(eventStore, "channel-bot-registration-store");
        var kindProbe = new StubActorKindProbe(new Dictionary<string, string?>
        {
            ["channel-bot-registration-store"] = "channel.runtime.channel-bot-registration",
        });
        var runtime = new RecordingActorRuntime();
        var service = CreateService(
            kindProbe, runtime, new RecordingStreamProvider(), eventStore, CreateChannelRuntimeSpec());

        await RunStartupCleanupAsync(service);

        runtime.DestroyedActorIds.Should().BeEmpty();
        (await eventStore.GetVersionAsync("channel-bot-registration-store")).Should().Be(1);
    }

    [Fact]
    public async Task StartAsync_ShouldNotDestroyActor_WhenRetiredKindTokenIsOnlySubstring()
    {
        var eventStore = new InMemoryEventStore();
        await AppendSingleEventAsync(eventStore, "channel-bot-registration-store");
        var kindProbe = new StubActorKindProbe(new Dictionary<string, string?>
        {
            ["channel-bot-registration-store"] = "channel-runtime.channel-bot-registration-proxy",
        });
        var runtime = new RecordingActorRuntime();
        var service = CreateService(
            kindProbe, runtime, new RecordingStreamProvider(), eventStore, CreateChannelRuntimeSpec());

        await RunStartupCleanupAsync(service);

        runtime.DestroyedActorIds.Should().BeEmpty();
        (await eventStore.GetVersionAsync("channel-bot-registration-store")).Should().Be(1);
    }

    [Fact]
    public async Task StartAsync_ShouldResetStream_WhenActorStateWasAlreadyDestroyed()
    {
        var eventStore = new InMemoryEventStore();
        await AppendSingleEventAsync(eventStore, "channel-bot-registration-store");
        var kindProbe = new StubActorKindProbe(new Dictionary<string, string?>());
        var runtime = new RecordingActorRuntime();
        var service = CreateService(
            kindProbe, runtime, new RecordingStreamProvider(), eventStore, CreateChannelRuntimeSpec());

        await RunStartupCleanupAsync(service);

        runtime.DestroyedActorIds.Should().Contain("channel-bot-registration-store");
        (await eventStore.GetVersionAsync("channel-bot-registration-store")).Should().Be(0);
    }

    [Fact]
    public async Task StartAsync_ShouldCleanRetiredUserAgentsDiscoveredFromCatalogBeforeCatalogReset()
    {
        var eventStore = new InMemoryEventStore();
        var documents = new RecordingProjectionStore<UserAgentCatalogDocument>(
            CatalogDocument("skill-runner-old", RetiredSkillRunnerAgentType),
            CatalogDocument("workflow-agent-old", "workflow_agent"),
            CatalogDocument("skill-runner-current", RetiredSkillRunnerAgentType),
            CatalogDocument("skill-runner-proxy", RetiredSkillRunnerAgentType),
            new UserAgentCatalogDocument
            {
                Id = "skill-runner-prefix-only",
                ActorId = "agent-registry-store",
            });
        await AppendSingleEventAsync(eventStore, "skill-runner-old");
        await AppendSingleEventAsync(eventStore, "workflow-agent-old");
        await AppendSingleEventAsync(eventStore, "skill-runner-current");
        await AppendSingleEventAsync(eventStore, "skill-runner-proxy");
        await AppendSingleEventAsync(eventStore, "skill-runner-prefix-only");

        var kindProbe = new StubActorKindProbe(new Dictionary<string, string?>
        {
            ["agent-registry-store"] = "channel-runtime.user-agent-catalog",
            ["skill-runner-old"] = "channel-runtime.skill-runner",
            ["workflow-agent-old"] = "channel-runtime.workflow-agent",
            ["skill-runner-current"] = "scheduled.skill-runner",
            ["skill-runner-proxy"] = "channel-runtime.skill-runner-proxy",
            ["skill-runner-prefix-only"] = "channel-runtime.skill-runner",
        });
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddSingleton<Aevatar.Foundation.Abstractions.Persistence.IEventStore>(eventStore);
        serviceCollection.AddSingleton<IActorKindProbe>(kindProbe);
        serviceCollection.AddSingleton<IProjectionDocumentReader<UserAgentCatalogDocument, string>>(documents);
        serviceCollection.AddSingleton<IProjectionWriteDispatcher<UserAgentCatalogDocument>>(documents);

        var runtime = new RecordingActorRuntime();
        var service = CreateService(
            kindProbe,
            runtime,
            new RecordingStreamProvider(),
            eventStore,
            CreateScheduledSpec(),
            serviceCollection.BuildServiceProvider());

        await RunStartupCleanupAsync(service);

        runtime.DestroyedActorIds.Should().Contain("skill-runner-old");
        runtime.DestroyedActorIds.Should().Contain("workflow-agent-old");
        runtime.DestroyedActorIds.Should().Contain("agent-registry-store");
        runtime.DestroyedActorIds.Should().NotContain("skill-runner-current");
        runtime.DestroyedActorIds.Should().NotContain("skill-runner-proxy");
        runtime.DestroyedActorIds.Should().NotContain("skill-runner-prefix-only");
        (await eventStore.GetVersionAsync("skill-runner-old")).Should().Be(0);
        (await eventStore.GetVersionAsync("workflow-agent-old")).Should().Be(0);
        (await eventStore.GetVersionAsync("skill-runner-current")).Should().Be(1);
        (await eventStore.GetVersionAsync("skill-runner-proxy")).Should().Be(1);
        (await eventStore.GetVersionAsync("skill-runner-prefix-only")).Should().Be(1);
        (await eventStore.GetVersionAsync("agent-registry-store")).Should().Be(0);
    }

    [Fact]
    public async Task StartAsync_ShouldSkipCatalogWalk_WhenCatalogRuntimeKindIsAlreadyCurrent()
    {
        // Once the catalog actor is on the new namespace, the cleanup must not
        // query agent-registry-store on every startup nor probe per-entry actors —
        // otherwise warm clusters pay an unbounded scan cost forever.
        var eventStore = new InMemoryEventStore();
        await AppendSingleEventAsync(eventStore, "skill-runner-already-migrated");

        var probedActorIds = new List<string>();
        var kindProbe = new RecordingKindProbe(probedActorIds, new Dictionary<string, string?>
        {
            ["agent-registry-store"] = "scheduled.user-agent-catalog",
        });
        var runtime = new RecordingActorRuntime();
        var service = CreateService(
            kindProbe, runtime, new RecordingStreamProvider(), eventStore, CreateScheduledSpec());

        await RunStartupCleanupAsync(service);

        runtime.DestroyedActorIds.Should().BeEmpty();
        probedActorIds.Should().NotContain("skill-runner-already-migrated");
        (await eventStore.GetVersionAsync("skill-runner-already-migrated")).Should().Be(1);
    }

    [Fact]
    public async Task StartAsync_ShouldDiscoverRetiredUserAgentsFromReadModel_WhenCatalogStreamHasBeenCompacted()
    {
        // Refactor (iter22/cluster-003):
        //   Old pattern: retired actor discovery replayed catalog event streams after read-model lookup.
        //   New principle: compacted/generated actor cleanup uses typed UserAgentCatalogDocument rows only.
        var eventStore = new InMemoryEventStore();
        // No catalog events — represents the post-compaction scenario.
        await AppendSingleEventAsync(eventStore, "agent-registry-store");
        await AppendSingleEventAsync(eventStore, "skill-runner-snapshotted");
        await AppendSingleEventAsync(eventStore, "workflow-agent-snapshotted");

        var documents = new RecordingProjectionStore<UserAgentCatalogDocument>(
            new UserAgentCatalogDocument
            {
                Id = "skill-runner-snapshotted",
                ActorId = "agent-registry-store",
                AgentType = RetiredSkillRunnerAgentType,
            },
            new UserAgentCatalogDocument
            {
                Id = "workflow-agent-snapshotted",
                ActorId = "agent-registry-store",
                AgentType = "workflow_agent",
            });
        var kindProbe = new StubActorKindProbe(new Dictionary<string, string?>
        {
            ["agent-registry-store"] = "channel-runtime.user-agent-catalog",
            ["skill-runner-snapshotted"] = "channel-runtime.skill-runner",
            ["workflow-agent-snapshotted"] = "channel-runtime.workflow-agent",
        });
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddSingleton<Aevatar.Foundation.Abstractions.Persistence.IEventStore>(eventStore);
        serviceCollection.AddSingleton<IActorKindProbe>(kindProbe);
        serviceCollection.AddSingleton<IProjectionDocumentReader<UserAgentCatalogDocument, string>>(documents);
        serviceCollection.AddSingleton<IProjectionWriteDispatcher<UserAgentCatalogDocument>>(documents);

        var runtime = new RecordingActorRuntime();
        var service = CreateService(
            kindProbe,
            runtime,
            new RecordingStreamProvider(),
            eventStore,
            CreateScheduledSpec(),
            serviceCollection.BuildServiceProvider());

        await RunStartupCleanupAsync(service);

        runtime.DestroyedActorIds.Should().Contain("skill-runner-snapshotted");
        runtime.DestroyedActorIds.Should().Contain("workflow-agent-snapshotted");
        runtime.DestroyedActorIds.Should().Contain("agent-registry-store");
        (await eventStore.GetVersionAsync("skill-runner-snapshotted")).Should().Be(0);
        (await eventStore.GetVersionAsync("workflow-agent-snapshotted")).Should().Be(0);
    }

    [Fact]
    public async Task StartAsync_ShouldStillDestroyActor_WhenIncomingRelayRemovalThrows()
    {
        // The incoming-relay removal targets the parent stream's topology — a
        // transient stream-provider failure there must not abort the destroy +
        // event-stream reset path that the cleanup is here to perform.
        var eventStore = new InMemoryEventStore();
        var projectionScopeActorId = SyntheticRetiredActorSpec.ProjectionScopeId;
        await AppendSingleEventAsync(eventStore, projectionScopeActorId);
        var kindProbe = new StubActorKindProbe(new Dictionary<string, string?>
        {
            [projectionScopeActorId] = SyntheticRetiredActorSpec.ProjectionScopeKind,
        });
        var runtime = new RecordingActorRuntime();
        // The projection-scope target's incoming-relay cleanup resolves its SourceStreamId
        // (the actor-body stream); make that throw to assert the destroy + reset path survives.
        var streamProvider = new ThrowingRelayStreamProvider(SyntheticRetiredActorSpec.ActorBodyId);
        var service = CreateService(
            kindProbe, runtime, streamProvider, eventStore, new SyntheticRetiredActorSpec());

        await RunStartupCleanupAsync(service);

        runtime.DestroyedActorIds.Should().Contain(projectionScopeActorId);
        (await eventStore.GetVersionAsync(projectionScopeActorId)).Should().Be(0);
    }

    [Fact]
    public async Task StartAsync_ShouldSkipDynamicTargets_WhenReadModelDiscoveryThrows()
    {
        // Projection store unavailable must NOT abort startup cleanup, but it
        // also must not fall back to catalog event replay as a query path.
        var eventStore = new InMemoryEventStore();
        await AppendCatalogEventsAsync(eventStore,
        [
            new UserAgentCatalogEntry
            {
                AgentId = "skill-runner-recent",
                AgentType = RetiredSkillRunnerAgentType,
            },
        ]);
        await AppendSingleEventAsync(eventStore, "skill-runner-recent");
        var kindProbe = new StubActorKindProbe(new Dictionary<string, string?>
        {
            ["agent-registry-store"] = "channel-runtime.user-agent-catalog",
            ["skill-runner-recent"] = "channel-runtime.skill-runner",
        });
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddSingleton<Aevatar.Foundation.Abstractions.Persistence.IEventStore>(eventStore);
        serviceCollection.AddSingleton<IActorKindProbe>(kindProbe);
        serviceCollection.AddSingleton<IProjectionDocumentReader<UserAgentCatalogDocument, string>>(
            new ThrowingProjectionReader<UserAgentCatalogDocument>());
        serviceCollection.AddSingleton<IProjectionWriteDispatcher<UserAgentCatalogDocument>>(
            new NoopProjectionWriter<UserAgentCatalogDocument>());

        var runtime = new RecordingActorRuntime();
        var service = CreateService(
            kindProbe,
            runtime,
            new RecordingStreamProvider(),
            eventStore,
            CreateScheduledSpec(),
            serviceCollection.BuildServiceProvider());

        await RunStartupCleanupAsync(service);

        runtime.DestroyedActorIds.Should().NotContain("skill-runner-recent");
        runtime.DestroyedActorIds.Should().Contain("agent-registry-store");
        (await eventStore.GetVersionAsync("skill-runner-recent")).Should().Be(1);
        (await eventStore.GetVersionAsync("agent-registry-store")).Should().Be(0);
    }

    [Fact]
    public void ScheduledRetiredActorSpec_ShouldNotReintroduceCatalogEventReplayOrActorIdPrefixClassification()
    {
        // Refactor (iter22/cluster-003):
        //   Old pattern: retired actor discovery used GetEventsAsync/GetVersionAsync and actorId StartsWith.
        //   New principle: source stays on typed read-model enumeration without actorId pattern facts.
        var source = File.ReadAllText(GetScheduledRetiredActorSpecSourcePath());
        var code = StripLineComments(source);

        code.Should().NotContain("IEventStore");
        code.Should().NotContain("GetVersionAsync");
        code.Should().NotContain("GetEventsAsync");
        code.Should().NotContain("DiscoverFromCatalogEventsAsync");
        code.Should().NotContain("ActorIdPrefix");
        code.Should().NotContain("LegacyWorkflowAgentActorIdPrefix");
        code.Should().NotContain("StartsWith");
    }

    [Fact]
    public void RetiredActorCleanupHostedService_ShouldNotReintroduceEventStoreMarkerLeaseOrPolling()
    {
        // Refactor (issue1287-first):
        //   Old pattern: hosted cleanup used EventStore marker lease state and polling cadence.
        //   New principle: source stays on BackgroundService trigger + per-target revalidation.
        var source = File.ReadAllText(GetRetiredActorCleanupHostedServiceSourcePath());
        var code = StripLineComments(source);

        code.Should().NotContain("MarkerStreamId");
        code.Should().NotContain("CleanupLease");
        code.Should().NotContain("AppendMarkerAsync");
        code.Should().NotContain("ReadMarkerAsync");
        code.Should().NotContain("ReleaseLeaseAsync");
        code.Should().NotContain("Task.Delay");
        code.Should().NotContain("WaitPollMilliseconds");
        code.Should().NotContain("InProgressTimeoutSeconds");
    }

    [Fact]
    public async Task StartAsync_ShouldDeleteMatchingReadModels()
    {
        var eventStore = new InMemoryEventStore();
        await AppendSingleEventAsync(eventStore, "agent-registry-store");
        var documents = new RecordingProjectionStore<UserAgentCatalogDocument>(
            new UserAgentCatalogDocument
            {
                Id = "agent-doc-delete",
                ActorId = "agent-registry-store",
            },
            new UserAgentCatalogDocument
            {
                Id = "agent-doc-keep",
                ActorId = "other-store",
            });
        var kindProbe = new StubActorKindProbe(new Dictionary<string, string?>
        {
            ["agent-registry-store"] = "channel-runtime.user-agent-catalog",
        });
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddSingleton<Aevatar.Foundation.Abstractions.Persistence.IEventStore>(eventStore);
        serviceCollection.AddSingleton<IActorKindProbe>(kindProbe);
        serviceCollection.AddSingleton<IProjectionDocumentReader<UserAgentCatalogDocument, string>>(documents);
        serviceCollection.AddSingleton<IProjectionWriteDispatcher<UserAgentCatalogDocument>>(documents);
        var runtime = new RecordingActorRuntime();
        var service = CreateService(
            kindProbe,
            runtime,
            new RecordingStreamProvider(),
            eventStore,
            CreateScheduledSpec(),
            serviceCollection.BuildServiceProvider());

        await RunStartupCleanupAsync(service);

        documents.DeletedIds.Should().Equal("agent-doc-delete");
        documents.RemainingIds.Should().Equal("agent-doc-keep");
    }

    [Fact]
    public async Task StartAsync_ShouldContinue_WhenReadModelCleanupThrows()
    {
        var eventStore = new InMemoryEventStore();
        await AppendSingleEventAsync(eventStore, "channel-bot-registration-store");
        var kindProbe = new StubActorKindProbe(new Dictionary<string, string?>
        {
            ["channel-bot-registration-store"] = "channel-runtime.channel-bot-registration",
        });
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddSingleton<Aevatar.Foundation.Abstractions.Persistence.IEventStore>(eventStore);
        serviceCollection.AddSingleton<IActorKindProbe>(kindProbe);
        serviceCollection.AddSingleton<IProjectionDocumentReader<ChannelBotRegistrationDocument, string>>(
            new ThrowingProjectionReader<ChannelBotRegistrationDocument>());
        serviceCollection.AddSingleton<IProjectionWriteDispatcher<ChannelBotRegistrationDocument>>(
            new NoopProjectionWriter<ChannelBotRegistrationDocument>());
        var runtime = new RecordingActorRuntime();
        var service = CreateService(
            kindProbe,
            runtime,
            new RecordingStreamProvider(),
            eventStore,
            CreateChannelRuntimeSpec(),
            serviceCollection.BuildServiceProvider());

        await RunStartupCleanupAsync(service);

        runtime.DestroyedActorIds.Should().Contain("channel-bot-registration-store");
        (await eventStore.GetVersionAsync("channel-bot-registration-store")).Should().Be(0);
    }

    [Fact]
    public async Task StartAsync_ShouldResetStreamPubSub_ForEachCleanedActor()
    {
        // Stream pub/sub state (Orleans PubSubRendezvousGrain) lives outside the
        // event store and the GAgent. Skipping its reset leaves stale rendezvous
        // entries behind, which then block the next silo wave's
        // RegisterAsStreamProducer with InconsistentStateException — the bug
        // this hosted service is meant to prevent. Exercise that the cleanup
        // calls the IStreamPubSubMaintenance hook for every cleaned actor.
        var eventStore = new InMemoryEventStore();
        await AppendSingleEventAsync(eventStore, SyntheticRetiredActorSpec.ActorBodyId);
        await AppendSingleEventAsync(eventStore, SyntheticRetiredActorSpec.ProjectionScopeId);
        var kindProbe = new StubActorKindProbe(new Dictionary<string, string?>
        {
            [SyntheticRetiredActorSpec.ActorBodyId] = SyntheticRetiredActorSpec.ActorBodyKind,
            [SyntheticRetiredActorSpec.ProjectionScopeId] = SyntheticRetiredActorSpec.ProjectionScopeKind,
        });
        var runtime = new RecordingActorRuntime();
        var pubSub = new RecordingStreamPubSubMaintenance();
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddSingleton<Aevatar.Foundation.Abstractions.Persistence.IEventStore>(eventStore);
        serviceCollection.AddSingleton<IActorKindProbe>(kindProbe);
        serviceCollection.AddSingleton<IStreamPubSubMaintenance>(pubSub);
        var service = CreateService(
            kindProbe,
            runtime,
            new RecordingStreamProvider(),
            eventStore,
            new SyntheticRetiredActorSpec(),
            serviceCollection.BuildServiceProvider());

        await RunStartupCleanupAsync(service);

        pubSub.ResetActorIds.Should().Contain(SyntheticRetiredActorSpec.ActorBodyId);
        pubSub.ResetActorIds.Should().Contain(SyntheticRetiredActorSpec.ProjectionScopeId);
    }

    [Fact]
    public async Task StartAsync_ShouldContinue_WhenStreamPubSubResetThrows()
    {
        var eventStore = new InMemoryEventStore();
        await AppendSingleEventAsync(eventStore, "channel-bot-registration-store");
        var kindProbe = new StubActorKindProbe(new Dictionary<string, string?>
        {
            ["channel-bot-registration-store"] = "channel-runtime.channel-bot-registration",
        });
        var runtime = new RecordingActorRuntime();
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddSingleton<Aevatar.Foundation.Abstractions.Persistence.IEventStore>(eventStore);
        serviceCollection.AddSingleton<IActorKindProbe>(kindProbe);
        serviceCollection.AddSingleton<IStreamPubSubMaintenance>(new ThrowingStreamPubSubMaintenance());
        var service = CreateService(
            kindProbe,
            runtime,
            new RecordingStreamProvider(),
            eventStore,
            CreateChannelRuntimeSpec(),
            serviceCollection.BuildServiceProvider());

        await RunStartupCleanupAsync(service);

        runtime.DestroyedActorIds.Should().Contain("channel-bot-registration-store");
        (await eventStore.GetVersionAsync("channel-bot-registration-store")).Should().Be(0);
    }

    [Fact]
    public async Task StartAsync_ShouldRunEachRegisteredSpec()
    {
        var eventStore = new InMemoryEventStore();
        await AppendSingleEventAsync(eventStore, "device-registration-store");
        await AppendSingleEventAsync(eventStore, "channel-bot-registration-store");
        var kindProbe = new StubActorKindProbe(new Dictionary<string, string?>
        {
            ["device-registration-store"] = "channel-runtime.device-registration",
            ["channel-bot-registration-store"] = "channel-runtime.channel-bot-registration",
        });
        var runtime = new RecordingActorRuntime();
        var service = CreateService(
            kindProbe,
            runtime,
            new RecordingStreamProvider(),
            eventStore,
            specs: [CreateDeviceSpec(), CreateChannelRuntimeSpec()]);

        await RunStartupCleanupAsync(service);

        runtime.DestroyedActorIds.Should().Contain("device-registration-store");
        runtime.DestroyedActorIds.Should().Contain("channel-bot-registration-store");
        (await eventStore.GetVersionAsync("device-registration-store")).Should().Be(0);
        (await eventStore.GetVersionAsync("channel-bot-registration-store")).Should().Be(0);
    }

    [Fact]
    public async Task Cleanup_ShouldSkipDestructiveWork_WhenPerTargetRevalidationSeesCurrentRuntimeKind()
    {
        var eventStore = new InMemoryEventStore();
        await AppendSingleEventAsync(eventStore, "channel-bot-registration-store");
        var kindProbe = new SequencedKindProbe(new Dictionary<string, Queue<string?>>
        {
            ["channel-bot-registration-store"] = new Queue<string?>([
                "channel-runtime.channel-bot-registration",
                "channel.runtime.channel-bot-registration",
            ]),
        });
        var runtime = new RecordingActorRuntime();
        var service = CreateService(
            kindProbe, runtime, new RecordingStreamProvider(), eventStore, CreateChannelRuntimeSpec());

        await RunStartupCleanupAsync(service);

        runtime.DestroyedActorIds.Should().BeEmpty();
        (await eventStore.GetVersionAsync("channel-bot-registration-store")).Should().Be(1);
    }

    [Fact]
    public async Task StartAsync_ShouldReturnBeforeCleanupDiscoveryCompletes()
    {
        var eventStore = new InMemoryEventStore();
        await AppendSingleEventAsync(eventStore, "blocked-retired-actor");
        var kindProbe = new StubActorKindProbe(new Dictionary<string, string?>
        {
            ["blocked-retired-actor"] = "tests.retired-blocked-actor",
        });
        var runtime = new RecordingActorRuntime();
        var spec = new BlockingDiscoveryRetiredActorSpec("blocked-retired-actor");
        var service = CreateService(
            kindProbe, runtime, new RecordingStreamProvider(), eventStore, spec);

        await service.StartAsync(CancellationToken.None);

        await spec.DiscoveryEntered.Task;
        runtime.DestroyedActorIds.Should().BeEmpty();
        service.Completion.IsCompleted.Should().BeFalse();

        spec.ReleaseDiscovery();
        await service.Completion;

        runtime.DestroyedActorIds.Should().Contain("blocked-retired-actor");
        (await eventStore.GetVersionAsync("blocked-retired-actor")).Should().Be(0);
    }

    private static IRetiredActorSpec CreateChannelRuntimeSpec() => new ChannelRuntimeRetiredActorSpec();

    private static IRetiredActorSpec CreateDeviceSpec() => new DeviceRetiredActorSpec();

    private static IRetiredActorSpec CreateScheduledSpec() => new ScheduledRetiredActorSpec();

    private static RetiredActorCleanupHostedService CreateService(
        IActorKindProbe kindProbe,
        RecordingActorRuntime runtime,
        RecordingStreamProvider streamProvider,
        InMemoryEventStore eventStore,
        IRetiredActorSpec spec,
        IServiceProvider? services = null) =>
        CreateService(kindProbe, runtime, streamProvider, eventStore, [spec], services);

    private static RetiredActorCleanupHostedService CreateService(
        IActorKindProbe kindProbe,
        RecordingActorRuntime runtime,
        RecordingStreamProvider streamProvider,
        InMemoryEventStore eventStore,
        IReadOnlyList<IRetiredActorSpec> specs,
        IServiceProvider? services = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        var resolvedServices = services ?? BuildSpecServices(eventStore, kindProbe);

        return new RetiredActorCleanupHostedService(
            specs,
            kindProbe,
            runtime,
            streamProvider,
            eventStore,
            eventStore,
            resolvedServices,
            configuration,
            NullLogger<RetiredActorCleanupHostedService>.Instance);
    }

    private static IServiceProvider BuildSpecServices(
        InMemoryEventStore eventStore, IActorKindProbe kindProbe)
    {
        var services = new ServiceCollection();
        services.AddSingleton<Aevatar.Foundation.Abstractions.Persistence.IEventStore>(eventStore);
        services.AddSingleton(kindProbe);
        return services.BuildServiceProvider();
    }

    private static async Task RunStartupCleanupAsync(RetiredActorCleanupHostedService service)
    {
        await service.StartAsync(CancellationToken.None);
        await service.Completion;
    }

    private static Task AppendSingleEventAsync(InMemoryEventStore eventStore, string actorId) =>
        eventStore.AppendAsync(
            actorId,
            [
                new StateEvent
                {
                    AgentId = actorId,
                    EventId = Guid.NewGuid().ToString("N"),
                    EventType = StringValue.Descriptor.FullName,
                    EventData = Any.Pack(new StringValue { Value = "seed" }),
                    Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                    Version = 1,
                },
            ],
            expectedVersion: 0);

    private static Task AppendCatalogEventsAsync(
        InMemoryEventStore eventStore,
        IReadOnlyList<UserAgentCatalogEntry> entries)
    {
        var events = entries
            .Select((entry, index) => new StateEvent
            {
                AgentId = "agent-registry-store",
                EventId = Guid.NewGuid().ToString("N"),
                EventType = UserAgentCatalogUpsertedEvent.Descriptor.FullName,
                EventData = Any.Pack(new UserAgentCatalogUpsertedEvent
                {
                    Entry = entry,
                }),
                Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                Version = index + 1,
            })
            .ToArray();
        return eventStore.AppendAsync("agent-registry-store", events, expectedVersion: 0);
    }

    private static UserAgentCatalogDocument CatalogDocument(string agentId, string agentType) =>
        new()
        {
            Id = agentId,
            ActorId = "agent-registry-store",
            AgentType = agentType,
        };

    private static string StripLineComments(string source)
    {
        var lines = source
            .Split('\n')
            .Select(static line =>
            {
                var index = line.IndexOf("//", StringComparison.Ordinal);
                return index >= 0 ? line[..index] : line;
            });
        return string.Join('\n', lines);
    }

    private static string GetScheduledRetiredActorSpecSourcePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "agents",
                "Aevatar.GAgents.Scheduled",
                "ScheduledRetiredActorSpec.cs");
            if (File.Exists(candidate))
                return candidate;

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate ScheduledRetiredActorSpec.cs from {AppContext.BaseDirectory}");
    }

    private static string GetRetiredActorCleanupHostedServiceSourcePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "src",
                "Aevatar.Foundation.Runtime.Hosting",
                "Maintenance",
                "RetiredActorCleanupHostedService.cs");
            if (File.Exists(candidate))
                return candidate;

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate RetiredActorCleanupHostedService.cs from {AppContext.BaseDirectory}");
    }

    private sealed class StubActorKindProbe(IReadOnlyDictionary<string, string?> kinds) : IActorKindProbe
    {
        public Task<string?> GetRuntimeAgentKindAsync(string actorId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(kinds.TryGetValue(actorId, out var typeName) ? typeName : null);
        }
    }

    private sealed class RecordingKindProbe(
        List<string> probedActorIds,
        IReadOnlyDictionary<string, string?> kinds) : IActorKindProbe
    {
        public Task<string?> GetRuntimeAgentKindAsync(string actorId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            probedActorIds.Add(actorId);
            return Task.FromResult(kinds.TryGetValue(actorId, out var typeName) ? typeName : null);
        }
    }

    private sealed class SequencedKindProbe(IReadOnlyDictionary<string, Queue<string?>> kinds) : IActorKindProbe
    {
        public Task<string?> GetRuntimeAgentKindAsync(string actorId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(
                kinds.TryGetValue(actorId, out var queue) && queue.Count > 0
                    ? queue.Dequeue()
                    : null);
        }
    }

    // Exercises the generic retired-actor cleanup mechanism (actor-body target + a
    // projection-scope target with an incoming source-stream relay) using NON-LIVE kind
    // tokens. Production specs must never retire a live materialization scope kind — that
    // invariant is guarded by
    // ProjectionRuntimeRegistrationTests.RetiredProjectionScopeTokens_ShouldNotRetireLiveMaterializationScopeKinds —
    // so the destroy/relay/reset mechanism is covered here independently of any real module spec.
    private sealed class SyntheticRetiredActorSpec : RetiredActorSpec
    {
        public const string ActorBodyId = "tests-retired-store";
        public const string ActorBodyKind = "tests.retired-actor";
        public const string ProjectionScopeId =
            "projection.durable.scope:tests-retired:tests-retired-store";
        public const string ProjectionScopeKind =
            "projection.materialization-scope.tests-retired-materialization-context";

        public override string SpecId => "tests-synthetic";

        public override IReadOnlyList<RetiredActorTarget> Targets { get; } =
        [
            new(ActorBodyId, [ActorBodyKind], CleanupReadModels: true),
            new(ProjectionScopeId, [ProjectionScopeKind], SourceStreamId: ActorBodyId),
        ];
    }

    private sealed class BlockingDiscoveryRetiredActorSpec(string actorId) : RetiredActorSpec
    {
        private readonly TaskCompletionSource _discoveryEntered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseDiscovery = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource DiscoveryEntered => _discoveryEntered;

        public override string SpecId => "blocking-discovery";

        public override IReadOnlyList<RetiredActorTarget> Targets => [];

        public void ReleaseDiscovery() => _releaseDiscovery.SetResult();

        public override async IAsyncEnumerable<RetiredActorTarget> DiscoverDynamicTargetsAsync(
            IServiceProvider services,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            _discoveryEntered.SetResult();
            await _releaseDiscovery.Task.WaitAsync(ct);
            yield return new RetiredActorTarget(
                actorId,
                ["tests.retired-blocked-actor"]);
        }
    }

    private sealed class RecordingActorRuntime : IActorRuntime
    {
        public List<string> DestroyedActorIds { get; } = [];

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent =>
            throw new NotSupportedException();

        public Task<IActor> CreateAsync(System.Type agentType, string? id = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DestroyAsync(string id, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            DestroyedActorIds.Add(id);
            return Task.CompletedTask;
        }

        public Task<IActor?> GetAsync(string id) => Task.FromResult<IActor?>(null);

        public Task<bool> ExistsAsync(string id) => Task.FromResult(false);

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task UnlinkAsync(string childId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private class RecordingStreamProvider : IStreamProvider
    {
        public List<(string Source, string Target)> RemovedRelays { get; } = [];

        private readonly Dictionary<string, List<StreamForwardingBinding>> _relaysBySource = new(StringComparer.Ordinal);

        public void SeedRelay(string sourceStreamId, string targetStreamId)
        {
            if (!_relaysBySource.TryGetValue(sourceStreamId, out var relays))
            {
                relays = [];
                _relaysBySource[sourceStreamId] = relays;
            }

            relays.Add(new StreamForwardingBinding
            {
                SourceStreamId = sourceStreamId,
                TargetStreamId = targetStreamId,
            });
        }

        public virtual IStream GetStream(string actorId) => new RecordingStream(actorId, RemovedRelays, _relaysBySource);
    }

    private sealed class ThrowingRelayStreamProvider(string throwingStreamId) : RecordingStreamProvider
    {
        public override IStream GetStream(string actorId)
        {
            if (string.Equals(actorId, throwingStreamId, StringComparison.Ordinal))
                return new ThrowingStream(actorId);
            return base.GetStream(actorId);
        }
    }

    private sealed class ThrowingStream(string streamId) : IStream
    {
        public string StreamId => streamId;

        public Task ProduceAsync<T>(T message, CancellationToken ct = default)
            where T : IMessage =>
            throw new NotSupportedException();

        public Task<IAsyncDisposable> SubscribeAsync<T>(
            Func<T, Task> handler,
            CancellationToken ct = default)
            where T : IMessage, new() =>
            throw new NotSupportedException();

        public Task UpsertRelayAsync(StreamForwardingBinding binding, CancellationToken ct = default) =>
            throw new InvalidOperationException("stream topology unavailable");

        public Task RemoveRelayAsync(string targetStreamId, CancellationToken ct = default) =>
            throw new InvalidOperationException("stream topology unavailable");

        public Task<IReadOnlyList<StreamForwardingBinding>> ListRelaysAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("stream topology unavailable");
    }

    private sealed class RecordingStream(
        string streamId,
        List<(string Source, string Target)> removedRelays,
        Dictionary<string, List<StreamForwardingBinding>> relaysBySource) : IStream
    {
        public string StreamId => streamId;

        public Task ProduceAsync<T>(T message, CancellationToken ct = default)
            where T : IMessage =>
            throw new NotSupportedException();

        public Task<IAsyncDisposable> SubscribeAsync<T>(
            Func<T, Task> handler,
            CancellationToken ct = default)
            where T : IMessage, new() =>
            throw new NotSupportedException();

        public Task UpsertRelayAsync(StreamForwardingBinding binding, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task RemoveRelayAsync(string targetStreamId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            removedRelays.Add((streamId, targetStreamId));
            if (relaysBySource.TryGetValue(streamId, out var relays))
                relays.RemoveAll(relay => string.Equals(relay.TargetStreamId, targetStreamId, StringComparison.Ordinal));

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<StreamForwardingBinding>> ListRelaysAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!relaysBySource.TryGetValue(streamId, out var relays))
                return Task.FromResult<IReadOnlyList<StreamForwardingBinding>>([]);

            return Task.FromResult<IReadOnlyList<StreamForwardingBinding>>(
                relays.Select(static relay => new StreamForwardingBinding
                {
                    SourceStreamId = relay.SourceStreamId,
                    TargetStreamId = relay.TargetStreamId,
                    ForwardingMode = relay.ForwardingMode,
                    DirectionFilter = new HashSet<TopologyAudience>(relay.DirectionFilter),
                    EventTypeFilter = new HashSet<string>(relay.EventTypeFilter, StringComparer.Ordinal),
                    Version = relay.Version,
                    LeaseId = relay.LeaseId,
                }).ToArray());
        }
    }

    private sealed class RecordingProjectionStore<TReadModel> :
        IProjectionDocumentReader<TReadModel, string>,
        IProjectionWriteDispatcher<TReadModel>
        where TReadModel : class, IProjectionReadModel
    {
        private readonly List<TReadModel> _documents;

        public RecordingProjectionStore(params TReadModel[] documents)
        {
            _documents = documents.ToList();
        }

        public List<string> DeletedIds { get; } = [];

        public IReadOnlyList<string> RemainingIds => _documents.Select(static document => document.Id).ToArray();

        public Task<TReadModel?> GetAsync(string key, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(_documents.FirstOrDefault(document => document.Id == key));
        }

        public Task<ProjectionDocumentQueryResult<TReadModel>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var actorId = query.Filters
                .Where(static filter => filter.FieldPath == nameof(IProjectionReadModel.ActorId))
                .Select(static filter => filter.Value.RawValue as string)
                .FirstOrDefault();
            var items = _documents
                .Where(document => string.Equals(document.ActorId, actorId, StringComparison.Ordinal))
                .Take(query.Take)
                .ToArray();
            return Task.FromResult(new ProjectionDocumentQueryResult<TReadModel>
            {
                Items = items,
            });
        }

        public Task<ProjectionWriteResult> UpsertAsync(TReadModel readModel, CancellationToken ct = default) =>
            Task.FromResult(ProjectionWriteResult.Applied());

        public Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            DeletedIds.Add(id);
            _documents.RemoveAll(document => string.Equals(document.Id, id, StringComparison.Ordinal));
            return Task.FromResult(ProjectionWriteResult.Applied());
        }
    }

    private sealed class ThrowingProjectionReader<TReadModel> : IProjectionDocumentReader<TReadModel, string>
        where TReadModel : class, IProjectionReadModel
    {
        public Task<TReadModel?> GetAsync(string key, CancellationToken ct = default) =>
            throw new InvalidOperationException("projection store unavailable");

        public Task<ProjectionDocumentQueryResult<TReadModel>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("projection store unavailable");
    }

    private sealed class NoopProjectionWriter<TReadModel> : IProjectionWriteDispatcher<TReadModel>
        where TReadModel : class, IProjectionReadModel
    {
        public Task<ProjectionWriteResult> UpsertAsync(TReadModel readModel, CancellationToken ct = default) =>
            Task.FromResult(ProjectionWriteResult.Applied());

        public Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default) =>
            Task.FromResult(ProjectionWriteResult.Applied());
    }

    private sealed class RecordingStreamPubSubMaintenance : IStreamPubSubMaintenance
    {
        public List<string> ResetActorIds { get; } = [];

        public Task<bool> ResetActorStreamPubSubAsync(string actorId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ResetActorIds.Add(actorId);
            return Task.FromResult(true);
        }
    }

    private sealed class ThrowingStreamPubSubMaintenance : IStreamPubSubMaintenance
    {
        public Task<bool> ResetActorStreamPubSubAsync(string actorId, CancellationToken ct = default) =>
            throw new InvalidOperationException("pub/sub state reset failed");
    }
}
