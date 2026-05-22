using System.Runtime.CompilerServices;
using Aevatar.AI.Abstractions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.NyxidChat;
using Aevatar.Presentation.AGUI;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using AiTextMessageContentEvent = Aevatar.AI.Abstractions.TextMessageContentEvent;
using AiTextMessageEndEvent = Aevatar.AI.Abstractions.TextMessageEndEvent;
using AiTextMessageStartEvent = Aevatar.AI.Abstractions.TextMessageStartEvent;

namespace Aevatar.AI.Tests;

// Test-add (test-coverage/pr-678/cluster-004):
//   Covers refactor-introduced behavior in NyxIdChatProjectionSession.
//   Cluster intent: Agent SSE streams observe typed AGUI projection sessions instead of endpoint-local raw subscriptions.
public sealed class NyxIdChatProjectionSessionTests
{
    [Fact]
    public async Task ProjectionPort_ShouldStartAttachDetachAndReleaseChatSession()
    {
        var activation = new RecordingActivationService();
        var release = new RecordingReleaseService();
        var hub = new RecordingSessionEventHub();
        var port = new NyxIdChatSessionProjectionPort(activation, release, hub, new RecordingActorRuntime());
        var sink = new RecordingEventSink();

        var lease = await port.EnsureChatProjectionAsync("chat-actor-1", "session-1", CancellationToken.None);
        var liveSinkLease = await port.AttachLiveSinkAsync(lease!, sink, CancellationToken.None);
        await hub.Handler!(new AGUIEvent
        {
            TextMessageContent = new Aevatar.Presentation.AGUI.TextMessageContentEvent
            {
                MessageId = "session-1",
                Delta = "hello",
            },
        });
        await port.DetachLiveSinkAsync(liveSinkLease, CancellationToken.None);
        await port.ReleaseActorProjectionAsync(lease!, CancellationToken.None);

        var request = activation.Requests.Should().ContainSingle().Subject;
        request.RootActorId.Should().Be("chat-actor-1");
        request.SessionId.Should().Be("session-1");
        request.ProjectionKind.Should().Be("nyxid-chat-session");
        request.Mode.Should().Be(ProjectionRuntimeMode.SessionObservation);

        var runtimeLease = lease.Should().BeOfType<NyxIdChatSessionRuntimeLease>().Subject;
        runtimeLease.ActorId.Should().Be("chat-actor-1");
        runtimeLease.RootEntityId.Should().Be("chat-actor-1");
        runtimeLease.ScopeId.Should().Be("chat-actor-1");
        runtimeLease.SessionId.Should().Be("session-1");
        runtimeLease.Context.Should().BeSameAs(activation.LeaseToReturn.Context);

        hub.SubscribeCalls.Should().Be(1);
        hub.LastScopeId.Should().Be("chat-actor-1");
        hub.LastSessionId.Should().Be("session-1");
        sink.Events.Should().ContainSingle().Which.TextMessageContent.Delta.Should().Be("hello");
        hub.DisposedSubscriptions.Should().Be(1);
        release.Leases.Should().ContainSingle().Which.Should().BeSameAs(lease);
    }

    [Fact]
    public async Task AttachExistingChatProjectionAsync_ShouldAttachOnlyWhenProjectionSessionExists()
    {
        var runtime = new RecordingActorRuntime();
        runtime.MarkExists("projection.session.scope:nyxid-chat-session:chat-actor-1:session-1");
        var hub = new RecordingSessionEventHub();
        var port = new NyxIdChatSessionProjectionPort(
            new RecordingActivationService(),
            new RecordingReleaseService(),
            hub,
            runtime);
        var sink = new RecordingEventSink();

        var attachment = await port.AttachExistingChatProjectionAsync(
            "chat-actor-1",
            "session-1",
            sink,
            CancellationToken.None);

        attachment.Should().NotBeNull();
        attachment!.ProjectionLease.ActorId.Should().Be("chat-actor-1");
        attachment.ProjectionLease.SessionId.Should().Be("session-1");
        hub.SubscribeCalls.Should().Be(1);
        hub.LastScopeId.Should().Be("chat-actor-1");
        hub.LastSessionId.Should().Be("session-1");
        runtime.ExistsCalls.Should().ContainSingle()
            .Which.Should().Be("projection.session.scope:nyxid-chat-session:chat-actor-1:session-1");
    }

