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

namespace Aevatar.Hosting.Tests;

public sealed class RetiredActorCleanupHostedServiceTests
{
    [Fact]
    public async Task StartAsync_ShouldDestroyRetiredActors_RemoveRelays_AndResetEventStreams()
    {
        var eventStore = new InMemoryEventStore();
        await AppendSingleEventAsync(eventStore, "channel-bot-registration-store");
        await AppendSingleEventAsync(
            eventStore,
            "projection.durable.scope:channel-bot-registration:channel-bot-registration-store");
        var typeProbe = new StubActorTypeProbe(new Dictionary<string, string?>
        {
            ["channel-bot-registration-store"] =
                "Aevatar.GAgents.ChannelRuntime.ChannelBotRegistrationGAgent, Aevatar.GAgents.ChannelRuntime",
            ["projection.durable.scope:channel-bot-registration:channel-bot-registration-store"] =
                "Aevatar.CQRS.Projection.Core.Orchestration.ProjectionMaterializationScopeGAgent`1[[Aevatar.GAgents.ChannelRuntime.ChannelBotRegistrationMaterializationContext, Aevatar.GAgents.ChannelRuntime]], Aevatar.CQRS.Projection.Core",
        });
        var runtime = new RecordingActorRuntime();
        var streamProvider = new RecordingStreamProvider();
        streamProvider.SeedRelay("channel-bot-registration-store", "stale-child-stream");
        var service = CreateService(typeProbe, runtime, streamProvider, eventStore, CreateChannelRuntimeSpec());

        await service.StartAsync(CancellationToken.None);

        runtime.DestroyedActorIds.Should().Contain("channel-bot-registration-store");
        runtime.DestroyedActorIds.Should().Contain(
            "projection.durable.scope:channel-bot-registration:channel-bot-registration-store");
        streamProvider.RemovedRelays.Should().Contain((
            "channel-bot-registration-store",
            "projection.durable.scope:channel-bot-registration:channel-bot-registration-store"));
        streamProvider.RemovedRelays.Should().Contain(("channel-bot-registration-store", "stale-child-stream"));
        (await eventStore.GetVersionAsync("channel-bot-registration-store")).Should().Be(0);
        (await eventStore.GetVersionAsync(
            "projection.durable.scope:channel-bot-registration:channel-bot-registration-store")).Should().Be(0);
    }

    [Fact]
    public async Task StartAsync_ShouldNotDestroyActor_WhenRuntimeTypeIsCurrent()
    {
        var eventStore = new InMemoryEventStore();
        await AppendSingleEventAsync(eventStore, "channel-bot-registration-store");
        var typeProbe = new StubActorTypeProbe(new Dictionary<string, string?>
        {
            ["channel-bot-registration-store"] =
                "Aevatar.GAgents.Channel.Runtime.ChannelBotRegistrationGAgent, Aevatar.GAgents.Channel.Runtime",
        });
        var runtime = new RecordingActorRuntime();
        var service = CreateService(
            typeProbe, runtime, new RecordingStreamProvider(), eventStore, CreateChannelRuntimeSpec());

        await service.StartAsync(CancellationToken.None);

        runtime.DestroyedActorIds.Should().BeEmpty();
        (await eventStore.GetVersionAsync("channel-bot-registration-store")).Should().Be(1);
    }

    [Fact]
    public async Task StartAsync_ShouldNotDestroyActor_WhenRetiredTypeTokenIsOnlySubstring()
    {
        var eventStore = new InMemoryEventStore();
        await AppendSingleEventAsync(eventStore, "channel-bot-registration-store");
        var typeProbe = new StubActorTypeProbe(new Dictionary<string, string?>
        {
            ["channel-bot-registration-store"] =
                "Aevatar.GAgents.ChannelRuntime.ChannelBotRegistrationGAgentProxy, Aevatar.GAgents.ChannelRuntime",
        });
        var runtime = new RecordingActorRuntime();
        var service = CreateService(
            typeProbe, runtime, new RecordingStreamProvider(), eventStore, CreateChannelRuntimeSpec());

        await service.StartAsync(CancellationToken.None);

        runtime.DestroyedActorIds.Should().BeEmpty();
        (await eventStore.GetVersionAsync("channel-bot-registration-store")).Should().Be(1);
    }

