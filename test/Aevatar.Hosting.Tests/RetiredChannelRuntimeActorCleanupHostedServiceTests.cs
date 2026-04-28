using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.Scheduled;
using Aevatar.Mainnet.Host.Api.Hosting.Migration;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Hosting.Tests;

public sealed class RetiredChannelRuntimeActorCleanupHostedServiceTests
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
        var service = CreateService(typeProbe, runtime, streamProvider, eventStore);

        await service.StartAsync(CancellationToken.None);

        runtime.DestroyedActorIds.Should().Contain("channel-bot-registration-store");
        runtime.DestroyedActorIds.Should().Contain(
            "projection.durable.scope:channel-bot-registration:channel-bot-registration-store");
        streamProvider.RemovedRelays.Should().Contain((
            "channel-bot-registration-store",
            "projection.durable.scope:channel-bot-registration:channel-bot-registration-store"));
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
        var service = CreateService(typeProbe, runtime, new RecordingStreamProvider(), eventStore);

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
        var service = CreateService(typeProbe, runtime, new RecordingStreamProvider(), eventStore);

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
                AgentType = SkillRunnerDefaults.AgentType,
            },
            new UserAgentCatalogEntry
            {
                AgentId = "workflow-agent-old",
                AgentType = WorkflowAgentDefaults.AgentType,
            },
            new UserAgentCatalogEntry
            {
                AgentId = "skill-runner-current",
                AgentType = SkillRunnerDefaults.AgentType,
            },
        ]);
        await AppendSingleEventAsync(eventStore, "skill-runner-old");
        await AppendSingleEventAsync(eventStore, "workflow-agent-old");
        await AppendSingleEventAsync(eventStore, "skill-runner-current");

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
        });
        var runtime = new RecordingActorRuntime();
        var service = CreateService(typeProbe, runtime, new RecordingStreamProvider(), eventStore);

        await service.StartAsync(CancellationToken.None);

        runtime.DestroyedActorIds.Should().Contain("skill-runner-old");
        runtime.DestroyedActorIds.Should().Contain("workflow-agent-old");
        runtime.DestroyedActorIds.Should().Contain("agent-registry-store");
        runtime.DestroyedActorIds.Should().NotContain("skill-runner-current");
        (await eventStore.GetVersionAsync("skill-runner-old")).Should().Be(0);
        (await eventStore.GetVersionAsync("workflow-agent-old")).Should().Be(0);
        (await eventStore.GetVersionAsync("skill-runner-current")).Should().Be(1);
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
        var services = new ServiceCollection();
        services.AddSingleton<IProjectionDocumentReader<UserAgentCatalogDocument, string>>(documents);
        services.AddSingleton<IProjectionWriteDispatcher<UserAgentCatalogDocument>>(documents);
        var typeProbe = new StubActorTypeProbe(new Dictionary<string, string?>
        {
            ["agent-registry-store"] =
                "Aevatar.GAgents.ChannelRuntime.UserAgentCatalogGAgent, Aevatar.GAgents.ChannelRuntime",
        });
        var runtime = new RecordingActorRuntime();
        var service = CreateService(
            typeProbe,
            runtime,
            new RecordingStreamProvider(),
            eventStore,
            services.BuildServiceProvider());

        await service.StartAsync(CancellationToken.None);

        documents.DeletedIds.Should().Equal("agent-doc-delete");
        documents.RemainingIds.Should().Equal("agent-doc-keep");
    }

    [Fact]
    public async Task StartAsync_ShouldContinue_WhenReadModelCleanupThrows()
    {
        var eventStore = new InMemoryEventStore();
        await AppendSingleEventAsync(eventStore, "channel-bot-registration-store");
        var services = new ServiceCollection();
        services.AddSingleton<IProjectionDocumentReader<ChannelBotRegistrationDocument, string>>(
            new ThrowingProjectionReader<ChannelBotRegistrationDocument>());
        services.AddSingleton<IProjectionWriteDispatcher<ChannelBotRegistrationDocument>>(
            new NoopProjectionWriter<ChannelBotRegistrationDocument>());
        var typeProbe = new StubActorTypeProbe(new Dictionary<string, string?>
        {
            ["channel-bot-registration-store"] =
                "Aevatar.GAgents.ChannelRuntime.ChannelBotRegistrationGAgent, Aevatar.GAgents.ChannelRuntime",
        });
        var runtime = new RecordingActorRuntime();
        var service = CreateService(
            typeProbe,
            runtime,
            new RecordingStreamProvider(),
            eventStore,
            services.BuildServiceProvider());

        await service.StartAsync(CancellationToken.None);

        runtime.DestroyedActorIds.Should().Contain("channel-bot-registration-store");
        (await eventStore.GetVersionAsync("channel-bot-registration-store")).Should().Be(0);
    }

    private static RetiredChannelRuntimeActorCleanupHostedService CreateService(
        IActorTypeProbe typeProbe,
        RecordingActorRuntime runtime,
        RecordingStreamProvider streamProvider,
        InMemoryEventStore eventStore,
        IServiceProvider? services = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Aevatar:RetiredChannelRuntimeActorCleanup:WaitPollMilliseconds"] = "1",
                ["Aevatar:RetiredChannelRuntimeActorCleanup:InProgressTimeoutSeconds"] = "1",
            })
            .Build();

        return new RetiredChannelRuntimeActorCleanupHostedService(
            typeProbe,
            runtime,
            streamProvider,
            eventStore,
            eventStore,
            services ?? new ServiceCollection().BuildServiceProvider(),
            configuration,
            NullLogger<RetiredChannelRuntimeActorCleanupHostedService>.Instance);
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

    private sealed class RecordingStreamProvider : IStreamProvider
    {
        public List<(string Source, string Target)> RemovedRelays { get; } = [];

        public IStream GetStream(string actorId) => new RecordingStream(actorId, RemovedRelays);
    }

    private sealed class RecordingStream(
        string streamId,
        List<(string Source, string Target)> removedRelays) : IStream
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
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<StreamForwardingBinding>> ListRelaysAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();
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
}
