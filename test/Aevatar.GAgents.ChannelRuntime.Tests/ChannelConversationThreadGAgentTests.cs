using System.Reflection;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ChannelConversationThreadGAgentTests
{
    [Fact]
    public async Task HandleInboundActivityAsync_WhenPrivateNewCommand_RotatesBeforeDispatchingToConversation()
    {
        var actorId = ChannelConversationThreadGAgent.BuildActorId("lark:dm:user-2");
        var eventStore = new InMemoryEventStore();
        var actorRuntime = new CapturingActorRuntime();
        var dispatchPort = new CapturingActorDispatchPort();
        var agent = await CreateAgentAsync(actorId, eventStore, actorRuntime, dispatchPort);

        await agent.HandleInboundActivityAsync(BuildInboundActivity("msg-new-1", "/new", ConversationScope.DirectMessage));

        agent.State.Generation.Should().Be(1);
        agent.State.ActiveConversationCanonicalKey.Should().Be("lark:dm:user-2#session-00000001");
        agent.State.LastRotationActivityId.Should().Be("msg-new-1");
        actorRuntime.CreatedActorIds.Should().ContainSingle("channel-conversation:lark:dm:user-2#session-00000001");
        var captured = dispatchPort.Envelopes.Should().ContainSingle().Which;
        captured.ActorId.Should().Be("channel-conversation:lark:dm:user-2#session-00000001");
        captured.Envelope.Payload.Is(ChatActivity.Descriptor).Should().BeTrue();
        captured.Envelope.Payload.Unpack<ChatActivity>().Id.Should().Be("msg-new-1");

        var events = await eventStore.GetEventsAsync(actorId);
        events.Should().ContainSingle(
            x => x.EventData.TypeUrl.EndsWith(ConversationThreadRotatedEvent.Descriptor.FullName, StringComparison.Ordinal));
    }

    [Fact]
    public async Task HandleInboundActivityAsync_WhenPrivateNewCommandIsRedelivered_DoesNotRotateAgain()
    {
        var actorId = ChannelConversationThreadGAgent.BuildActorId("lark:dm:user-2");
        var eventStore = new InMemoryEventStore();
        var actorRuntime = new CapturingActorRuntime();
        var dispatchPort = new CapturingActorDispatchPort();
        var agent = await CreateAgentAsync(actorId, eventStore, actorRuntime, dispatchPort);
        var activity = BuildInboundActivity("msg-new-1", "/new", ConversationScope.DirectMessage);

        await agent.HandleInboundActivityAsync(activity);
        await agent.HandleInboundActivityAsync(activity);

        agent.State.Generation.Should().Be(1);
        agent.State.ActiveConversationCanonicalKey.Should().Be("lark:dm:user-2#session-00000001");
        actorRuntime.CreatedActorIds.Should().HaveCount(2);
        actorRuntime.CreatedActorIds.Should().AllBeEquivalentTo("channel-conversation:lark:dm:user-2#session-00000001");
        dispatchPort.Envelopes.Should().HaveCount(2);
        var events = await eventStore.GetEventsAsync(actorId);
        events.Should().ContainSingle(
            x => x.EventData.TypeUrl.EndsWith(ConversationThreadRotatedEvent.Descriptor.FullName, StringComparison.Ordinal));
    }

    [Fact]
    public async Task HandleInboundActivityAsync_WhenActivityIsNotPrivateNewCommand_DispatchesToCurrentConversation()
    {
        var actorId = ChannelConversationThreadGAgent.BuildActorId("lark:dm:user-2");
        var eventStore = new InMemoryEventStore();
        var actorRuntime = new CapturingActorRuntime();
        var dispatchPort = new CapturingActorDispatchPort();
        var agent = await CreateAgentAsync(actorId, eventStore, actorRuntime, dispatchPort);

        await agent.HandleInboundActivityAsync(BuildInboundActivity("msg-plain-1", "hello", ConversationScope.DirectMessage));

        agent.State.Generation.Should().Be(0);
        actorRuntime.CreatedActorIds.Should().ContainSingle("channel-conversation:lark:dm:user-2");
        dispatchPort.Envelopes.Should().ContainSingle().Which.ActorId.Should().Be("channel-conversation:lark:dm:user-2");
        var events = await eventStore.GetEventsAsync(actorId);
        events.Should().BeEmpty();
    }

    private static async Task<ChannelConversationThreadGAgent> CreateAgentAsync(
        string id,
        InMemoryEventStore eventStore,
        IActorRuntime actorRuntime,
        IActorDispatchPort dispatchPort)
    {
        var serviceProvider = new ServiceCollection()
            .AddSingleton<IEventStore>(eventStore)
            .AddSingleton(actorRuntime)
            .AddSingleton(dispatchPort)
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
            .BuildServiceProvider();

        var agent = new ChannelConversationThreadGAgent
        {
            Services = serviceProvider,
            EventPublisher = new NoopEventPublisher(),
            EventSourcingBehaviorFactory =
                serviceProvider.GetRequiredService<IEventSourcingBehaviorFactory<ConversationThreadGAgentState>>(),
        };
        SetId(agent, id);
        await agent.ActivateAsync();
        return agent;
    }

    private static ChatActivity BuildInboundActivity(string messageId, string text, ConversationScope scope) =>
        new()
        {
            Id = messageId,
            Type = ActivityType.Message,
            ChannelId = ChannelId.From("lark"),
            Bot = BotInstanceId.From("reg-1"),
            Conversation = ConversationReference.Create(
                ChannelId.From("lark"),
                BotInstanceId.From("reg-1"),
                scope,
                "user-2",
                scope == ConversationScope.DirectMessage ? "dm" : "group",
                "user-2"),
            From = new ParticipantRef { CanonicalId = "user-2" },
            Content = new MessageContent { Text = text },
        };

    private static void SetId(object agent, string id)
    {
        var current = agent.GetType();
        while (current is not null)
        {
            var setIdMethod = current.GetMethod(
                "SetId",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (setIdMethod is not null)
            {
                setIdMethod.Invoke(agent, [id]);
                return;
            }

            current = current.BaseType;
        }

        throw new InvalidOperationException("Unable to set agent id via reflection.");
    }

    private sealed class CapturingActorRuntime : IActorRuntime
    {
        public List<string?> CreatedActorIds { get; } = [];

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default) where TAgent : IAgent
        {
            CreatedActorIds.Add(id);
            return Task.FromResult<IActor>(new NoopActor(id ?? string.Empty));
        }

        public Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DestroyAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IActor?> GetAsync(string id) => Task.FromResult<IActor?>(null);

        public Task<bool> ExistsAsync(string id) => Task.FromResult(false);

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task UnlinkAsync(string childId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class CapturingActorDispatchPort : IActorDispatchPort
    {
        public List<(string ActorId, EventEnvelope Envelope)> Envelopes { get; } = [];

        public Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            Envelopes.Add((actorId, envelope));
            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }
    }

    private sealed class NoopActor(string id) : IActor
    {
        public string Id { get; } = id;

        public IAgent Agent => throw new NotSupportedException();

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class NoopEventPublisher : IEventPublisher
    {
        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience audience = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage =>
            Task.CompletedTask;

        public Task SendToAsync<TEvent>(
            string targetActorId,
            TEvent evt,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage =>
            Task.CompletedTask;
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

            var appended = events.Select(x => x.Clone()).ToList();
            stream.AddRange(appended);
            return Task.FromResult(new EventStoreCommitResult
            {
                AgentId = agentId,
                LatestVersion = stream.Count == 0 ? 0 : stream[^1].Version,
                CommittedEvents = { appended.Select(x => x.Clone()) },
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
                ? stream.Where(x => x.Version > fromVersion.Value).Select(x => x.Clone()).ToList()
                : stream.Select(x => x.Clone()).ToList();
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
