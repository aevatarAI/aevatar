using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core.TypeSystem;
using Aevatar.Foundation.Runtime.Implementations.Orleans.DependencyInjection;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.GAgents.ChatHistory;
using Aevatar.Tests.Shared;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Orleans;
using Orleans.Hosting;

namespace Aevatar.Integration.Tests;

public sealed class ChatTurnHistoryDeliveryOrleansActivationTests
{
    [Fact]
    public async Task Reactivation_WithPendingTerminal_ShouldAppendAndCommitOnActivationScheduler()
    {
        var deliveryActorId = $"chat-history-delivery-{Guid.NewGuid():N}";
        var scopeId = $"scope-{Guid.NewGuid():N}";
        var conversationId = $"conversation-{Guid.NewGuid():N}";
        var conversationActorId = ChatHistoryActorIds.Conversation(scopeId, conversationId);
        var eventStore = new SignalingEventStore();
        var host = await StartSiloHostAsync(eventStore);

        try
        {
            var grainFactory = host.Services.GetRequiredService<IGrainFactory>();
            var delivery = grainFactory.GetGrain<IRuntimeActorGrain>(deliveryActorId);
            (await delivery.InitializeAgentByKindAsync("chat.history.turn-delivery"))
                .Should().BeTrue();
            await delivery.DeactivateAsync();

            await eventStore.AppendAsync(
                deliveryActorId,
                CreatePendingTerminalEvents(
                    deliveryActorId,
                    scopeId,
                    conversationId),
                expectedVersion: 0);

            var reactivated = grainFactory.GetGrain<IRuntimeActorGrain>(deliveryActorId);
            (await reactivated.IsInitializedAsync().WaitAsync(TimeSpan.FromSeconds(5)))
                .Should().BeTrue();
            var deliveryEvents = await eventStore.WaitForAppendCommitAsync(
                deliveryActorId,
                TimeSpan.FromSeconds(15));

            deliveryEvents.Select(static item => item.EventData.TypeUrl).Should().Equal(
                Any.Pack(new ChatTurnHistoryDeliveryReservedEvent()).TypeUrl,
                Any.Pack(new ChatTurnHistoryDeliveryTerminalFrameObserved()).TypeUrl,
                Any.Pack(new ChatTurnHistoryDeliveryAppendDispatchedEvent()).TypeUrl,
                Any.Pack(new ChatTurnHistoryDeliveryAppendResultRecordedEvent()).TypeUrl);
            deliveryEvents[^1].EventData
                .Unpack<ChatTurnHistoryDeliveryAppendResultRecordedEvent>()
                .Accepted.Should().BeTrue();

            (await eventStore.GetEventsAsync(conversationActorId))
                .Select(static item => item.EventData.TypeUrl)
                .Should().ContainSingle()
                .Which.Should().Be(Any.Pack(new ChatTurnAppendedEvent()).TypeUrl);
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    private static Task<IHost> StartSiloHostAsync(SignalingEventStore eventStore) =>
        SharedOrleansPortAllocator.StartHostAsync(ports => Host.CreateDefaultBuilder()
            .UseOrleans(siloBuilder =>
            {
                siloBuilder.UseLocalhostClustering(
                    ports.SiloPort,
                    ports.GatewayPort,
                    serviceId: $"aevatar-chat-history-delivery-service-{Guid.NewGuid():N}",
                    clusterId: $"aevatar-chat-history-delivery-cluster-{Guid.NewGuid():N}");
                siloBuilder.AddAevatarFoundationRuntimeOrleans(options =>
                {
                    options.StreamBackend = AevatarOrleansRuntimeOptions.StreamBackendInMemory;
                    options.PersistenceBackend = AevatarOrleansRuntimeOptions.PersistenceBackendInMemory;
                });
                siloBuilder.ConfigureServices(services =>
                    services.AddAevatarAgentKindRegistry(builder => builder
                        .Register<ChatTurnHistoryDeliveryGAgent>()
                        .Register<ChatConversationGAgent>()));
            })
            .ConfigureServices(services =>
            {
                services.AddSingleton(TimeProvider.System);
                services.Replace(ServiceDescriptor.Singleton<IEventStore>(eventStore));
                services.Replace(ServiceDescriptor.Singleton<IEventStoreMaintenance>(eventStore));
            })
            .Build());

    private static IReadOnlyList<StateEvent> CreatePendingTerminalEvents(
        string deliveryActorId,
        string scopeId,
        string conversationId)
    {
        var observedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return
        [
            StateEventOf(deliveryActorId, 1, new ChatTurnHistoryDeliveryReservedEvent
            {
                DeliveryId = "delivery-alpha",
                ScopeId = scopeId,
                ConversationId = conversationId,
                TurnId = "turn-alpha",
                UserText = "prepare a report",
                SourceActorId = "source-alpha",
                SourceCommandId = "command-alpha",
                SourceCorrelationId = "correlation-alpha",
                ReservedAtUnixMs = observedAt - 1,
                CreateConversationIfMissing = true,
                RequestFingerprint = "fingerprint-alpha",
            }),
            StateEventOf(deliveryActorId, 2, new ChatTurnHistoryDeliveryTerminalFrameObserved
            {
                DeliveryId = "delivery-alpha",
                SourceActorId = "source-alpha",
                SourceCommandId = "command-alpha",
                Status = ChatTurnTerminalStatus.Completed,
                Text = "report complete",
                ObservedAtUnixMs = observedAt,
            }),
        ];
    }

    private static StateEvent StateEventOf(
        string actorId,
        long version,
        IMessage payload) =>
        new()
        {
            EventId = $"chat-history-delivery-{version}",
            AgentId = actorId,
            Version = version,
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            EventType = payload.Descriptor.FullName,
            EventData = Any.Pack(payload),
        };

    private sealed class SignalingEventStore : IEventStore, IEventStoreMaintenance
    {
        private readonly InMemoryEventStore _inner = new();
        private readonly TaskCompletionSource<string> _appendCommitted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<EventStoreCommitResult> AppendAsync(
            string agentId,
            IEnumerable<StateEvent> events,
            long expectedVersion,
            CancellationToken ct = default)
        {
            var batch = events.ToArray();
            var result = await _inner.AppendAsync(agentId, batch, expectedVersion, ct);
            if (batch.Any(static item =>
                    item.EventData.Is(ChatTurnHistoryDeliveryAppendResultRecordedEvent.Descriptor)))
            {
                _appendCommitted.TrySetResult(agentId);
            }

            return result;
        }

        public Task<IReadOnlyList<StateEvent>> GetEventsAsync(
            string agentId,
            long? fromVersion = null,
            CancellationToken ct = default) =>
            _inner.GetEventsAsync(agentId, fromVersion, ct);

        public Task<long> GetVersionAsync(
            string agentId,
            CancellationToken ct = default) =>
            _inner.GetVersionAsync(agentId, ct);

        public Task<long> DeleteEventsUpToAsync(
            string agentId,
            long toVersion,
            CancellationToken ct = default) =>
            _inner.DeleteEventsUpToAsync(agentId, toVersion, ct);

        public Task<bool> ResetStreamAsync(
            string agentId,
            CancellationToken ct = default) =>
            _inner.ResetStreamAsync(agentId, ct);

        public async Task<IReadOnlyList<StateEvent>> WaitForAppendCommitAsync(
            string actorId,
            TimeSpan timeout)
        {
            var committedActorId = await _appendCommitted.Task.WaitAsync(timeout);
            committedActorId.Should().Be(actorId);
            return await GetEventsAsync(actorId);
        }
    }
}