    [Fact]
    public async Task AttachExistingChatProjectionAsync_ShouldReturnNull_WhenProjectionSessionIsCold()
    {
        var runtime = new RecordingActorRuntime();
        var hub = new RecordingSessionEventHub();
        var port = new NyxIdChatSessionProjectionPort(
            new RecordingActivationService(),
            new RecordingReleaseService(),
            hub,
            runtime);

        var attachment = await port.AttachExistingChatProjectionAsync(
            "chat-actor-1",
            "session-1",
            new RecordingEventSink(),
            CancellationToken.None);

        attachment.Should().BeNull();
        hub.SubscribeCalls.Should().Be(0);
        runtime.ExistsCalls.Should().ContainSingle()
            .Which.Should().Be("projection.session.scope:nyxid-chat-session:chat-actor-1:session-1");
    }

    [Fact]
    public void SessionEventCodec_ShouldValidateEventTypeAndPayload()
    {
        var codec = new NyxIdChatSessionEventCodec();
        var evt = new AGUIEvent
        {
            RunFinished = new RunFinishedEvent
            {
                ThreadId = "chat-actor-1",
                RunId = "session-1",
            },
        };

        var payload = codec.Serialize(evt);

        codec.Channel.Should().Be("nyxid-chat-session");
        codec.GetEventType(evt).Should().Be(AGUIEvent.EventOneofCase.RunFinished.ToString());
        codec.GetEventType(new AGUIEvent()).Should().Be(AGUIEvent.Descriptor.FullName);
        codec.Deserialize(codec.GetEventType(evt), payload).Should().BeEquivalentTo(evt);
        codec.Deserialize(AGUIEvent.EventOneofCase.RunError.ToString(), payload).Should().BeNull();
        codec.Deserialize("", payload).Should().BeNull();
        codec.Deserialize(codec.GetEventType(evt), ByteString.Empty).Should().BeNull();
        codec.Deserialize(codec.GetEventType(evt), ByteString.CopyFromUtf8("not-a-proto")).Should().BeNull();
    }

    [Fact]
    public async Task Projector_ShouldFillMessageDefaultsAndEmitTerminalFrame()
    {
        var hub = new RecordingSessionEventHub();
        var projector = new NyxIdChatSessionEventProjector(hub);
        var context = new NyxIdChatSessionProjectionContext
        {
            RootActorId = "chat-actor-1",
            SessionId = "session-1",
            ProjectionKind = "nyxid-chat-session",
        };

        await projector.ProjectAsync(
            context,
            new EventEnvelope
            {
                Payload = Any.Pack(new AiTextMessageStartEvent()),
            },
            CancellationToken.None);
        await projector.ProjectAsync(
            context,
            new EventEnvelope
            {
                Payload = Any.Pack(new AiTextMessageContentEvent { Delta = "delta" }),
            },
            CancellationToken.None);
        await projector.ProjectAsync(
            context,
            new EventEnvelope
            {
                Payload = Any.Pack(new AiTextMessageEndEvent { Content = "done" }),
            },
            CancellationToken.None);

        hub.Published.Should().HaveCount(4);
        hub.Published.Should().OnlyContain(p => p.ScopeId == "chat-actor-1" && p.SessionId == "session-1");
        hub.Published[0].Event.TextMessageStart.MessageId.Should().Be("session-1");
        hub.Published[1].Event.TextMessageContent.MessageId.Should().Be("session-1");
        hub.Published[1].Event.TextMessageContent.Delta.Should().Be("delta");
        hub.Published[2].Event.TextMessageEnd.MessageId.Should().Be("session-1");
        hub.Published[3].Event.RunFinished.ThreadId.Should().Be("chat-actor-1");
        hub.Published[3].Event.RunFinished.RunId.Should().Be("session-1");
    }

