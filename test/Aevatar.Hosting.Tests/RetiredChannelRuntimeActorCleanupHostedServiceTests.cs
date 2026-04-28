using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Runtime.Persistence;
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

    private static RetiredChannelRuntimeActorCleanupHostedService CreateService(
        IActorTypeProbe typeProbe,
        RecordingActorRuntime runtime,
        RecordingStreamProvider streamProvider,
        InMemoryEventStore eventStore)
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
            new ServiceCollection().BuildServiceProvider(),
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
}