    [Fact]
    public async Task StartAsync_ShouldResetStream_WhenActorStateWasAlreadyDestroyed()
    {
        var eventStore = new InMemoryEventStore();
        await AppendSingleEventAsync(eventStore, "channel-bot-registration-store");
        var typeProbe = new StubActorTypeProbe(new Dictionary<string, string?>());
        var runtime = new RecordingActorRuntime();
        var service = CreateService(
            typeProbe, runtime, new RecordingStreamProvider(), eventStore, CreateChannelRuntimeSpec());

        await service.StartAsync(CancellationToken.None);

        runtime.DestroyedActorIds.Should().Contain("channel-bot-registration-store");
        (await eventStore.GetVersionAsync("channel-bot-registration-store")).Should().Be(0);
    }

    [Fact]
    public async Task StartAsync_ShouldCleanRetiredUserAgentsDiscoveredFromCatalogBeforeCatalogReset()
    {
        var eventStore = new InMemoryEventStore();
        await AppendCatalogEventsAsync(eventStore,
        [
            new UserAgentCatalogEntry
            {
                AgentId = "skill-runner-old",
                AgentType = SkillDefinitionDefaults.AgentType,
            },
            new UserAgentCatalogEntry
            {
                AgentId = "workflow-agent-old",
                AgentType = WorkflowAgentDefaults.AgentType,
            },
            new UserAgentCatalogEntry
            {
                AgentId = "skill-runner-current",
                AgentType = SkillDefinitionDefaults.AgentType,
            },
            new UserAgentCatalogEntry
            {
                AgentId = "skill-runner-proxy",
                AgentType = SkillDefinitionDefaults.AgentType,
            },
        ]);
        await AppendSingleEventAsync(eventStore, "skill-runner-old");
        await AppendSingleEventAsync(eventStore, "workflow-agent-old");
        await AppendSingleEventAsync(eventStore, "skill-runner-current");
        await AppendSingleEventAsync(eventStore, "skill-runner-proxy");

        var typeProbe = new StubActorTypeProbe(new Dictionary<string, string?>
        {
            ["agent-registry-store"] =
                "Aevatar.GAgents.ChannelRuntime.UserAgentCatalogGAgent, Aevatar.GAgents.ChannelRuntime",
            ["skill-runner-old"] =
                "Aevatar.GAgents.ChannelRuntime.SkillRunnerGAgent, Aevatar.GAgents.ChannelRuntime",
            ["workflow-agent-old"] =
                "Aevatar.GAgents.ChannelRuntime.WorkflowAgentGAgent, Aevatar.GAgents.ChannelRuntime",
            ["skill-runner-current"] =
                "Aevatar.GAgents.Scheduled.SkillRunnerGAgent, Aevatar.GAgents.Scheduled",
            ["skill-runner-proxy"] =
                "Aevatar.GAgents.ChannelRuntime.SkillRunnerGAgentProxy, Aevatar.GAgents.ChannelRuntime",
        });
        var runtime = new RecordingActorRuntime();
        var service = CreateService(
            typeProbe, runtime, new RecordingStreamProvider(), eventStore, CreateScheduledSpec());

        await service.StartAsync(CancellationToken.None);

        runtime.DestroyedActorIds.Should().Contain("skill-runner-old");
        runtime.DestroyedActorIds.Should().Contain("workflow-agent-old");
        runtime.DestroyedActorIds.Should().Contain("agent-registry-store");
        runtime.DestroyedActorIds.Should().NotContain("skill-runner-current");
        runtime.DestroyedActorIds.Should().NotContain("skill-runner-proxy");
        (await eventStore.GetVersionAsync("skill-runner-old")).Should().Be(0);
        (await eventStore.GetVersionAsync("workflow-agent-old")).Should().Be(0);
        (await eventStore.GetVersionAsync("skill-runner-current")).Should().Be(1);
        (await eventStore.GetVersionAsync("skill-runner-proxy")).Should().Be(1);
        (await eventStore.GetVersionAsync("agent-registry-store")).Should().Be(0);
    }

