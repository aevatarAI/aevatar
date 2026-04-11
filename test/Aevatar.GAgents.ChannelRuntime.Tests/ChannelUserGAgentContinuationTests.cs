using System.Reflection;
using Aevatar.AI.Abstractions;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public class ChannelUserGAgentContinuationTests
{
    [Fact]
    public async Task HandleChatEnd_ShouldSendReply_AndCompletePendingSession()
    {
        var runtime = new RecordingActorRuntime();
        var streams = new RecordingStreamProvider();
        var scheduler = new RecordingCallbackScheduler();
        var adapter = new RecordingPlatformAdapter("lark");
        using var services = BuildServices(runtime, streams, scheduler, adapter, new InMemoryEventStore());
        var agent = CreateAgent(services, "channel-user-lark-reg-1-ou_123");

        await agent.ActivateAsync();
        await agent.HandleInbound(BuildInboundEvent());

        var request = runtime.SingleChatRequest();

        await agent.HandleEventAsync(BuildTopologyEnvelope(
            "channel-lark-reg-1-ou_123",
            TopologyAudience.Parent,
            new TextMessageContentEvent
            {
                SessionId = request.SessionId,
                Delta = "hello ",
            }));
        await agent.HandleEventAsync(BuildTopologyEnvelope(
            "channel-lark-reg-1-ou_123",
            TopologyAudience.Parent,
            new TextMessageContentEvent
            {
                SessionId = request.SessionId,
                Delta = "world",
            }));
        await agent.HandleEventAsync(BuildTopologyEnvelope(
            "channel-lark-reg-1-ou_123",
            TopologyAudience.Parent,
            new TextMessageEndEvent
            {
                SessionId = request.SessionId,
                Content = "hello world",
            }));

        adapter.Replies.Should().ContainSingle();
        adapter.Replies[0].ReplyText.Should().Be("hello world");
        agent.State.PendingSessions.Should().BeEmpty();
        scheduler.Canceled.Should().ContainSingle(x => x.CallbackId == $"chat-timeout-{request.SessionId}");
        streams.GetRecordingStream("channel-lark-reg-1-ou_123").RemovedTargets
            .Should().ContainSingle("channel-user-lark-reg-1-ou_123");
    }

    [Fact]
    public async Task HandleChatTimeout_SelfEnvelope_ShouldSendFallbackReply()
    {
        var runtime = new RecordingActorRuntime();
        var streams = new RecordingStreamProvider();
        var scheduler = new RecordingCallbackScheduler();
        var adapter = new RecordingPlatformAdapter("lark");
        using var services = BuildServices(runtime, streams, scheduler, adapter, new InMemoryEventStore());
        var agent = CreateAgent(services, "channel-user-lark-reg-1-ou_123");

        await agent.ActivateAsync();
        await agent.HandleInbound(BuildInboundEvent());

        var request = runtime.SingleChatRequest();
        await agent.HandleEventAsync(BuildTopologyEnvelope(
            "channel-user-lark-reg-1-ou_123",
            TopologyAudience.Self,
            new ChannelChatTimeoutEvent
            {
                SessionId = request.SessionId,
            }));

        adapter.Replies.Should().ContainSingle();
        adapter.Replies[0].ReplyText.Should().Be("Sorry, it's taking too long to respond. Please try again.");
        agent.State.PendingSessions.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleChatTimeout_ShouldSendPartialReply_WhenBufferedContentExists()
    {
        var runtime = new RecordingActorRuntime();
        var streams = new RecordingStreamProvider();
        var scheduler = new RecordingCallbackScheduler();
        var adapter = new RecordingPlatformAdapter("lark");
        using var services = BuildServices(runtime, streams, scheduler, adapter, new InMemoryEventStore());
        var agent = CreateAgent(services, "channel-user-lark-reg-1-ou_123");

        await agent.ActivateAsync();
        await agent.HandleInbound(BuildInboundEvent());

        var request = runtime.SingleChatRequest();
        await agent.HandleEventAsync(BuildTopologyEnvelope(
            "channel-lark-reg-1-ou_123",
            TopologyAudience.Parent,
            new TextMessageContentEvent
            {
                SessionId = request.SessionId,
                Delta = "partial reply",
            }));

        await agent.HandleEventAsync(BuildTopologyEnvelope(
            "channel-user-lark-reg-1-ou_123",
            TopologyAudience.Self,
            new ChannelChatTimeoutEvent
            {
                SessionId = request.SessionId,
            }));

        adapter.Replies.Should().ContainSingle();
        adapter.Replies[0].ReplyText.Should().Be("partial reply");
        agent.State.PendingSessions.Should().BeEmpty();
    }

    [Fact]
    public async Task ActivateAsync_ShouldRecoverPendingSession_AndRedispatchChatRequest()
    {
        const string actorId = "channel-user-lark-reg-1-ou_123";

        var eventStore = new InMemoryEventStore();
        var runtime1 = new RecordingActorRuntime();
        var streams1 = new RecordingStreamProvider();
        var scheduler1 = new RecordingCallbackScheduler();
        var adapter1 = new RecordingPlatformAdapter("lark");
        using (var services1 = BuildServices(runtime1, streams1, scheduler1, adapter1, eventStore))
        {
            var agent1 = CreateAgent(services1, actorId);
            await agent1.ActivateAsync();
            await agent1.HandleInbound(BuildInboundEvent());
            runtime1.ChatRequests.Should().ContainSingle();
        }

        var runtime2 = new RecordingActorRuntime();
        var streams2 = new RecordingStreamProvider();
        var scheduler2 = new RecordingCallbackScheduler();
        var adapter2 = new RecordingPlatformAdapter("lark");
        using var services2 = BuildServices(runtime2, streams2, scheduler2, adapter2, eventStore);
        var agent2 = CreateAgent(services2, actorId);

        await agent2.ActivateAsync();

        runtime2.ChatRequests.Should().ContainSingle();
        var recovered = runtime2.ChatRequests[0];
        recovered.Prompt.Should().Be("hello from lark");
        recovered.ScopeId.Should().Be("scope-1");
        scheduler2.TimeoutRequests.Should().ContainSingle(x => x.CallbackId == $"chat-timeout-{recovered.SessionId}");
        streams2.GetRecordingStream("channel-lark-reg-1-ou_123").Bindings
            .Should().ContainSingle(x => x.TargetStreamId == actorId);
    }

    [Fact]
    public async Task ActivateAsync_ShouldRecoverExpiredPendingSession_BySchedulingImmediateTimeoutWithoutRedispatch()
    {
        const string actorId = "channel-user-lark-reg-1-ou_123";

        var eventStore = new InMemoryEventStore();
        await SeedPendingSessionAsync(
            eventStore,
            actorId,
            new ChannelPendingChatSession
            {
                SessionId = "expired-session",
                ChatActorId = "channel-lark-reg-1-ou_123",
                OrgToken = "org-token",
                Platform = "lark",
                ConversationId = "oc_chat_1",
                SenderId = "ou_123",
                SenderName = "Alice",
                MessageId = "om_msg_1",
                ChatType = "p2p",
                RegistrationId = "reg-1",
                NyxProviderSlug = "api-lark-bot",
                RegistrationScopeId = "scope-1",
                Prompt = "hello from lark",
                TimeoutAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(-1)),
            });

        var runtime = new RecordingActorRuntime();
        var streams = new RecordingStreamProvider();
        var scheduler = new RecordingCallbackScheduler();
        var adapter = new RecordingPlatformAdapter("lark");
        using var services = BuildServices(runtime, streams, scheduler, adapter, eventStore);
        var agent = CreateAgent(services, actorId);

        await agent.ActivateAsync();

        runtime.ChatRequests.Should().BeEmpty();
        scheduler.TimeoutRequests.Should().ContainSingle(x => x.CallbackId == "chat-timeout-expired-session");
        streams.GetRecordingStream("channel-lark-reg-1-ou_123").Bindings.Should().BeEmpty();

        await agent.HandleEventAsync(scheduler.TimeoutRequests[0].TriggerEnvelope);

        adapter.Replies.Should().ContainSingle();
        adapter.Replies[0].ReplyText.Should().Be("Sorry, it's taking too long to respond. Please try again.");
        agent.State.PendingSessions.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleInbound_ShouldDedupProcessedMessageId()
    {
        var runtime = new RecordingActorRuntime();
        var streams = new RecordingStreamProvider();
        var scheduler = new RecordingCallbackScheduler();
        var adapter = new RecordingPlatformAdapter("lark");
        using var services = BuildServices(runtime, streams, scheduler, adapter, new InMemoryEventStore());
        var agent = CreateAgent(services, "channel-user-lark-reg-1-ou_123");
        var inbound = BuildInboundEvent();

        await agent.ActivateAsync();
        await agent.HandleInbound(inbound);
        await agent.HandleInbound(inbound);

        runtime.ChatRequests.Should().ContainSingle();
        agent.State.PendingSessions.Should().ContainSingle();
    }

    [Fact]
    public async Task HandleInbound_ShouldResumePendingSession_WhenPreviousDispatchFailed()
    {
        var runtime = new RecordingActorRuntime
        {
            FailChatRequestsRemaining = 1,
        };
        var streams = new RecordingStreamProvider();
        var scheduler = new RecordingCallbackScheduler();
        var adapter = new RecordingPlatformAdapter("lark");
        using var services = BuildServices(runtime, streams, scheduler, adapter, new InMemoryEventStore());
        var agent = CreateAgent(services, "channel-user-lark-reg-1-ou_123");
        var inbound = BuildInboundEvent();

        await agent.ActivateAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.HandleInbound(inbound));

        runtime.ChatRequests.Should().BeEmpty();
        agent.State.PendingSessions.Should().ContainSingle();

        await agent.HandleInbound(inbound);
        await agent.HandleInbound(inbound);

        runtime.ChatRequests.Should().ContainSingle();
        agent.State.PendingSessions.Should().ContainSingle();
    }

    private static ChannelInboundEvent BuildInboundEvent() => new()
    {
        Text = "hello from lark",
        SenderId = "ou_123",
        SenderName = "Alice",
        ConversationId = "oc_chat_1",
        MessageId = "om_msg_1",
        ChatType = "p2p",
        Platform = "lark",
        RegistrationId = "reg-1",
        RegistrationToken = "org-token",
        RegistrationScopeId = "scope-1",
        NyxProviderSlug = "api-lark-bot",
    };

    private static EventEnvelope BuildTopologyEnvelope(
        string publisherActorId,
        TopologyAudience audience,
        IMessage payload) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(payload),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication(publisherActorId, audience),
        };

    private static async Task SeedPendingSessionAsync(
        InMemoryEventStore eventStore,
        string actorId,
        ChannelPendingChatSession session)
    {
        await eventStore.AppendAsync(actorId,
        [
            new StateEvent
            {
                EventId = Guid.NewGuid().ToString("N"),
                Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                Version = 1,
                EventType = ChannelUserTrackedEvent.Descriptor.FullName,
                EventData = Any.Pack(new ChannelUserTrackedEvent
                {
                    Platform = session.Platform,
                    PlatformUserId = session.SenderId,
                    DisplayName = session.SenderName,
                }),
                AgentId = actorId,
            },
            new StateEvent
            {
                EventId = Guid.NewGuid().ToString("N"),
                Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                Version = 2,
                EventType = ChannelChatRequestedEvent.Descriptor.FullName,
                EventData = Any.Pack(new ChannelChatRequestedEvent
                {
                    Session = session,
                }),
                AgentId = actorId,
            },
        ], 0);
    }

    private static ServiceProvider BuildServices(
        RecordingActorRuntime runtime,
        RecordingStreamProvider streams,
        RecordingCallbackScheduler scheduler,
        RecordingPlatformAdapter adapter,
        IEventStore eventStore)
    {
        return new ServiceCollection()
            .AddSingleton(eventStore)
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
            .AddSingleton<IActorRuntime>(runtime)
            .AddSingleton<IStreamProvider>(streams)
            .AddSingleton<IActorRuntimeCallbackScheduler>(scheduler)
            .AddSingleton<IMemoryCache, MemoryCache>(_ => new MemoryCache(new MemoryCacheOptions()))
            .AddSingleton(new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://example.com" }))
            .AddSingleton<IPlatformAdapter>(adapter)
            .BuildServiceProvider();
    }

    private static ChannelUserGAgent CreateAgent(IServiceProvider services, string actorId)
    {
        var agent = new ChannelUserGAgent
        {
            Services = services,
            Logger = NullLogger.Instance,
            EventSourcingBehaviorFactory =
                services.GetRequiredService<IEventSourcingBehaviorFactory<ChannelUserState>>(),
        };

        typeof(GAgentBase)
            .GetMethod("SetId", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(agent, [actorId]);
        return agent;
    }

    private sealed class RecordingActorRuntime : IActorRuntime
    {
        private readonly Dictionary<string, RecordingActor> _actors = new(StringComparer.Ordinal);

        public List<ChatRequestEvent> ChatRequests { get; } = [];
        public int FailChatRequestsRemaining { get; set; }

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent =>
            CreateAsync(typeof(TAgent), id, ct);

        public Task<IActor> CreateAsync(global::System.Type agentType, string? id = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var actorId = id ?? Guid.NewGuid().ToString("N");
            var actor = new RecordingActor(actorId, this);
            _actors[actorId] = actor;
            return Task.FromResult<IActor>(actor);
        }

        public Task DestroyAsync(string id, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _actors.Remove(id);
            return Task.CompletedTask;
        }

        public Task<IActor?> GetAsync(string id) =>
            Task.FromResult<IActor?>(_actors.TryGetValue(id, out var actor) ? actor : null);

        public Task<bool> ExistsAsync(string id) => Task.FromResult(_actors.ContainsKey(id));

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task UnlinkAsync(string childId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public ChatRequestEvent SingleChatRequest() => ChatRequests.Should().ContainSingle().Subject;

        private sealed class RecordingActor(string id, RecordingActorRuntime owner) : IActor
        {
            public string Id { get; } = id;
            public IAgent Agent { get; } = null!;

            public Task ActivateAsync(CancellationToken ct = default)
            {
                ct.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }

            public Task DeactivateAsync(CancellationToken ct = default)
            {
                ct.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }

            public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
            {
                ct.ThrowIfCancellationRequested();
                if (envelope.Payload?.Is(ChatRequestEvent.Descriptor) == true)
                {
                    if (owner.FailChatRequestsRemaining > 0)
                    {
                        owner.FailChatRequestsRemaining--;
                        throw new InvalidOperationException("simulated chat dispatch failure");
                    }

                    owner.ChatRequests.Add(envelope.Payload.Unpack<ChatRequestEvent>());
                }
                return Task.CompletedTask;
            }

            public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);

            public Task<IReadOnlyList<string>> GetChildrenIdsAsync() =>
                Task.FromResult<IReadOnlyList<string>>([]);
        }
    }

    private sealed class RecordingStreamProvider : IStreamProvider
    {
        private readonly Dictionary<string, RecordingStream> _streams = new(StringComparer.Ordinal);

        public IStream GetStream(string actorId) => GetRecordingStream(actorId);

        public RecordingStream GetRecordingStream(string actorId)
        {
            if (_streams.TryGetValue(actorId, out var stream))
                return stream;

            stream = new RecordingStream(actorId);
            _streams[actorId] = stream;
            return stream;
        }
    }

    private sealed class RecordingStream(string streamId) : IStream
    {
        public string StreamId { get; } = streamId;
        public List<StreamForwardingBinding> Bindings { get; } = [];
        public List<string> RemovedTargets { get; } = [];

        public Task ProduceAsync<T>(T message, CancellationToken ct = default) where T : IMessage
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<IAsyncDisposable> SubscribeAsync<T>(Func<T, Task> handler, CancellationToken ct = default)
            where T : IMessage, new()
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IAsyncDisposable>(new AsyncDisposableStub());
        }

        public Task UpsertRelayAsync(StreamForwardingBinding binding, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Bindings.Add(binding);
            return Task.CompletedTask;
        }

        public Task RemoveRelayAsync(string targetStreamId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            RemovedTargets.Add(targetStreamId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<StreamForwardingBinding>> ListRelaysAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<StreamForwardingBinding>>(Bindings);
        }
    }

    private sealed class RecordingCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        public List<RuntimeCallbackTimeoutRequest> TimeoutRequests { get; } = [];
        public List<RuntimeCallbackLease> Canceled { get; } = [];

        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(RuntimeCallbackTimeoutRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            TimeoutRequests.Add(request);
            return Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                TimeoutRequests.Count,
                RuntimeCallbackBackend.InMemory));
        }

        public Task<RuntimeCallbackLease> ScheduleTimerAsync(RuntimeCallbackTimerRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            throw new NotSupportedException();
        }

        public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Canceled.Add(lease);
            return Task.CompletedTask;
        }

        public Task PurgeActorAsync(string actorId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingPlatformAdapter(string platform) : IPlatformAdapter
    {
        public string Platform { get; } = platform;
        public List<ReplyRecord> Replies { get; } = [];

        public Task<IResult?> TryHandleVerificationAsync(HttpContext http, ChannelBotRegistrationEntry registration) =>
            Task.FromResult<IResult?>(null);

        public Task<InboundMessage?> ParseInboundAsync(HttpContext http, ChannelBotRegistrationEntry registration) =>
            Task.FromResult<InboundMessage?>(null);

        public Task<PlatformReplyDeliveryResult> SendReplyAsync(
            string replyText,
            InboundMessage inbound,
            ChannelBotRegistrationEntry registration,
            NyxIdApiClient nyxClient,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Replies.Add(new ReplyRecord(replyText, inbound, registration));
            return Task.FromResult(new PlatformReplyDeliveryResult(true, "ok"));
        }
    }

    private sealed record ReplyRecord(
        string ReplyText,
        InboundMessage Inbound,
        ChannelBotRegistrationEntry Registration);

    private sealed class AsyncDisposableStub : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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
                throw new InvalidOperationException(
                    $"Optimistic concurrency conflict: expected {expectedVersion}, actual {currentVersion}");

            var appended = events.Select(x => x.Clone()).ToList();
            stream.AddRange(appended);
            var latest = stream.Count == 0 ? 0 : stream[^1].Version;
            return Task.FromResult(new EventStoreCommitResult
            {
                AgentId = agentId,
                LatestVersion = latest,
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
