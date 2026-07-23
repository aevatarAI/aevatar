using System.Runtime.CompilerServices;
using Aevatar.AI.Abstractions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.NyxidChat;
using Aevatar.AGUI.Contracts;
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
    public async Task ProjectionPort_ShouldAttachExistingDetachAndReleaseChatSession()
    {
        var release = new RecordingReleaseService();
        var hub = new RecordingSessionEventHub();
        var runtime = new RecordingActorRuntime();
        runtime.MarkExists("projection.session.scope:nyxid-chat-session:chat-actor-1:session-1");
        var port = new NyxIdChatSessionProjectionPort(release, hub, CreateAttachExistingLookup(runtime));
        var sink = new RecordingEventSink();

        var attachment = await port.AttachExistingChatProjectionAsync("chat-actor-1", "session-1", sink, CancellationToken.None);
        await hub.Handler!(new AGUIEvent
        {
            TextMessageContent = new Aevatar.AGUI.Contracts.TextMessageContentEvent
            {
                MessageId = "session-1",
                Delta = "hello",
            },
        });
        await port.DetachLiveSinkAsync(attachment!.LiveSinkLease, CancellationToken.None);
        await port.ReleaseActorProjectionAsync(attachment.ProjectionLease, CancellationToken.None);
        var runtimeLease = attachment.ProjectionLease.Should().BeOfType<NyxIdChatSessionRuntimeLease>().Subject;
        runtimeLease.ActorId.Should().Be("chat-actor-1");
        runtimeLease.RootEntityId.Should().Be("chat-actor-1");
        runtimeLease.Context.RootActorId.Should().Be("chat-actor-1");
        runtimeLease.SessionId.Should().Be("session-1");

        hub.SubscribeCalls.Should().Be(1);
        hub.LastRootActorId.Should().Be("chat-actor-1");
        hub.LastSessionId.Should().Be("session-1");
        sink.Events.Should().ContainSingle().Which.TextMessageContent.Delta.Should().Be("hello");
        hub.DisposedSubscriptions.Should().Be(1);
        release.Leases.Should().ContainSingle().Which.Should().BeSameAs(attachment.ProjectionLease);
    }

    [Fact]
    public void ProjectionPort_ShouldNotExposePublicEnsureProjectionApi()
    {
        typeof(INyxIdChatSessionProjectionPort)
            .GetMethods()
            .Select(method => method.Name)
            .Should()
            .NotContain(name => name.StartsWith("Ensure", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AttachExistingChatProjectionAsync_ShouldAttachOnlyWhenProjectionSessionExists()
    {
        var runtime = new RecordingActorRuntime();
        runtime.MarkExists("projection.session.scope:nyxid-chat-session:chat-actor-1:session-1");
        var hub = new RecordingSessionEventHub();
        var port = new NyxIdChatSessionProjectionPort(
            new RecordingReleaseService(),
            hub,
            CreateAttachExistingLookup(runtime));
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
        hub.LastRootActorId.Should().Be("chat-actor-1");
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
            new RecordingReleaseService(),
            hub,
            CreateAttachExistingLookup(runtime));

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
    public async Task Projector_ShouldIgnoreBareTransientTextEvents()
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
                Payload = Any.Pack(new ChatTokenUsageEvent
                {
                    SessionId = "session-1",
                    Usage = new TokenUsagePayload
                    {
                        PromptTokens = 2,
                        CompletionTokens = 4,
                        TotalTokens = 6,
                    },
                    Model = "nyxid-model",
                }),
            },
            CancellationToken.None);
        await projector.ProjectAsync(
            context,
            new EventEnvelope
            {
                Payload = Any.Pack(new AiTextMessageEndEvent { Content = "done" }),
            },
            CancellationToken.None);

        hub.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task Projector_ShouldEmitTerminalFramesFromCommittedCompletion()
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
            CommittedEnvelope(
                context.RootActorId,
                new RoleChatSessionCompletedEvent
                {
                    SessionId = "session-1",
                    Content = "done",
                    ContentEmitted = false,
                    Usage = new TokenUsagePayload
                    {
                        PromptTokens = 2,
                        CompletionTokens = 4,
                        TotalTokens = 6,
                    },
                    Model = "nyxid-model",
                }),
            CancellationToken.None);

        hub.Published.Should().HaveCount(5);
        hub.Published.Should().OnlyContain(p => p.RootActorId == "chat-actor-1" && p.SessionId == "session-1");
        hub.Published[0].Event.TextMessageStart.MessageId.Should().Be("session-1");
        hub.Published[0].Event.TextMessageStart.Role.Should().Be("assistant");
        hub.Published[1].Event.TextMessageContent.MessageId.Should().Be("session-1");
        hub.Published[1].Event.TextMessageContent.Delta.Should().Be("done");
        hub.Published[2].Event.Usage.Should().NotBeNull();
        hub.Published[2].Event.Usage.Available.Should().BeTrue();
        hub.Published[2].Event.Usage.TotalTokens.Should().Be(6);
        hub.Published[2].Event.Usage.Model.Should().Be("nyxid-model");
        hub.Published[3].Event.TextMessageEnd.MessageId.Should().Be("session-1");
        hub.Published[4].Event.RunFinished.ThreadId.Should().Be("chat-actor-1");
        hub.Published[4].Event.RunFinished.RunId.Should().Be("session-1");
        hub.Published[4].Event.RunFinished.Result.Unpack<StringValue>().Value.Should().Be("done");
    }

    [Fact]
    public async Task Projector_ShouldEmitToolFramesFromCommittedCompletion()
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
            CommittedEnvelope(
                context.RootActorId,
                new RoleChatSessionCompletedEvent
                {
                    SessionId = "session-1",
                    Content = "done",
                    ToolCalls =
                    {
                        new ToolCallEvent
                        {
                            ToolName = "nyxid_proxy",
                            ArgumentsJson = "{\"path\":\"/v1/test\"}",
                            CallId = "call-1",
                        },
                    },
                    ToolReceipts =
                    {
                        new AgentToolReceipt
                        {
                            ToolName = "nyxid_proxy",
                            CallId = "call-1",
                            Status = AgentToolReceiptStatus.Success,
                            ResultJson = "{\"ok\":true}",
                        },
                    },
                }),
            CancellationToken.None);

        hub.Published.Select(p => p.Event.EventCase).Should().Equal(
            AGUIEvent.EventOneofCase.ToolCallStart,
            AGUIEvent.EventOneofCase.ToolCallEnd,
            AGUIEvent.EventOneofCase.TextMessageStart,
            AGUIEvent.EventOneofCase.TextMessageContent,
            AGUIEvent.EventOneofCase.TextMessageEnd,
            AGUIEvent.EventOneofCase.RunFinished);
        hub.Published[0].Event.ToolCallStart.ToolName.Should().Be("nyxid_proxy");
        hub.Published[0].Event.ToolCallStart.ToolCallId.Should().Be("call-1");
        hub.Published[1].Event.ToolCallEnd.ToolCallId.Should().Be("call-1");
        hub.Published[1].Event.ToolCallEnd.Result.Should().Be("{\"ok\":true}");
    }

    [Fact]
    public async Task Projector_ShouldEmitApprovalFrameFromCommittedPendingApproval()
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
            CommittedEnvelope(
                context.RootActorId,
                new PendingToolApprovalPersistedEvent
                {
                    Pending = new PendingToolApprovalState
                    {
                        RequestId = "approval-1",
                        SessionId = "session-1",
                        ToolName = "shell",
                        ToolCallId = "call-approval",
                        ArgumentsJson = "{\"cmd\":\"pwd\"}",
                        IsDestructive = true,
                    },
                }),
            CancellationToken.None);

        var published = hub.Published.Should().ContainSingle().Subject;
        published.RootActorId.Should().Be("chat-actor-1");
        published.SessionId.Should().Be("session-1");
        published.Event.EventCase.Should().Be(AGUIEvent.EventOneofCase.Custom);
        published.Event.Custom.Name.Should().Be("TOOL_APPROVAL_REQUEST");
        published.Event.Custom.Payload.Is(Struct.Descriptor).Should().BeTrue();
        var fields = published.Event.Custom.Payload.Unpack<Struct>().Fields;
        fields["requestId"].StringValue.Should().Be("approval-1");
        fields["turnId"].StringValue.Should().Be("session-1");
        fields.Should().NotContainKey("sessionId");
        fields["toolName"].StringValue.Should().Be("shell");
        fields["toolCallId"].StringValue.Should().Be("call-approval");
        fields["argumentsJson"].StringValue.Should().Be("{\"cmd\":\"pwd\"}");
        fields["isDestructive"].BoolValue.Should().BeTrue();
    }

    [Fact]
    public async Task Projector_ShouldIgnorePendingAndCompletionFactsFromDifferentTurn()
    {
        var hub = new RecordingSessionEventHub();
        var projector = new NyxIdChatSessionEventProjector(hub);
        var context = new NyxIdChatSessionProjectionContext
        {
            RootActorId = "chat-actor-1",
            SessionId = "turn-a",
            ProjectionKind = "nyxid-chat-session",
        };

        await projector.ProjectAsync(
            context,
            CommittedEnvelope(
                context.RootActorId,
                new PendingToolApprovalPersistedEvent
                {
                    Pending = new PendingToolApprovalState
                    {
                        RequestId = "approval-b",
                        SessionId = "turn-b",
                        ToolName = "shell",
                        ToolCallId = "call-b",
                    },
                }),
            CancellationToken.None);
        await projector.ProjectAsync(
            context,
            CommittedEnvelope(
                context.RootActorId,
                new RoleChatSessionCompletedEvent
                {
                    SessionId = "turn-b",
                    Content = "other turn",
                    Outcome = RoleChatSessionOutcome.Completed,
                }),
            CancellationToken.None);

        hub.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task Projector_ShouldSynthesizeContent_WhenActorAlreadyEmittedTransientFrames()
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
            CommittedEnvelope(
                context.RootActorId,
                new RoleChatSessionCompletedEvent
                {
                    SessionId = "session-1",
                    Content = "done",
                    ContentEmitted = true,
                }),
            CancellationToken.None);

        hub.Published.Select(p => p.Event.EventCase).Should().Equal(
            AGUIEvent.EventOneofCase.TextMessageStart,
            AGUIEvent.EventOneofCase.TextMessageContent,
            AGUIEvent.EventOneofCase.TextMessageEnd,
            AGUIEvent.EventOneofCase.RunFinished);
        hub.Published[1].Event.TextMessageContent.Delta.Should().Be("done");
    }

    [Fact]
    public async Task Projector_ShouldEmitTerminalForEachRepeatedCommittedCompletionFact()
    {
        var hub = new RecordingSessionEventHub();
        var projector = new NyxIdChatSessionEventProjector(hub);
        var context = new NyxIdChatSessionProjectionContext
        {
            RootActorId = "chat-actor-1",
            SessionId = "turn-idempotent",
            ProjectionKind = "nyxid-chat-session",
        };
        var completed = new RoleChatSessionCompletedEvent
        {
            SessionId = context.SessionId,
            Prompt = "same prompt",
            Content = "cached answer",
            Outcome = RoleChatSessionOutcome.Completed,
        };

        await projector.ProjectAsync(
            context,
            CommittedEnvelope(context.RootActorId, completed),
            CancellationToken.None);
        await projector.ProjectAsync(
            context,
            CommittedEnvelope(context.RootActorId, completed),
            CancellationToken.None);

        hub.Published
            .Where(static entry => entry.Event.EventCase == AGUIEvent.EventOneofCase.RunFinished)
            .Should()
            .HaveCount(2)
            .And.OnlyContain(entry => entry.Event.RunFinished.RunId == context.SessionId);
    }

    [Fact]
    public async Task Projector_ShouldEmitRunErrorFromCommittedFailure()
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
            CommittedEnvelope(
                context.RootActorId,
                new RoleChatSessionCompletedEvent
                {
                    SessionId = "session-1",
                    Content = "LLM request failed [tools=none]: upstream unavailable",
                }),
            CancellationToken.None);

        hub.Published.Should().ContainSingle();
        hub.Published[0].Event.RunError.Message.Should().Be("upstream unavailable");
        hub.Published[0].Event.RunError.RunId.Should().Be("session-1");
    }

    [Fact]
    public async Task Projector_ShouldEmitRunErrorFromCommittedLlmErrorPrefix()
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
            CommittedEnvelope(
                context.RootActorId,
                new RoleChatSessionCompletedEvent
                {
                    SessionId = "session-1",
                    Content = "[[AEVATAR_LLM_ERROR]] provider exploded",
                }),
            CancellationToken.None);

        var published = hub.Published.Should().ContainSingle().Which;
        published.Event.EventCase.Should().Be(AGUIEvent.EventOneofCase.RunError);
        published.Event.RunError.Message.Should().Be("provider exploded");
        published.Event.RunError.RunId.Should().Be("session-1");
    }

    [Fact]
    public async Task Projector_ShouldEmitTypedTerminalFromCommittedTurnConflict()
    {
        var hub = new RecordingSessionEventHub();
        var projector = new NyxIdChatSessionEventProjector(hub);
        var context = new NyxIdChatSessionProjectionContext
        {
            RootActorId = "chat-actor-1",
            SessionId = "turn-client-request-1",
            ProjectionKind = "nyxid-chat-session",
        };

        await projector.ProjectAsync(
            context,
            CommittedEnvelope(
                context.RootActorId,
                new RoleChatSessionConflictEvent
                {
                    SessionId = context.SessionId,
                    Reason = RoleChatSessionConflictReason.PromptMismatch,
                    SafeMessage = "This client request id was already used for different input.",
                }),
            CancellationToken.None);

        var terminal = hub.Published.Should().ContainSingle().Which.Event;
        terminal.EventCase.Should().Be(AGUIEvent.EventOneofCase.RunError);
        terminal.RunError.RunId.Should().Be(context.SessionId);
        terminal.RunError.Code.Should().Be("IDEMPOTENCY_CONFLICT");
        terminal.RunError.Message.Should().Be("This client request id was already used for different input.");
    }

    [Fact]
    public async Task Projector_ShouldEmitAuthorizationCustomThenBlockedTerminal()
    {
        var hub = new RecordingSessionEventHub();
        var projector = new NyxIdChatSessionEventProjector(hub);
        var context = new NyxIdChatSessionProjectionContext
        {
            RootActorId = "chat-actor-1",
            SessionId = "turn-blocked",
            ProjectionKind = "nyxid-chat-session",
        };
        var blocker = new NyxIdAuthorizationRequiredEvent
        {
            ServiceSlug = "api-github",
            ServiceLabel = "GitHub",
            ResourceUri = "/repos/private",
            ReasonCode = "NYXID_FORBIDDEN",
            SafeMessage = "Connect or reauthorize api-github to continue.",
        };

        await projector.ProjectAsync(
            context,
            CommittedEnvelope(
                context.RootActorId,
                new RoleChatSessionCompletedEvent
                {
                    SessionId = context.SessionId,
                    Outcome = RoleChatSessionOutcome.Blocked,
                    AuthorizationRequired = blocker,
                }),
            CancellationToken.None);

        hub.Published.Select(entry => entry.Event.EventCase).Should().Equal(
            AGUIEvent.EventOneofCase.Custom,
            AGUIEvent.EventOneofCase.RunFinished);
        hub.Published[0].Event.Custom.Name.Should().Be("nyxid.authorization.required");
        hub.Published[0].Event.Custom.Payload.Unpack<NyxIdAuthorizationRequiredEvent>()
            .Should().BeEquivalentTo(blocker);
        hub.Published[1].Event.RunFinished.RunId.Should().Be(context.SessionId);
        hub.Published[1].Event.RunFinished.Status.Should().Be(RunCompletionStatus.Blocked);
        hub.Published[1].Event.RunFinished.Result.Unpack<NyxIdAuthorizationRequiredEvent>()
            .Should().BeEquivalentTo(blocker);
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

    private static EventEnvelope CommittedEnvelope(string actorId, IMessage evt) => new()
    {
        Payload = Any.Pack(new CommittedStateEventPublished
        {
            StateEvent = new StateEvent
            {
                EventId = Guid.NewGuid().ToString("N"),
                Version = 1,
                EventData = Any.Pack(evt),
                Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            },
            StateRoot = Any.Pack(new RoleGAgentState()),
        }),
        Route = EnvelopeRouteSemantics.CreateObserverPublication(actorId),
        Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
    };

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

    private static IProjectionScopeAttachExistingLeaseLookup<NyxIdChatSessionRuntimeLease> CreateAttachExistingLookup(
        IActorRuntime runtime) =>
        new ProjectionScopeAttachExistingLeaseLookup<NyxIdChatSessionRuntimeLease, NyxIdChatSessionProjectionContext>(
            runtime,
            request => new NyxIdChatSessionProjectionContext
            {
                RootActorId = request.RootActorId,
                ProjectionKind = request.ProjectionKind,
                SessionId = request.SessionId,
            },
            (_, context) => new NyxIdChatSessionRuntimeLease(context));

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
        public List<(string RootActorId, string SessionId, AGUIEvent Event)> Published { get; } = [];
        public int SubscribeCalls { get; private set; }
        public int DisposedSubscriptions { get; private set; }
        public string? LastRootActorId { get; private set; }
        public string? LastSessionId { get; private set; }
        public Func<AGUIEvent, ValueTask>? Handler { get; private set; }

        public Task PublishAsync(string rootActorId, string sessionId, AGUIEvent evt, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Published.Add((rootActorId, sessionId, evt));
            return Task.CompletedTask;
        }

        public Task<IAsyncDisposable> SubscribeAsync(
            string rootActorId,
            string sessionId,
            Func<AGUIEvent, ValueTask> handler,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            SubscribeCalls++;
            LastRootActorId = rootActorId;
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
