using System.Reflection;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Core;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.AI.Tests;

public class RoleGAgentReplayContractTests
{
    [Fact]
    public async Task InitializeRoleEvent_ShouldPersistAndReplayRoleState()
    {
        var store = new InMemoryEventStoreForTests();
        var services = BuildServices(store);

        var agent1 = CreateAgent(services, "role-init-replay");
        await agent1.ActivateAsync();
        await agent1.HandleInitializeRoleAgent(new InitializeRoleAgentEvent
        {
            RoleName = "researcher",
            ProviderName = "mock",
            Model = "m1",
            SystemPrompt = "be helpful",
            MaxToolRounds = 4,
            MaxHistoryMessages = 32,
            StreamBufferCapacity = 128,
        });
        await agent1.DeactivateAsync();

        var persisted = await store.GetEventsAsync("role-init-replay");
        persisted.Should().ContainSingle(x => x.EventType.Contains(nameof(InitializeRoleAgentEvent), StringComparison.Ordinal));

        var agent2 = CreateAgent(services, "role-init-replay");
        await agent2.ActivateAsync();

        agent2.RoleName.Should().Be("researcher");
        agent2.State.RoleName.Should().Be("researcher");
        agent2.EffectiveConfig.ProviderName.Should().Be("mock");
        agent2.EffectiveConfig.Model.Should().Be("m1");
        agent2.EffectiveConfig.SystemPrompt.Should().Be("be helpful");
        agent2.EffectiveConfig.MaxToolRounds.Should().Be(4);
        agent2.EffectiveConfig.MaxHistoryMessages.Should().Be(32);
        agent2.EffectiveConfig.StreamBufferCapacity.Should().Be(128);
    }

    [Fact]
    public async Task InitializeRoleEvent_ShouldPreserveExplicitZeroTemperature()
    {
        var store = new InMemoryEventStoreForTests();
        var services = BuildServices(store);

        var agent = CreateAgent(services, "role-temperature-zero");
        await agent.ActivateAsync();
        await agent.HandleInitializeRoleAgent(new InitializeRoleAgentEvent
        {
            RoleName = "assistant",
            ProviderName = "mock",
            SystemPrompt = "system",
            Temperature = 0,
        });

        agent.EffectiveConfig.Temperature.Should().Be(0);

        var persisted = await store.GetEventsAsync("role-temperature-zero");
        persisted.Should().ContainSingle();
        var evt = persisted.Single().EventData.Unpack<InitializeRoleAgentEvent>();
        evt.HasTemperature.Should().BeTrue();
        evt.Temperature.Should().Be(0);
    }

    [Fact]
    public async Task RoleGAgentFactory_ShouldUseEventSourcedInitializePath()
    {
        var store = new InMemoryEventStoreForTests();
        var services = BuildServices(store);

        var agent1 = CreateAgent(services, "role-factory-replay");
        await agent1.ActivateAsync();
        await RoleGAgentFactory.ApplyInitialization(agent1, new RoleYamlConfig
        {
            Name = "assistant",
            Provider = "mock",
            SystemPrompt = "system",
        }, services);
        await agent1.DeactivateAsync();

        var persisted = await store.GetEventsAsync("role-factory-replay");
        persisted.Should().ContainSingle(x => x.EventType.Contains(nameof(InitializeRoleAgentEvent), StringComparison.Ordinal));

        var agent2 = CreateAgent(services, "role-factory-replay");
        await agent2.ActivateAsync();
        agent2.State.RoleName.Should().Be("assistant");
        agent2.RoleName.Should().Be("assistant");
    }