    [Fact]
    public async Task StartAsync_ShouldSkipCatalogWalk_WhenCatalogRuntimeTypeIsAlreadyCurrent()
    {
        // Once the catalog actor is on the new namespace, the cleanup must not
        // replay agent-registry-store on every startup nor probe per-entry actors —
        // otherwise warm clusters pay an unbounded scan cost forever.
        var eventStore = new InMemoryEventStore();
        await AppendCatalogEventsAsync(eventStore,
        [
            new UserAgentCatalogEntry
            {
                AgentId = "skill-runner-already-migrated",
                AgentType = SkillDefinitionDefaults.AgentType,
            },
        ]);
        await AppendSingleEventAsync(eventStore, "skill-runner-already-migrated");

        var probedActorIds = new List<string>();
        var typeProbe = new RecordingTypeProbe(probedActorIds, new Dictionary<string, string?>
        {
            ["agent-registry-store"] =
                "Aevatar.GAgents.Scheduled.UserAgentCatalogGAgent, Aevatar.GAgents.Scheduled",
        });
        var runtime = new RecordingActorRuntime();
        var service = CreateService(
            typeProbe, runtime, new RecordingStreamProvider(), eventStore, CreateScheduledSpec());

        await service.StartAsync(CancellationToken.None);

        runtime.DestroyedActorIds.Should().BeEmpty();
        probedActorIds.Should().NotContain("skill-runner-already-migrated");
        (await eventStore.GetVersionAsync("skill-runner-already-migrated")).Should().Be(1);
    }