    [Fact]
    public async Task Projector_ShouldIgnoreInvalidContextAndUnmappedEnvelope()
    {
        var hub = new RecordingSessionEventHub();
        var projector = new NyxIdChatSessionEventProjector(hub);

        await projector.ProjectAsync(
            new NyxIdChatSessionProjectionContext
            {
                RootActorId = "",
                SessionId = "session-1",
                ProjectionKind = "nyxid-chat-session",
            },
            new EventEnvelope
            {
                Payload = Any.Pack(new AiTextMessageContentEvent { Delta = "ignored" }),
            },
            CancellationToken.None);
        await projector.ProjectAsync(
            new NyxIdChatSessionProjectionContext
            {
                RootActorId = "chat-actor-1",
                SessionId = "session-1",
                ProjectionKind = "nyxid-chat-session",
            },
            new EventEnvelope
            {
                Payload = Any.Pack(new StringValue { Value = "unknown" }),
            },
            CancellationToken.None);

        hub.Published.Should().BeEmpty();
    }

    private sealed class RecordingActivationService : IProjectionScopeActivationService<NyxIdChatSessionRuntimeLease>
    {
        public List<ProjectionScopeStartRequest> Requests { get; } = [];

        public NyxIdChatSessionRuntimeLease LeaseToReturn { get; } = new(new NyxIdChatSessionProjectionContext
        {
            RootActorId = "chat-actor-1",
            ProjectionKind = "nyxid-chat-session",
            SessionId = "session-1",
        });

        public Task<NyxIdChatSessionRuntimeLease> EnsureAsync(
            ProjectionScopeStartRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(LeaseToReturn);
        }
    }

    private sealed class RecordingReleaseService : IProjectionScopeReleaseService<NyxIdChatSessionRuntimeLease>
    {
        public List<NyxIdChatSessionRuntimeLease> Leases { get; } = [];

        public Task ReleaseIfIdleAsync(NyxIdChatSessionRuntimeLease lease, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Leases.Add(lease);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingActorRuntime : IActorRuntime
    {
        private readonly HashSet<string> _existingActors = new(StringComparer.Ordinal);

        public List<string> ExistsCalls { get; } = [];

        public void MarkExists(string actorId) => _existingActors.Add(actorId);

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent =>
            throw new NotSupportedException();

        public Task<IActor> CreateAsync(System.Type agentType, string? id = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DestroyAsync(string id, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IActor?> GetAsync(string id) =>
            throw new NotSupportedException();

        public Task<bool> ExistsAsync(string id)
        {
            ExistsCalls.Add(id);
            return Task.FromResult(_existingActors.Contains(id));
        }

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task UnlinkAsync(string childId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingSessionEventHub : IProjectionSessionEventHub<AGUIEvent>
    {
        public List<(string ScopeId, string SessionId, AGUIEvent Event)> Published { get; } = [];
        public int SubscribeCalls { get; private set; }
        public int DisposedSubscriptions { get; private set; }
        public string? LastScopeId { get; private set; }
        public string? LastSessionId { get; private set; }
        public Func<AGUIEvent, ValueTask>? Handler { get; private set; }

        public Task PublishAsync(string scopeId, string sessionId, AGUIEvent evt, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Published.Add((scopeId, sessionId, evt));
            return Task.CompletedTask;
        }

        public Task<IAsyncDisposable> SubscribeAsync(
            string scopeId,
            string sessionId,
            Func<AGUIEvent, ValueTask> handler,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            SubscribeCalls++;
            LastScopeId = scopeId;
            LastSessionId = sessionId;
            Handler = handler;
            return Task.FromResult<IAsyncDisposable>(new DelegateAsyncDisposable(() => DisposedSubscriptions++));
        }
    }

    private sealed class RecordingEventSink : IEventSink<AGUIEvent>
    {
        public List<AGUIEvent> Events { get; } = [];

        public void Push(AGUIEvent evt) => Events.Add(evt);

        public ValueTask PushAsync(AGUIEvent evt, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Events.Add(evt);
            return ValueTask.CompletedTask;
        }

        public void Complete()
        {
        }

        public async IAsyncEnumerable<AGUIEvent> ReadAllAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class DelegateAsyncDisposable(Action onDispose) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            onDispose();
            return ValueTask.CompletedTask;
        }
    }
}