    [Fact]
    public async Task CompletedSession_ShouldReplayCachedCompletionWithoutCallingProviderAgain()
    {
        var store = new InMemoryEventStoreForTests();
        var provider = new CountingLlmProviderFactory("cached answer");
        var services = BuildServices(store);

        var agent1 = CreateAgent(services, "role-session-replay", provider);
        await agent1.ActivateAsync();
        await agent1.HandleInitializeRoleAgent(new InitializeRoleAgentEvent
        {
            RoleName = "assistant",
            ProviderName = provider.Name,
            SystemPrompt = "system",
        });
        await agent1.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "hello",
            SessionId = "session-1",
        });
        await agent1.DeactivateAsync();

        provider.StreamCallCount.Should().Be(1);
        provider.StreamRequests.Should().ContainSingle();
        provider.StreamRequests[0].RequestId.Should().Be("session-1");
        var persisted = await store.GetEventsAsync("role-session-replay");
        persisted.Should().Contain(x => x.EventType.Contains(nameof(RoleChatSessionStartedEvent), StringComparison.Ordinal));
        persisted.Should().Contain(x => x.EventType.Contains(nameof(RoleChatSessionCompletedEvent), StringComparison.Ordinal));

        var replayPublisher = new RecordingEventPublisher();
        var agent2 = CreateAgent(services, "role-session-replay", provider);
        agent2.EventPublisher = replayPublisher;
        await agent2.ActivateAsync();

        await agent2.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "hello",
            SessionId = "session-1",
        });

        provider.StreamCallCount.Should().Be(1);
        replayPublisher.Published
            .OfType<TextMessageStartEvent>()
            .Should()
            .ContainSingle(x => x.SessionId == "session-1");
        replayPublisher.Published
            .OfType<TextMessageContentEvent>()
            .Should()
            .ContainSingle(x => x.Delta == "cached answer" && x.SessionId == "session-1");
        replayPublisher.Published
            .OfType<TextMessageEndEvent>()
            .Should()
            .ContainSingle(x => x.Content == "cached answer");
    }

    [Fact]
    public async Task StartedSessionReplay_ShouldResumeProviderCallAndPersistCompletion()
    {
        var store = new InMemoryEventStoreForTests();
        var provider = new CountingLlmProviderFactory("resumed answer");
        var services = BuildServices(store);

        var agent1 = CreateAgent(services, "role-session-resume", provider);
        await agent1.ActivateAsync();
        await agent1.HandleInitializeRoleAgent(new InitializeRoleAgentEvent
        {
            RoleName = "assistant",
            ProviderName = provider.Name,
            SystemPrompt = "system",
        });
        await agent1.DeactivateAsync();

        await store.AppendAsync(
            "role-session-resume",
            [
                StateEventFor(
                    "role-session-resume",
                    2,
                    new RoleChatSessionStartedEvent
                    {
                        SessionId = "session-2",
                        Prompt = "hello again",
                    }),
            ],
            expectedVersion: 1);

        var replayPublisher = new RecordingEventPublisher();
        var agent2 = CreateAgent(services, "role-session-resume", provider);
        agent2.EventPublisher = replayPublisher;
        await agent2.ActivateAsync();

        await agent2.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "hello again",
            SessionId = "session-2",
        });

        provider.StreamCallCount.Should().Be(1);
        provider.StreamRequests.Should().ContainSingle();
        provider.StreamRequests[0].RequestId.Should().Be("session-2");
        replayPublisher.Published
            .OfType<TextMessageStartEvent>()
            .Should()
            .Contain(x => x.SessionId == "session-2");
        replayPublisher.Published
            .OfType<TextMessageEndEvent>()
            .Should()
            .Contain(x => x.Content == "resumed answer");

        var persisted = await store.GetEventsAsync("role-session-resume");
        persisted.Should().Contain(x => x.EventType.Contains(nameof(RoleChatSessionCompletedEvent), StringComparison.Ordinal));
    }

    [Fact]
    public async Task RoleChatSessions_ShouldRetainOnlyRecentBoundedCache()
    {
        var store = new InMemoryEventStoreForTests();
        var provider = new CountingLlmProviderFactory("bounded");
        var services = BuildServices(store);

        var agent = CreateAgent(services, "role-session-retention", provider);
        await agent.ActivateAsync();
        await agent.HandleInitializeRoleAgent(new InitializeRoleAgentEvent
        {
            RoleName = "assistant",
            ProviderName = provider.Name,
            SystemPrompt = "system",
        });

        for (var i = 1; i <= 130; i++)
        {
            await agent.HandleChatRequest(new ChatRequestEvent
            {
                Prompt = $"prompt-{i}",
                SessionId = $"session-{i}",
            });
        }

        agent.State.Sessions.Count.Should().Be(128);
        agent.State.Sessions.ContainsKey("session-1").Should().BeFalse();
        agent.State.Sessions.ContainsKey("session-2").Should().BeFalse();
        agent.State.Sessions.ContainsKey("session-130").Should().BeTrue();
    }

    private static IServiceProvider BuildServices(InMemoryEventStoreForTests store)
    {
        return new ServiceCollection()
            .AddSingleton<IEventStore>(store)
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
            .BuildServiceProvider();
    }

    private static RoleGAgent CreateAgent(
        IServiceProvider services,
        string actorId,
        ILLMProviderFactory? providerFactory = null)
    {
        var agent = new RoleGAgent(providerFactory)
        {
            Services = services,
            EventSourcingBehaviorFactory = services.GetRequiredService<IEventSourcingBehaviorFactory<RoleGAgentState>>(),
        };
        AssignActorId(agent, actorId);
        return agent;
    }

    private static StateEvent StateEventFor(string agentId, long version, IMessage evt) =>
        new()
        {
            EventId = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Version = version,
            EventType = evt.Descriptor.FullName,
            EventData = Any.Pack(evt),
            AgentId = agentId,
        };

    private static void AssignActorId(RoleGAgent agent, string actorId)
    {
        var setIdMethod = typeof(GAgentBase).GetMethod(
            "SetId",
            BindingFlags.Instance | BindingFlags.NonPublic);
        setIdMethod.Should().NotBeNull();
        setIdMethod!.Invoke(agent, [actorId]);
    }

    private sealed class RecordingEventPublisher : IEventPublisher
    {
        public List<IMessage> Published { get; } = [];

        public Task PublishAsync<TEvent>(
            TEvent evt,
            EventDirection direction = EventDirection.Down,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            IReadOnlyDictionary<string, string>? metadata = null)
            where TEvent : IMessage
        {
            _ = direction;
            _ = ct;
            _ = sourceEnvelope;
            _ = metadata;
            Published.Add(evt);
            return Task.CompletedTask;
        }

        public Task SendToAsync<TEvent>(
            string targetActorId,
            TEvent evt,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            IReadOnlyDictionary<string, string>? metadata = null)
            where TEvent : IMessage
        {
            _ = targetActorId;
            return PublishAsync(evt, EventDirection.Self, ct, sourceEnvelope, metadata);
        }
    }

    private sealed class CountingLlmProviderFactory(string response) : ILLMProviderFactory, ILLMProvider
    {
        public int StreamCallCount { get; private set; }
        public List<LLMRequest> StreamRequests { get; } = [];

        public string Name => "counting";

        public ILLMProvider GetProvider(string name) => this;

        public ILLMProvider GetDefault() => this;

        public IReadOnlyList<string> GetAvailableProviders() => [Name];

        public Task<LLMResponse> ChatAsync(LLMRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new LLMResponse
            {
                Content = response,
                FinishReason = "stop",
                Usage = new TokenUsage(1, 1, 2),
            });
        }

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            StreamCallCount++;
            StreamRequests.Add(request);
            yield return new LLMStreamChunk
            {
                DeltaContent = response,
            };
            await Task.CompletedTask;
            yield return new LLMStreamChunk
            {
                IsLast = true,
                Usage = new TokenUsage(1, 1, 2),
            };
        }
    }
}