    [Fact]
    public async Task StartAsync_ShouldDiscoverRetiredUserAgentsFromReadModel_WhenCatalogStreamHasBeenCompacted()
    {
        // Snapshot+compaction can drop the original UserAgentCatalogUpsertedEvent
        // entries from agent-registry-store. The discovery must still find the
        // generated actor ids via the projection read model so they are cleaned
        // before the catalog itself is destroyed.
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
                AgentType = SkillDefinitionDefaults.AgentType,
            },
            new UserAgentCatalogDocument
            {
                Id = "workflow-agent-snapshotted",
                ActorId = "agent-registry-store",
                AgentType = WorkflowAgentDefaults.AgentType,
            });
        var typeProbe = new StubActorTypeProbe(new Dictionary<string, string?>
        {
            ["agent-registry-store"] =
                "Aevatar.GAgents.ChannelRuntime.UserAgentCatalogGAgent, Aevatar.GAgents.ChannelRuntime",
            ["skill-runner-snapshotted"] =
                "Aevatar.GAgents.ChannelRuntime.SkillRunnerGAgent, Aevatar.GAgents.ChannelRuntime",
            ["workflow-agent-snapshotted"] =
                "Aevatar.GAgents.ChannelRuntime.WorkflowAgentGAgent, Aevatar.GAgents.ChannelRuntime",
        });
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddSingleton<Aevatar.Foundation.Abstractions.Persistence.IEventStore>(eventStore);
        serviceCollection.AddSingleton<IActorTypeProbe>(typeProbe);
        serviceCollection.AddSingleton<IProjectionDocumentReader<UserAgentCatalogDocument, string>>(documents);
        serviceCollection.AddSingleton<IProjectionWriteDispatcher<UserAgentCatalogDocument>>(documents);

        var runtime = new RecordingActorRuntime();
        var service = CreateService(
            typeProbe,
            runtime,
            new RecordingStreamProvider(),
            eventStore,
            CreateScheduledSpec(),
            serviceCollection.BuildServiceProvider());

        await service.StartAsync(CancellationToken.None);

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
        var projectionScopeActorId =
            "projection.durable.scope:channel-bot-registration:channel-bot-registration-store";
        await AppendSingleEventAsync(eventStore, projectionScopeActorId);
        var typeProbe = new StubActorTypeProbe(new Dictionary<string, string?>
        {
            [projectionScopeActorId] =
                "Aevatar.CQRS.Projection.Core.Orchestration.ProjectionMaterializationScopeGAgent`1[[Aevatar.GAgents.ChannelRuntime.ChannelBotRegistrationMaterializationContext, Aevatar.GAgents.ChannelRuntime]], Aevatar.CQRS.Projection.Core",
        });
        var runtime = new RecordingActorRuntime();
        var streamProvider = new ThrowingRelayStreamProvider("channel-bot-registration-store");
        var service = CreateService(
            typeProbe, runtime, streamProvider, eventStore, CreateChannelRuntimeSpec());

        await service.StartAsync(CancellationToken.None);

        runtime.DestroyedActorIds.Should().Contain(projectionScopeActorId);
        (await eventStore.GetVersionAsync(projectionScopeActorId)).Should().Be(0);
    }

    [Fact]
    public async Task StartAsync_ShouldFallBackToCatalogEvents_WhenReadModelDiscoveryThrows()
    {
        // Projection store unavailable (transient error) must NOT abort startup
        // cleanup. The read-model path is best-effort; the event-stream walk and
        // static targets must still run.
        var eventStore = new InMemoryEventStore();
        await AppendCatalogEventsAsync(eventStore,
        [
            new UserAgentCatalogEntry
            {
                AgentId = "skill-runner-recent",
                AgentType = SkillDefinitionDefaults.AgentType,
            },
        ]);
        await AppendSingleEventAsync(eventStore, "skill-runner-recent");
        var typeProbe = new StubActorTypeProbe(new Dictionary<string, string?>
        {
            ["agent-registry-store"] =
                "Aevatar.GAgents.ChannelRuntime.UserAgentCatalogGAgent, Aevatar.GAgents.ChannelRuntime",
            ["skill-runner-recent"] =
                "Aevatar.GAgents.ChannelRuntime.SkillRunnerGAgent, Aevatar.GAgents.ChannelRuntime",
        });
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddSingleton<Aevatar.Foundation.Abstractions.Persistence.IEventStore>(eventStore);
        serviceCollection.AddSingleton<IActorTypeProbe>(typeProbe);
        serviceCollection.AddSingleton<IProjectionDocumentReader<UserAgentCatalogDocument, string>>(
            new ThrowingProjectionReader<UserAgentCatalogDocument>());
        serviceCollection.AddSingleton<IProjectionWriteDispatcher<UserAgentCatalogDocument>>(
            new NoopProjectionWriter<UserAgentCatalogDocument>());

        var runtime = new RecordingActorRuntime();
        var service = CreateService(
            typeProbe,
            runtime,
            new RecordingStreamProvider(),
            eventStore,
            CreateScheduledSpec(),
            serviceCollection.BuildServiceProvider());

        await service.StartAsync(CancellationToken.None);

        runtime.DestroyedActorIds.Should().Contain("skill-runner-recent");
        runtime.DestroyedActorIds.Should().Contain("agent-registry-store");
        (await eventStore.GetVersionAsync("skill-runner-recent")).Should().Be(0);
        (await eventStore.GetVersionAsync("agent-registry-store")).Should().Be(0);
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
        var typeProbe = new StubActorTypeProbe(new Dictionary<string, string?>
        {
            ["agent-registry-store"] =
                "Aevatar.GAgents.ChannelRuntime.UserAgentCatalogGAgent, Aevatar.GAgents.ChannelRuntime",
        });
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddSingleton<Aevatar.Foundation.Abstractions.Persistence.IEventStore>(eventStore);
        serviceCollection.AddSingleton<IActorTypeProbe>(typeProbe);
        serviceCollection.AddSingleton<IProjectionDocumentReader<UserAgentCatalogDocument, string>>(documents);
        serviceCollection.AddSingleton<IProjectionWriteDispatcher<UserAgentCatalogDocument>>(documents);
        var runtime = new RecordingActorRuntime();
        var service = CreateService(
            typeProbe,
            runtime,
            new RecordingStreamProvider(),
            eventStore,
            CreateScheduledSpec(),
            serviceCollection.BuildServiceProvider());

        await service.StartAsync(CancellationToken.None);

        documents.DeletedIds.Should().Equal("agent-doc-delete");
        documents.RemainingIds.Should().Equal("agent-doc-keep");
    }

    [Fact]
    public async Task StartAsync_ShouldContinue_WhenReadModelCleanupThrows()
    {
        var eventStore = new InMemoryEventStore();
        await AppendSingleEventAsync(eventStore, "channel-bot-registration-store");
        var typeProbe = new StubActorTypeProbe(new Dictionary<string, string?>
        {
            ["channel-bot-registration-store"] =
                "Aevatar.GAgents.ChannelRuntime.ChannelBotRegistrationGAgent, Aevatar.GAgents.ChannelRuntime",
        });
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddSingleton<Aevatar.Foundation.Abstractions.Persistence.IEventStore>(eventStore);
        serviceCollection.AddSingleton<IActorTypeProbe>(typeProbe);
        serviceCollection.AddSingleton<IProjectionDocumentReader<ChannelBotRegistrationDocument, string>>(
            new ThrowingProjectionReader<ChannelBotRegistrationDocument>());
        serviceCollection.AddSingleton<IProjectionWriteDispatcher<ChannelBotRegistrationDocument>>(
            new NoopProjectionWriter<ChannelBotRegistrationDocument>());
        var runtime = new RecordingActorRuntime();
        var service = CreateService(
            typeProbe,
            runtime,
            new RecordingStreamProvider(),
            eventStore,
            CreateChannelRuntimeSpec(),
            serviceCollection.BuildServiceProvider());

        await service.StartAsync(CancellationToken.None);

        runtime.DestroyedActorIds.Should().Contain("channel-bot-registration-store");
        (await eventStore.GetVersionAsync("channel-bot-registration-store")).Should().Be(0);
    }

    [Fact]
    public async Task StartAsync_ShouldDestroyMidMigrationProjectionScope_AtNewScopeKey()
    {
        // Mid-migration deploys may have created the durable projection scope
        // actor at the *new* scope key (UserAgentCatalog: user-agent-catalog-read-model)
        // while still bound to the old ChannelRuntime materialization context.
        // The retired-cleanup spec must target both the old and new scope keys
        // so a single deploy auto-recovers without manual redis surgery.
        var newScopeKeyActorId =
            "projection.durable.scope:user-agent-catalog-read-model:agent-registry-store";
        var eventStore = new InMemoryEventStore();
        await AppendSingleEventAsync(eventStore, newScopeKeyActorId);
        var typeProbe = new StubActorTypeProbe(new Dictionary<string, string?>
        {
            [newScopeKeyActorId] =
                "Aevatar.CQRS.Projection.Core.Orchestration.ProjectionMaterializationScopeGAgent`1[[Aevatar.GAgents.ChannelRuntime.UserAgentCatalogMaterializationContext, Aevatar.GAgents.ChannelRuntime]], Aevatar.CQRS.Projection.Core",
        });
        var runtime = new RecordingActorRuntime();
        var service = CreateService(
            typeProbe, runtime, new RecordingStreamProvider(), eventStore, CreateScheduledSpec());

        await service.StartAsync(CancellationToken.None);

        runtime.DestroyedActorIds.Should().Contain(newScopeKeyActorId);
        (await eventStore.GetVersionAsync(newScopeKeyActorId)).Should().Be(0);
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
        await AppendSingleEventAsync(eventStore, "channel-bot-registration-store");
        await AppendSingleEventAsync(
            eventStore,
            "projection.durable.scope:channel-bot-registration:channel-bot-registration-store");
        var typeProbe = new StubActorTypeProbe(new Dictionary<string, string?>
        {
            ["channel-bot-registration-store"] =
                "Aevatar.GAgents.ChannelRuntime.ChannelBotRegistrationGAgent, Aevatar.GAgents.ChannelRuntime",
            ["projection.durable.scope:channel-bot-registration:channel-bot-registration-store"] =
                "Aevatar.CQRS.Projection.Core.Orchestration.ProjectionMaterializationScopeGAgent`1[[Aevatar.GAgents.ChannelRuntime.ChannelBotRegistrationMaterializationContext, Aevatar.GAgents.ChannelRuntime]], Aevatar.CQRS.Projection.Core",
        });
        var runtime = new RecordingActorRuntime();
        var pubSub = new RecordingStreamPubSubMaintenance();
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddSingleton<Aevatar.Foundation.Abstractions.Persistence.IEventStore>(eventStore);
        serviceCollection.AddSingleton<IActorTypeProbe>(typeProbe);
        serviceCollection.AddSingleton<IStreamPubSubMaintenance>(pubSub);
        var service = CreateService(
            typeProbe,
            runtime,
            new RecordingStreamProvider(),
            eventStore,
            CreateChannelRuntimeSpec(),
            serviceCollection.BuildServiceProvider());

        await service.StartAsync(CancellationToken.None);

        pubSub.ResetActorIds.Should().Contain("channel-bot-registration-store");
        pubSub.ResetActorIds.Should().Contain(
            "projection.durable.scope:channel-bot-registration:channel-bot-registration-store");
    }

    [Fact]
    public async Task StartAsync_ShouldContinue_WhenStreamPubSubResetThrows()
    {
        var eventStore = new InMemoryEventStore();
        await AppendSingleEventAsync(eventStore, "channel-bot-registration-store");
        var typeProbe = new StubActorTypeProbe(new Dictionary<string, string?>
        {
            ["channel-bot-registration-store"] =
                "Aevatar.GAgents.ChannelRuntime.ChannelBotRegistrationGAgent, Aevatar.GAgents.ChannelRuntime",
        });
        var runtime = new RecordingActorRuntime();
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddSingleton<Aevatar.Foundation.Abstractions.Persistence.IEventStore>(eventStore);
        serviceCollection.AddSingleton<IActorTypeProbe>(typeProbe);
        serviceCollection.AddSingleton<IStreamPubSubMaintenance>(new ThrowingStreamPubSubMaintenance());
        var service = CreateService(
            typeProbe,
            runtime,
            new RecordingStreamProvider(),
            eventStore,
            CreateChannelRuntimeSpec(),
            serviceCollection.BuildServiceProvider());

        await service.StartAsync(CancellationToken.None);

        runtime.DestroyedActorIds.Should().Contain("channel-bot-registration-store");
        (await eventStore.GetVersionAsync("channel-bot-registration-store")).Should().Be(0);
    }

    [Fact]
    public async Task StartAsync_ShouldRunEachRegisteredSpec()
    {
        var eventStore = new InMemoryEventStore();
        await AppendSingleEventAsync(eventStore, "device-registration-store");
        await AppendSingleEventAsync(eventStore, "channel-bot-registration-store");
        var typeProbe = new StubActorTypeProbe(new Dictionary<string, string?>
        {
            ["device-registration-store"] =
                "Aevatar.GAgents.ChannelRuntime.DeviceRegistrationGAgent, Aevatar.GAgents.ChannelRuntime",
            ["channel-bot-registration-store"] =
                "Aevatar.GAgents.ChannelRuntime.ChannelBotRegistrationGAgent, Aevatar.GAgents.ChannelRuntime",
        });
        var runtime = new RecordingActorRuntime();
        var service = CreateService(
            typeProbe,
            runtime,
            new RecordingStreamProvider(),
            eventStore,
            specs: [CreateDeviceSpec(), CreateChannelRuntimeSpec()]);

        await service.StartAsync(CancellationToken.None);

        runtime.DestroyedActorIds.Should().Contain("device-registration-store");
        runtime.DestroyedActorIds.Should().Contain("channel-bot-registration-store");
        (await eventStore.GetVersionAsync("device-registration-store")).Should().Be(0);
        (await eventStore.GetVersionAsync("channel-bot-registration-store")).Should().Be(0);
    }

    private static IRetiredActorSpec CreateChannelRuntimeSpec() => new ChannelRuntimeRetiredActorSpec();

    private static IRetiredActorSpec CreateDeviceSpec() => new DeviceRetiredActorSpec();

    private static IRetiredActorSpec CreateScheduledSpec() => new ScheduledRetiredActorSpec();

    private static RetiredActorCleanupHostedService CreateService(
        IActorTypeProbe typeProbe,
        RecordingActorRuntime runtime,
        RecordingStreamProvider streamProvider,
        InMemoryEventStore eventStore,
        IRetiredActorSpec spec,
        IServiceProvider? services = null) =>
        CreateService(typeProbe, runtime, streamProvider, eventStore, [spec], services);

    private static RetiredActorCleanupHostedService CreateService(
        IActorTypeProbe typeProbe,
        RecordingActorRuntime runtime,
        RecordingStreamProvider streamProvider,
        InMemoryEventStore eventStore,
        IReadOnlyList<IRetiredActorSpec> specs,
        IServiceProvider? services = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Aevatar:RetiredActorCleanup:WaitPollMilliseconds"] = "1",
                ["Aevatar:RetiredActorCleanup:InProgressTimeoutSeconds"] = "1",
            })
            .Build();

        var resolvedServices = services ?? BuildSpecServices(eventStore, typeProbe);

        return new RetiredActorCleanupHostedService(
            specs,
            typeProbe,
            runtime,
            streamProvider,
            eventStore,
            eventStore,
            resolvedServices,
            configuration,
            NullLogger<RetiredActorCleanupHostedService>.Instance);
    }

    private static IServiceProvider BuildSpecServices(
        InMemoryEventStore eventStore, IActorTypeProbe typeProbe)
    {
        var services = new ServiceCollection();
        services.AddSingleton<Aevatar.Foundation.Abstractions.Persistence.IEventStore>(eventStore);
        services.AddSingleton(typeProbe);
        return services.BuildServiceProvider();
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

    private sealed class StubActorTypeProbe(IReadOnlyDictionary<string, string?> typeNames) : IActorTypeProbe
    {
        public Task<string?> GetRuntimeAgentTypeNameAsync(string actorId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(typeNames.TryGetValue(actorId, out var typeName) ? typeName : null);
        }
    }

    private sealed class RecordingTypeProbe(
        List<string> probedActorIds,
        IReadOnlyDictionary<string, string?> typeNames) : IActorTypeProbe
    {
        public Task<string?> GetRuntimeAgentTypeNameAsync(string actorId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            probedActorIds.Add(actorId);
            return Task.FromResult(typeNames.TryGetValue(actorId, out var typeName) ? typeName : null);
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
