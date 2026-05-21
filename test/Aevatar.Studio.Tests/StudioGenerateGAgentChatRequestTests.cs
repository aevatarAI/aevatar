using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Studio.Hosting.Endpoints;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;

namespace Aevatar.Studio.Tests;

public sealed class StudioGenerateGAgentChatRequestTests
{
    [Fact]
    public async Task ScriptGenerateGAgent_ShouldHandleChatRequest_AndPublishStreamingCompletion()
    {
        var services = BuildServices(out var eventStore);
        var publisher = new RecordingEventPublisher();
        var agent = CreateScriptAgent(services, publisher, new StreamingProviderFactory("studio-pong"));
        await agent.ActivateAsync();

        await agent.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "Classify this refund request and keep the member state in context.",
            SessionId = "cmd-1",
            ScopeId = "scope-a",
            TimeoutMs = 30000,
        });

        publisher.Published.Should().HaveCount(3);
        publisher.Published[0].Should().BeOfType<TextMessageStartEvent>()
            .Which.SessionId.Should().Be("cmd-1");
        publisher.Published[1].Should().BeOfType<TextMessageContentEvent>()
            .Which.Delta.Should().Be("studio-pong");
        publisher.Published[2].Should().BeOfType<TextMessageEndEvent>()
            .Which.Content.Should().Be("studio-pong");

        var persisted = await eventStore.GetEventsAsync("script-generate-1");
        var completed = persisted.Should().ContainSingle(x =>
                x.EventData.Is(RoleChatSessionCompletedEvent.Descriptor))
            .Subject.EventData.Unpack<RoleChatSessionCompletedEvent>();
        completed.SessionId.Should().Be("cmd-1");
        completed.Content.Should().Be("studio-pong");
        completed.ContentEmitted.Should().BeFalse();
    }

    [Fact]
    public async Task WorkflowGenerateGAgent_ShouldHandleChatRequest_AndPublishStreamingCompletion()
    {
        var services = BuildServices(out _);
        var publisher = new RecordingEventPublisher();
        var agent = CreateWorkflowAgent(services, publisher, new StreamingProviderFactory("workflow-pong"));
        await agent.ActivateAsync();

        await agent.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "Build a workflow.",
            SessionId = "cmd-2",
            ScopeId = "scope-a",
        });

        publisher.Published.OfType<TextMessageContentEvent>().Should().ContainSingle()
            .Which.Delta.Should().Be("workflow-pong");
        publisher.Published.OfType<TextMessageEndEvent>().Should().ContainSingle()
            .Which.Content.Should().Be("workflow-pong");
    }

    private static ServiceProvider BuildServices(out InMemoryEventStore eventStore)
    {
        eventStore = new InMemoryEventStore();
        return new ServiceCollection()
            .AddSingleton<IEventStore>(eventStore)
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
            .BuildServiceProvider();
    }

    private static ScriptGenerateGAgent CreateScriptAgent(
        IServiceProvider services,
        IEventPublisher publisher,
        ILLMProviderFactory providerFactory)
    {
        var agent = new ScriptGenerateGAgent(providerFactory)
        {
            Services = services,
            EventPublisher = publisher,
            EventSourcingBehaviorFactory = services.GetRequiredService<IEventSourcingBehaviorFactory<Empty>>(),
        };
        AssignActorId(agent, "script-generate-1");
        return agent;
    }

    private static WorkflowGenerateGAgent CreateWorkflowAgent(
        IServiceProvider services,
        IEventPublisher publisher,
        ILLMProviderFactory providerFactory)
    {
        var agent = new WorkflowGenerateGAgent(providerFactory)
        {
            Services = services,
            EventPublisher = publisher,
            EventSourcingBehaviorFactory = services.GetRequiredService<IEventSourcingBehaviorFactory<Empty>>(),
        };
        AssignActorId(agent, "workflow-generate-1");
        return agent;
    }

    private static void AssignActorId(GAgentBase agent, string actorId)
    {
        var setId = typeof(GAgentBase).GetMethod("SetId", BindingFlags.Instance | BindingFlags.NonPublic);
        setId.Should().NotBeNull();
        setId!.Invoke(agent, [actorId]);
    }

    private sealed class RecordingEventPublisher : IEventPublisher
    {
        public List<IMessage> Published { get; } = [];

        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience direction = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            _ = direction;
            _ = ct;
            _ = sourceEnvelope;
            _ = options;
            Published.Add(evt);
            return Task.CompletedTask;
        }

        public Task SendToAsync<TEvent>(
            string targetActorId,
            TEvent evt,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            _ = targetActorId;
            return PublishAsync(evt, TopologyAudience.Self, ct, sourceEnvelope, options);
        }
    }

    private sealed class StreamingProviderFactory(string response) : ILLMProviderFactory, ILLMProvider
    {
        public string Name => "studio-test";

        public ILLMProvider GetProvider(string name) => this;

        public ILLMProvider GetDefault() => this;

        public IReadOnlyList<string> GetAvailableProviders() => [Name];

        public Task<LLMResponse> ChatAsync(LLMRequest request, CancellationToken ct = default)
        {
            _ = request;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new LLMResponse { Content = response });
        }

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            _ = request;
            ct.ThrowIfCancellationRequested();
            yield return new LLMStreamChunk { DeltaContent = response };
            await Task.CompletedTask;
        }
    }

    private sealed class InMemoryEventStore : IEventStore
    {
        private readonly Dictionary<string, List<StateEvent>> _events = new(StringComparer.Ordinal);

        public Task<EventStoreCommitResult> AppendAsync(
            string agentId,
            IEnumerable<StateEvent> events,
            long expectedVersion,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!_events.TryGetValue(agentId, out var stream))
            {
                stream = [];
                _events[agentId] = stream;
            }

            var currentVersion = stream.Count == 0 ? 0 : stream[^1].Version;
            if (currentVersion != expectedVersion)
                throw new EventStoreOptimisticConcurrencyException(agentId, expectedVersion, currentVersion);

            var appended = events.Select(static x => x.Clone()).ToList();
            stream.AddRange(appended);
            return Task.FromResult(new EventStoreCommitResult
            {
                AgentId = agentId,
                LatestVersion = stream.Count == 0 ? 0 : stream[^1].Version,
                CommittedEvents = { appended.Select(static x => x.Clone()) },
            });
        }

        public Task<IReadOnlyList<StateEvent>> GetEventsAsync(
            string agentId,
            long? fromVersion = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!_events.TryGetValue(agentId, out var stream))
                return Task.FromResult<IReadOnlyList<StateEvent>>([]);

            IReadOnlyList<StateEvent> result = fromVersion.HasValue
                ? stream.Where(x => x.Version > fromVersion.Value).Select(static x => x.Clone()).ToList()
                : stream.Select(static x => x.Clone()).ToList();
            return Task.FromResult(result);
        }

        public Task<long> GetVersionAsync(string agentId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!_events.TryGetValue(agentId, out var stream) || stream.Count == 0)
                return Task.FromResult(0L);

            return Task.FromResult(stream[^1].Version);
        }

        public Task<long> DeleteEventsUpToAsync(string agentId, long toVersion, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (toVersion <= 0 || !_events.TryGetValue(agentId, out var stream))
                return Task.FromResult(0L);

            var before = stream.Count;
            stream.RemoveAll(x => x.Version <= toVersion);
            return Task.FromResult((long)(before - stream.Count));
        }
    }
}
