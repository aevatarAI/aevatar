using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Maintenance;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Maintenance;
using Aevatar.Foundation.Runtime.Hosting.Maintenance;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Foundation.Runtime.Streaming;
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
        var documents = new RecordingProjectionStore<UserAgentCatalogDocument>(
            CatalogDocument("skill-runner-old", SkillRunnerDefaults.AgentType),
            CatalogDocument("workflow-agent-old", "workflow_agent"),
            CatalogDocument("skill-runner-current", SkillRunnerDefaults.AgentType),
            CatalogDocument("skill-runner-proxy", SkillRunnerDefaults.AgentType),
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
            ["skill-runner-prefix-only"] =
                "Aevatar.GAgents.ChannelRuntime.SkillRunnerGAgent, Aevatar.GAgents.ChannelRuntime",
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
    public async Task StartAsync_ShouldSkipCatalogWalk_WhenCatalogRuntimeTypeIsAlreadyCurrent()
    {
        // Once the catalog actor is on the new namespace, the cleanup must not
        // query agent-registry-store on every startup nor probe per-entry actors —
        // otherwise warm clusters pay an unbounded scan cost forever.
        var eventStore = new InMemoryEventStore();
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
                AgentType = SkillRunnerDefaults.AgentType,
            },
            new UserAgentCatalogDocument
            {
                Id = "workflow-agent-snapshotted",
                ActorId = "agent-registry-store",
                AgentType = "workflow_agent",
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
                AgentType = SkillRunnerDefaults.AgentType,
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

    [Fact]
    public async Task StartAsync_ShouldDispatchCoordinatorLeaseCommands_AndAvoidEventStoreMarkers()
    {
        // Refactor (issue1056/impl): Old pattern: hosted-service EventStore marker replay/write. New principle: actor-owned cleanup lease via IActorDispatchPort + EventEnvelope + narrow command-result contract (Phase 9 r6 consensus).
        var eventStore = new InMemoryEventStore();
        await AppendSingleEventAsync(eventStore, "channel-bot-registration-store");
        var typeProbe = new StubActorTypeProbe(new Dictionary<string, string?>
        {
            ["channel-bot-registration-store"] =
                "Aevatar.GAgents.ChannelRuntime.ChannelBotRegistrationGAgent, Aevatar.GAgents.ChannelRuntime",
        });
        var runtime = new RecordingActorRuntime();
        var dispatchPort = new CoordinatedCleanupDispatchPort();
        var service = CreateService(
            typeProbe,
            runtime,
            new RecordingStreamProvider(),
            eventStore,
            CreateChannelRuntimeSpec(),
            dispatchPort: dispatchPort,
            continuationPort: dispatchPort.ContinuationPort);

        await service.StartAsync(CancellationToken.None);

        runtime.CreatedByKind.Should().Contain((
            RetiredActorCleanupCoordinatorGAgent.Kind,
            RetiredActorCleanupCoordinatorGAgent.ActorId));
        dispatchPort.Payloads.Should().Contain(payload => payload is RetiredActorCleanupAcquireLeaseCommand);
        dispatchPort.Payloads.Should().Contain(payload => payload is RetiredActorCleanupCheckLeaseCommand);
        dispatchPort.Payloads.Should().Contain(payload => payload is RetiredActorCleanupReleaseLeaseCommand);
        var retiredMarkerStreamId = string.Join(
            ':',
            "__maintenance",
            "retired-actor-cleanup",
            "channel-runtime-retired-actors");
        (await eventStore.GetVersionAsync(retiredMarkerStreamId))
            .Should().Be(0);
    }

    [Fact]
    public async Task CoordinatorGAgent_ShouldGrantDenyCheckRelease_AndRecordFailureWithContinuations()
    {
        // Refactor (issue1056/impl): Old pattern: hosted-service EventStore marker replay/write. New principle: actor-owned cleanup lease via IActorDispatchPort + EventEnvelope + narrow command-result contract (Phase 9 r6 consensus).
        var publisher = new RecordingCoordinatorContinuationPublisher();
        var agent = CreateCoordinatorAgent(publisher);
        await agent.ActivateAsync(CancellationToken.None);

        await agent.HandleEventAsync(CreateEnvelope(new RetiredActorCleanupAcquireLeaseCommand
        {
            CommandId = "cmd-acquire-a",
            SpecId = "spec-a",
            OwnerToken = "owner-a",
            LeaseTimeoutSeconds = 300,
            RequestedAt = Timestamp.FromDateTime(DateTime.UtcNow),
        }));

        publisher.AcquireContinuations.Single().Result.Status.Should()
            .Be(RetiredActorCleanupAcquireLeaseStatus.Granted);

        await agent.HandleEventAsync(CreateEnvelope(new RetiredActorCleanupAcquireLeaseCommand
        {
            CommandId = "cmd-acquire-b",
            SpecId = "spec-a",
            OwnerToken = "owner-b",
            LeaseTimeoutSeconds = 300,
            RequestedAt = Timestamp.FromDateTime(DateTime.UtcNow),
        }));

        publisher.AcquireContinuations.Last().Result.Status.Should()
            .Be(RetiredActorCleanupAcquireLeaseStatus.Denied);

        await agent.HandleEventAsync(CreateEnvelope(new RetiredActorCleanupCheckLeaseCommand
        {
            CommandId = "cmd-check-a",
            SpecId = "spec-a",
            OwnerToken = "owner-a",
            CheckedAt = Timestamp.FromDateTime(DateTime.UtcNow),
        }));

        publisher.CheckContinuations.Single().Result.Status.Should()
            .Be(RetiredActorCleanupCheckLeaseStatus.StillOwner);

        await agent.HandleEventAsync(CreateEnvelope(new RetiredActorCleanupRecordFailureCommand
        {
            CommandId = "cmd-failure-a",
            SpecId = "spec-a",
            OwnerToken = "owner-a",
            Reason = "cleanup failed",
            OccurredAt = Timestamp.FromDateTime(DateTime.UtcNow),
        }));

        publisher.RecordFailureContinuations.Single().Result.Status.Should()
            .Be(RetiredActorCleanupRecordFailureStatus.Recorded);
        publisher.RecordFailureContinuations.Single().Result.FailureCount.Should().Be(1);

        await agent.HandleEventAsync(CreateEnvelope(new RetiredActorCleanupReleaseLeaseCommand
        {
            CommandId = "cmd-release-a",
            SpecId = "spec-a",
            OwnerToken = "owner-a",
            ReleasedAt = Timestamp.FromDateTime(DateTime.UtcNow),
        }));

        publisher.ReleaseContinuations.Single().Result.Status.Should()
            .Be(RetiredActorCleanupReleaseLeaseStatus.Released);
        agent.State.Leases.Should().BeEmpty();
    }

    [Fact]
    public async Task CoordinatorGAgent_ShouldEmitInvalidAcquireContinuation_WhenCommandIsInvalid()
    {
        var publisher = new RecordingCoordinatorContinuationPublisher();
        var agent = CreateCoordinatorAgent(publisher);
        await agent.ActivateAsync(CancellationToken.None);

        await agent.HandleEventAsync(CreateEnvelope(new RetiredActorCleanupAcquireLeaseCommand
        {
            CommandId = string.Empty,
            SpecId = "spec-a",
            OwnerToken = "owner-a",
            LeaseTimeoutSeconds = 300,
            RequestedAt = Timestamp.FromDateTime(DateTime.UtcNow),
        }));

        var result = publisher.AcquireContinuations.Single().Result;
        result.Status.Should().Be(RetiredActorCleanupAcquireLeaseStatus.Invalid);
        result.Message.Should().Be("command_id is required.");
        agent.State.Leases.Should().BeEmpty();
    }

    [Fact]
    public async Task CoordinatorGAgent_ShouldAllowExpiredLeaseReacquire()
    {
        var publisher = new RecordingCoordinatorContinuationPublisher();
        var agent = CreateCoordinatorAgent(publisher);
        await agent.ActivateAsync(CancellationToken.None);
        var acquiredAt = Timestamp.FromDateTime(DateTime.UtcNow.AddMinutes(-10));

        await agent.HandleEventAsync(CreateEnvelope(new RetiredActorCleanupAcquireLeaseCommand
        {
            CommandId = "cmd-acquire-old",
            SpecId = "spec-a",
            OwnerToken = "owner-a",
            LeaseTimeoutSeconds = 60,
            RequestedAt = acquiredAt,
        }));

        await agent.HandleEventAsync(CreateEnvelope(new RetiredActorCleanupAcquireLeaseCommand
        {
            CommandId = "cmd-acquire-new",
            SpecId = "spec-a",
            OwnerToken = "owner-b",
            LeaseTimeoutSeconds = 60,
            RequestedAt = Timestamp.FromDateTime(DateTime.UtcNow),
        }));

        publisher.AcquireContinuations.Should().HaveCount(2);
        publisher.AcquireContinuations.Last().Result.Status.Should()
            .Be(RetiredActorCleanupAcquireLeaseStatus.Granted);
        agent.State.Leases["spec-a"].OwnerToken.Should().Be("owner-b");
    }

    [Fact]
    public async Task CoordinatorGAgent_ShouldEmitNotOwnerAndIgnoredContinuations()
    {
        var publisher = new RecordingCoordinatorContinuationPublisher();
        var agent = CreateCoordinatorAgent(publisher);
        await agent.ActivateAsync(CancellationToken.None);

        await agent.HandleEventAsync(CreateEnvelope(new RetiredActorCleanupAcquireLeaseCommand
        {
            CommandId = "cmd-acquire-a",
            SpecId = "spec-a",
            OwnerToken = "owner-a",
            LeaseTimeoutSeconds = 300,
            RequestedAt = Timestamp.FromDateTime(DateTime.UtcNow),
        }));

        await agent.HandleEventAsync(CreateEnvelope(new RetiredActorCleanupCheckLeaseCommand
        {
            CommandId = "cmd-check-b",
            SpecId = "spec-a",
            OwnerToken = "owner-b",
            CheckedAt = Timestamp.FromDateTime(DateTime.UtcNow),
        }));

        await agent.HandleEventAsync(CreateEnvelope(new RetiredActorCleanupReleaseLeaseCommand
        {
            CommandId = "cmd-release-b",
            SpecId = "spec-a",
            OwnerToken = "owner-b",
            ReleasedAt = Timestamp.FromDateTime(DateTime.UtcNow),
        }));

        await agent.HandleEventAsync(CreateEnvelope(new RetiredActorCleanupRecordFailureCommand
        {
            CommandId = "cmd-failure-b",
            SpecId = "spec-a",
            OwnerToken = "owner-b",
            Reason = "not owner",
            OccurredAt = Timestamp.FromDateTime(DateTime.UtcNow),
        }));

        publisher.CheckContinuations.Single().Result.Status.Should()
            .Be(RetiredActorCleanupCheckLeaseStatus.NotOwner);
        publisher.ReleaseContinuations.Single().Result.Status.Should()
            .Be(RetiredActorCleanupReleaseLeaseStatus.NotOwner);
        publisher.RecordFailureContinuations.Single().Result.Status.Should()
            .Be(RetiredActorCleanupRecordFailureStatus.Ignored);
        agent.State.Leases["spec-a"].OwnerToken.Should().Be("owner-a");
    }

    [Fact]
    public async Task ContinuationPort_ShouldSubscribeToCoordinatorStream_AndIgnoreOtherPayloads()
    {
        var streams = new InMemoryStreamProvider();
        var port = new RetiredActorCleanupCoordinatorContinuationPort(streams);
        var received = new List<RetiredActorCleanupCoordinatorContinuation>();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var subscription = await port.SubscribeAsync(continuation =>
        {
            received.Add(continuation);
            completed.SetResult();
            return Task.CompletedTask;
        });

        await streams
            .GetStream(RetiredActorCleanupCoordinatorGAgent.ActorId)
            .ProduceAsync(new StringValue { Value = "ignored" });

        await streams
            .GetStream(RetiredActorCleanupCoordinatorGAgent.ActorId)
            .ProduceAsync(new RetiredActorCleanupAcquireLeaseContinuation
            {
                Result = new RetiredActorCleanupAcquireLeaseResult
                {
                    CommandId = "cmd-a",
                    SpecId = "spec-a",
                    OwnerToken = "owner-a",
                    Status = RetiredActorCleanupAcquireLeaseStatus.Granted,
                },
            });

        await completed.Task;
        received.Should().ContainSingle();
        received.Single().AcquireLease.Result.CommandId.Should().Be("cmd-a");
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
        IServiceProvider? services = null,
        IActorDispatchPort? dispatchPort = null,
        IRetiredActorCleanupCoordinatorContinuationPort? continuationPort = null) =>
        CreateService(typeProbe, runtime, streamProvider, eventStore, [spec], services, dispatchPort, continuationPort);

    private static RetiredActorCleanupHostedService CreateService(
        IActorTypeProbe typeProbe,
        RecordingActorRuntime runtime,
        RecordingStreamProvider streamProvider,
        InMemoryEventStore eventStore,
        IReadOnlyList<IRetiredActorSpec> specs,
        IServiceProvider? services = null,
        IActorDispatchPort? dispatchPort = null,
        IRetiredActorCleanupCoordinatorContinuationPort? continuationPort = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Aevatar:RetiredActorCleanup:WaitPollMilliseconds"] = "1",
                ["Aevatar:RetiredActorCleanup:InProgressTimeoutSeconds"] = "1",
            })
            .Build();

        var resolvedServices = services ?? BuildSpecServices(eventStore, typeProbe);
        var resolvedDispatchPort = dispatchPort ?? new CoordinatedCleanupDispatchPort();
        var resolvedContinuationPort = continuationPort ??
                                       (resolvedDispatchPort as CoordinatedCleanupDispatchPort)?.ContinuationPort ??
                                       new RecordingCleanupContinuationPort();

        return new RetiredActorCleanupHostedService(
            specs,
            typeProbe,
            runtime,
            resolvedDispatchPort,
            resolvedContinuationPort,
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
        public List<(string Kind, string Id)> CreatedByKind { get; } = [];

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent =>
            throw new NotSupportedException();

        public Task<IActor> CreateAsync(System.Type agentType, string? id = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IActor> CreateByKindAsync(string agentKind, string? id = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            CreatedByKind.Add((agentKind, id ?? string.Empty));
            return Task.FromResult<IActor>(new NoopActor(id ?? agentKind));
        }

        public Task DestroyAsync(string id, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            DestroyedActorIds.Add(id);
            return Task.CompletedTask;
        }

        public Task<IActor?> GetAsync(string id) => Task.FromResult<IActor?>(null);

        public Task<bool> ExistsAsync(string id) =>
            Task.FromResult(CreatedByKind.Any(created => string.Equals(created.Id, id, StringComparison.Ordinal)));

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task UnlinkAsync(string childId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class NoopActor(string id) : IActor
    {
        public string Id => id;

        public IAgent Agent => throw new NotSupportedException();

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private static RetiredActorCleanupCoordinatorGAgent CreateCoordinatorAgent(
        RecordingCoordinatorContinuationPublisher publisher)
    {
        var eventStore = new InMemoryEventStore();
        return new RetiredActorCleanupCoordinatorGAgent
        {
            EventSourcingBehaviorFactory =
                new DefaultEventSourcingBehaviorFactory<RetiredActorCleanupCoordinatorState>(eventStore),
            EventPublisher = publisher,
            Services = new ServiceCollection()
                .AddSingleton<IActorRuntimeCallbackScheduler, NoopCallbackScheduler>()
                .BuildServiceProvider(),
        };
    }

    private static EventEnvelope CreateEnvelope(IMessage payload) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(payload),
            Route = EnvelopeRouteSemantics.CreateDirect("test", RetiredActorCleanupCoordinatorGAgent.ActorId),
        };

    private sealed class CoordinatedCleanupDispatchPort : IActorDispatchPort
    {
        public RecordingCleanupContinuationPort ContinuationPort { get; } = new();

        public List<IMessage> Payloads { get; } = [];

        public Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            actorId.Should().Be(RetiredActorCleanupCoordinatorGAgent.ActorId);
            envelope.Payload.Should().NotBeNull();

            if (envelope.Payload!.TryUnpack<RetiredActorCleanupAcquireLeaseCommand>(out var acquire))
            {
                Payloads.Add(acquire);
                return CompleteAsync(
                    actorId,
                    envelope,
                    new RetiredActorCleanupCoordinatorContinuation
                    {
                        AcquireLease = new RetiredActorCleanupAcquireLeaseContinuation
                        {
                            Result = new RetiredActorCleanupAcquireLeaseResult
                            {
                                CommandId = acquire.CommandId,
                                SpecId = acquire.SpecId,
                                OwnerToken = acquire.OwnerToken,
                                Status = RetiredActorCleanupAcquireLeaseStatus.Granted,
                                CurrentOwnerToken = acquire.OwnerToken,
                            },
                        },
                    },
                    ct);
            }

            if (envelope.Payload.TryUnpack<RetiredActorCleanupCheckLeaseCommand>(out var check))
            {
                Payloads.Add(check);
                return CompleteAsync(
                    actorId,
                    envelope,
                    new RetiredActorCleanupCoordinatorContinuation
                    {
                        CheckLease = new RetiredActorCleanupCheckLeaseContinuation
                        {
                            Result = new RetiredActorCleanupCheckLeaseResult
                            {
                                CommandId = check.CommandId,
                                SpecId = check.SpecId,
                                OwnerToken = check.OwnerToken,
                                Status = RetiredActorCleanupCheckLeaseStatus.StillOwner,
                                CurrentOwnerToken = check.OwnerToken,
                            },
                        },
                    },
                    ct);
            }

            if (envelope.Payload.TryUnpack<RetiredActorCleanupReleaseLeaseCommand>(out var release))
            {
                Payloads.Add(release);
                return CompleteAsync(
                    actorId,
                    envelope,
                    new RetiredActorCleanupCoordinatorContinuation
                    {
                        ReleaseLease = new RetiredActorCleanupReleaseLeaseContinuation
                        {
                            Result = new RetiredActorCleanupReleaseLeaseResult
                            {
                                CommandId = release.CommandId,
                                SpecId = release.SpecId,
                                OwnerToken = release.OwnerToken,
                                Status = RetiredActorCleanupReleaseLeaseStatus.Released,
                            },
                        },
                    },
                    ct);
            }

            if (envelope.Payload.TryUnpack<RetiredActorCleanupRecordFailureCommand>(out var failure))
            {
                Payloads.Add(failure);
                return CompleteAsync(
                    actorId,
                    envelope,
                    new RetiredActorCleanupCoordinatorContinuation
                    {
                        RecordFailure = new RetiredActorCleanupRecordFailureContinuation
                        {
                            Result = new RetiredActorCleanupRecordFailureResult
                            {
                                CommandId = failure.CommandId,
                                SpecId = failure.SpecId,
                                OwnerToken = failure.OwnerToken,
                                Status = RetiredActorCleanupRecordFailureStatus.Recorded,
                                CurrentOwnerToken = failure.OwnerToken,
                                FailureCount = 1,
                            },
                        },
                    },
                    ct);
            }

            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }

        private async Task<DispatchAdmission> CompleteAsync(
            string actorId,
            EventEnvelope envelope,
            RetiredActorCleanupCoordinatorContinuation continuation,
            CancellationToken ct)
        {
            await ContinuationPort.PublishAsync(continuation, ct).ConfigureAwait(false);
            return DispatchAdmissionFactory.Create(actorId, envelope);
        }
    }

    private sealed class RecordingCleanupContinuationPort : IRetiredActorCleanupCoordinatorContinuationPort
    {
        private Func<RetiredActorCleanupCoordinatorContinuation, Task>? _handler;

        public Task<IAsyncDisposable> SubscribeAsync(
            Func<RetiredActorCleanupCoordinatorContinuation, Task> handler,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
            return Task.FromResult<IAsyncDisposable>(new Subscription(() => _handler = null));
        }

        public Task PublishAsync(RetiredActorCleanupCoordinatorContinuation continuation, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return _handler?.Invoke(continuation) ?? Task.CompletedTask;
        }

        private sealed class Subscription(Action dispose) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class RecordingCoordinatorContinuationPublisher : IEventPublisher
    {
        public List<RetiredActorCleanupAcquireLeaseContinuation> AcquireContinuations { get; } = [];

        public List<RetiredActorCleanupCheckLeaseContinuation> CheckContinuations { get; } = [];

        public List<RetiredActorCleanupReleaseLeaseContinuation> ReleaseContinuations { get; } = [];

        public List<RetiredActorCleanupRecordFailureContinuation> RecordFailureContinuations { get; } = [];

        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience audience = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            ct.ThrowIfCancellationRequested();
            audience.Should().Be(TopologyAudience.Self);
            Record(evt);
            return Task.CompletedTask;
        }

        public Task SendToAsync<TEvent>(
            string targetActorId,
            TEvent evt,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage =>
            throw new NotSupportedException();

        private void Record(IMessage evt)
        {
            switch (evt)
            {
                case RetiredActorCleanupAcquireLeaseContinuation acquire:
                    AcquireContinuations.Add(acquire);
                    break;
                case RetiredActorCleanupCheckLeaseContinuation check:
                    CheckContinuations.Add(check);
                    break;
                case RetiredActorCleanupReleaseLeaseContinuation release:
                    ReleaseContinuations.Add(release);
                    break;
                case RetiredActorCleanupRecordFailureContinuation failure:
                    RecordFailureContinuations.Add(failure);
                    break;
                default:
                    throw new NotSupportedException(evt.GetType().FullName);
            }
        }
    }

    private sealed class NoopCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<RuntimeCallbackLease> ScheduleTimerAsync(
            RuntimeCallbackTimerRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default) => Task.CompletedTask;

        public Task PurgeActorAsync(string actorId, CancellationToken ct = default) => Task.CompletedTask;
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
