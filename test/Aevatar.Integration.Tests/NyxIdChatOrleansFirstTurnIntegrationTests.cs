using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core.TypeSystem;
using Aevatar.Foundation.Runtime.Implementations.Orleans.DependencyInjection;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.GAgents.NyxidChat;
using Aevatar.Studio.Application.Studio.Abstractions;
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

public sealed class NyxIdChatOrleansFirstTurnIntegrationTests
{
    [Fact]
    public async Task FirstTurn_FromConversationActor_ShouldLinkDispatchAndComplete()
    {
        var conversationActorId = $"nyxid-chat-{Guid.NewGuid():N}";
        var turnActorId = NyxIdChatTurnActorIds.ForTurn(conversationActorId, "turn-alpha");
        var history = new RecordingChatHistoryCommandPort();
        var executor = new FixedTurnOperationExecutor();
        var host = await StartSiloHostAsync(history, executor);

        try
        {
            await AssertFirstTurnAsync(
                host,
                conversationActorId,
                turnActorId,
                history,
                executor,
                conversation => conversation.HandleEnvelopeAsync(CreateStartEnvelope(conversationActorId)));
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task TurnReactivation_WithAdmittedOperation_ShouldCompleteRecoveryAfterActivation()
    {
        var conversationActorId = $"nyxid-chat-{Guid.NewGuid():N}";
        var turnActorId = NyxIdChatTurnActorIds.ForTurn(
            conversationActorId,
            "turn-recovery");
        var history = new RecordingChatHistoryCommandPort();
        var executor = new FixedTurnOperationExecutor();
        var eventStore = new SignalingEventStore();
        var host = await StartSiloHostAsync(history, executor, eventStore);

        try
        {
            var grainFactory = host.Services.GetRequiredService<IGrainFactory>();
            var conversation = grainFactory.GetGrain<IRuntimeActorGrain>(conversationActorId);
            (await conversation.InitializeAgentByKindAsync(NyxIdChatServiceDefaults.GAgentKind))
                .Should().BeTrue();

            var turn = grainFactory.GetGrain<IRuntimeActorGrain>(turnActorId);
            (await turn.InitializeAgentByKindAsync(NyxIdChatServiceDefaults.TurnGAgentKind))
                .Should().BeTrue();
            await turn.DeactivateAsync();

            await eventStore.AppendAsync(
                turnActorId,
                [CreateTurnAdmission(turnActorId, conversationActorId)],
                expectedVersion: 0);

            var reactivated = grainFactory.GetGrain<IRuntimeActorGrain>(turnActorId);
            (await reactivated.IsInitializedAsync().WaitAsync(TimeSpan.FromSeconds(5)))
                .Should().BeTrue();
            var committed = await eventStore.WaitForTurnRecoveryAsync(
                turnActorId,
                TimeSpan.FromSeconds(10));

            committed.Select(static item => item.EventData.TypeUrl).Should().Equal(
                Any.Pack(new NyxIdChatTurnOperationAdmittedEvent()).TypeUrl,
                Any.Pack(new NyxIdChatTurnOperationCompletedEvent()).TypeUrl,
                Any.Pack(new NyxIdChatTurnOperationDeliveredEvent()).TypeUrl);
            executor.CallCount.Should().Be(0,
                "activation recovery must not repeat operation I/O");
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    private static async Task AssertFirstTurnAsync(
        IHost host,
        string conversationActorId,
        string turnActorId,
        RecordingChatHistoryCommandPort history,
        FixedTurnOperationExecutor executor,
        Func<IRuntimeActorGrain, Task> startTurn)
    {
        var grainFactory = host.Services.GetRequiredService<IGrainFactory>();
        var conversation = grainFactory.GetGrain<IRuntimeActorGrain>(conversationActorId);
        (await conversation.InitializeAgentByKindAsync(NyxIdChatServiceDefaults.GAgentKind))
            .Should().BeTrue();

        await startTurn(conversation);

        var terminal = await history.WaitForTerminalAsync(TimeSpan.FromSeconds(30));
        terminal.Status.Should().Be(ChatHistoryTurnTerminalStatus.Completed);
        terminal.Text.Should().Be("OK");
        executor.CallCount.Should().Be(1);

        var turn = grainFactory.GetGrain<IRuntimeActorGrain>(turnActorId);
        (await conversation.GetChildrenAsync()).Should().ContainSingle().Which.Should().Be(turnActorId);
        (await turn.GetParentAsync()).Should().Be(conversationActorId);

        var forwarding = host.Services.GetRequiredService<IStreamForwardingRegistry>();
        (await forwarding.ListBySourceAsync(conversationActorId)).Should()
            .ContainSingle(binding => binding.TargetStreamId == turnActorId);
        (await forwarding.ListBySourceAsync(turnActorId)).Should()
            .ContainSingle(binding => binding.TargetStreamId == conversationActorId);

        var eventStore = host.Services.GetRequiredService<IEventStore>();
        (await eventStore.GetEventsAsync(conversationActorId)).Select(item => item.EventData.TypeUrl)
            .Should().Contain(
                Any.Pack(new NyxIdChatOperationDispatchedEvent()).TypeUrl,
                Any.Pack(new NyxIdChatOperationReconciledEvent()).TypeUrl);
        (await eventStore.GetEventsAsync(turnActorId)).Select(item => item.EventData.TypeUrl)
            .Should().Contain(
                Any.Pack(new NyxIdChatTurnOperationAdmittedEvent()).TypeUrl,
                Any.Pack(new NyxIdChatTurnOperationCompletedEvent()).TypeUrl,
                Any.Pack(new NyxIdChatTurnOperationDeliveredEvent()).TypeUrl);
    }

    private static Task<IHost> StartSiloHostAsync(
        IChatHistoryCommandPort history,
        INyxIdChatTurnOperationExecutor executor,
        SignalingEventStore? eventStore = null) =>
        SharedOrleansPortAllocator.StartHostAsync(ports => Host.CreateDefaultBuilder()
            .UseOrleans(siloBuilder =>
            {
                siloBuilder.UseLocalhostClustering(
                    ports.SiloPort,
                    ports.GatewayPort,
                    serviceId: $"aevatar-nyxid-first-turn-service-{Guid.NewGuid():N}",
                    clusterId: $"aevatar-nyxid-first-turn-cluster-{Guid.NewGuid():N}");
                siloBuilder.AddAevatarFoundationRuntimeOrleans(options =>
                {
                    options.StreamBackend = AevatarOrleansRuntimeOptions.StreamBackendInMemory;
                    options.PersistenceBackend = AevatarOrleansRuntimeOptions.PersistenceBackendInMemory;
                });
                siloBuilder.ConfigureServices(services =>
                    services.AddAevatarAgentKindRegistry(builder => builder
                        .Register<NyxIdChatConversationGAgent>()
                        .Register<NyxIdChatTurnGAgent>()));
            })
            .ConfigureServices(services =>
            {
                services.AddSingleton(history);
                services.AddSingleton(executor);
                services.AddSingleton(TimeProvider.System);
                if (eventStore is not null)
                {
                    services.Replace(ServiceDescriptor.Singleton<IEventStore>(eventStore));
                    services.Replace(
                        ServiceDescriptor.Singleton<IEventStoreMaintenance>(eventStore));
                }
            })
            .Build());

    private static StateEvent CreateTurnAdmission(
        string turnActorId,
        string conversationActorId) =>
        new()
        {
            EventId = "turn-recovery-admission",
            AgentId = turnActorId,
            Version = 1,
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            EventType = NyxIdChatTurnOperationAdmittedEvent.Descriptor.FullName,
            EventData = Any.Pack(new NyxIdChatTurnOperationAdmittedEvent
            {
                Key = new NyxIdChatOperationKey
                {
                    ConversationActorId = conversationActorId,
                    TurnId = "turn-recovery",
                    TaskId = "task-recovery",
                    StepId = "step-recovery",
                    OperationId = "operation-recovery",
                    OperationGeneration = 1,
                },
                OperationKind = NyxIdChatStepKind.Llm,
                AdmittedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            }),
        };

    private static byte[] CreateStartEnvelope(string conversationActorId) =>
        new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(new NyxIdChatStartTurnCommand
            {
                ScopeId = "scope-alpha",
                ConversationActorId = conversationActorId,
                TurnId = "turn-alpha",
                TaskId = "task-alpha",
                ClientRequestId = "client-alpha",
                CommandId = "command-alpha",
                CorrelationId = "correlation-alpha",
                Prompt = "Reply with exactly: OK",
            }),
            Route = EnvelopeRouteSemantics.CreateDirect("test", conversationActorId),
            Runtime = new EnvelopeRuntime
            {
                Dispatch = new EnvelopeDispatchControl { PropagateFailure = true },
            },
        }.ToByteArray();

    private sealed class FixedTurnOperationExecutor : INyxIdChatTurnOperationExecutor
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public async Task<NyxIdChatTurnOperationExecution> ExecuteAsync(
            NyxIdChatOperationDispatchCommand command,
            NyxIdChatTransientExecutionSession session,
            Func<NyxIdChatOperationProgressSignal, CancellationToken, Task> reportProgressAsync,
            CancellationToken ct)
        {
            await Task.Yield();
            Interlocked.Increment(ref _callCount);
            return new NyxIdChatTurnOperationExecution(
                new NyxIdChatOperationResultSignal
                {
                    Key = command.Key.Clone(),
                    Llm = new NyxIdChatLLMOperationResult { Content = "OK" },
                });
        }
    }

    private sealed class RecordingChatHistoryCommandPort : IChatHistoryCommandPort
    {
        private readonly TaskCompletionSource<ChatHistoryTurnTerminalNotification> _terminal =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ChatHistoryTurnTerminalNotification> WaitForTerminalAsync(TimeSpan timeout) =>
            _terminal.Task.WaitAsync(timeout);

        public Task InitializeConversationAsync(
            ChatHistoryConversationInitialization request,
            CancellationToken ct = default) => Task.CompletedTask;

        public Task ReserveTurnDeliveryAsync(
            ChatHistoryTurnDeliveryReservation request,
            CancellationToken ct = default) => Task.CompletedTask;

        public Task NotifyTurnTerminalAsync(
            ChatHistoryTurnTerminalNotification notification,
            CancellationToken ct = default)
        {
            _terminal.TrySetResult(notification);
            return Task.CompletedTask;
        }

        public Task SaveMessagesAsync(
            string scopeId,
            string conversationId,
            ConversationMeta meta,
            IReadOnlyList<StoredChatMessage> messages,
            CancellationToken ct = default) => Task.CompletedTask;

        public Task<ChatHistoryDeleteResult> DeleteConversationAsync(
            string scopeId,
            string conversationId,
            CancellationToken ct = default) =>
            Task.FromResult(ChatHistoryDeleteResult.Accepted());
    }

    private sealed class SignalingEventStore : IEventStore, IEventStoreMaintenance
    {
        private readonly InMemoryEventStore _inner = new();
        private readonly TaskCompletionSource<string> _turnRecovery =
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
                    item.EventData.Is(NyxIdChatTurnOperationDeliveredEvent.Descriptor)))
            {
                _turnRecovery.TrySetResult(agentId);
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

        public async Task<IReadOnlyList<StateEvent>> WaitForTurnRecoveryAsync(
            string actorId,
            TimeSpan timeout)
        {
            var recoveredActorId = await _turnRecovery.Task.WaitAsync(timeout);
            recoveredActorId.Should().Be(actorId);
            return await GetEventsAsync(actorId);
        }
    }
}
