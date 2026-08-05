using System.Runtime.CompilerServices;
using Aevatar.AI.Abstractions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.NyxidChat;
using Aevatar.AGUI.Contracts;
using FluentAssertions;
using Aevatar.Foundation.Abstractions.Tools;
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
    public async Task Projector_ShouldMapCommittedProgressOneToOneWithActorSequence()
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
                new RoleChatSessionProgressedEvent
                {
                    SessionId = context.SessionId,
                    Sequence = 7,
                    TextDelta = new RoleChatTextDeltaProgress { Delta = "early" },
                }),
            CancellationToken.None);

        var frame = hub.Published.Should().ContainSingle().Subject.Event;
        frame.Sequence.Should().Be(7);
        frame.TextMessageContent.MessageId.Should().Be("session-1");
        frame.TextMessageContent.Delta.Should().Be("early");
    }

    [Fact]
    public async Task Projector_ShouldNotExpandNormalCommittedCompletion()
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
                    SessionId = context.SessionId,
                    Content = "must not be resent",
                    Outcome = RoleChatSessionOutcome.Completed,
                }),
            CancellationToken.None);

        hub.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task Projector_ShouldExpandSnapshotOnlyForExplicitReplayProgress()
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
                new RoleChatSessionProgressedEvent
                {
                    SessionId = context.SessionId,
                    Sequence = 12,
                    Replay = new RoleChatReplayProgress
                    {
                        Snapshot = new RoleChatSessionCompletedEvent
                        {
                            SessionId = context.SessionId,
                            Content = "replayed",
                            Outcome = RoleChatSessionOutcome.Completed,
                        },
                    },
                }),
            CancellationToken.None);

        hub.Published.Should().NotBeEmpty();
        hub.Published.Should().OnlyContain(entry => entry.Event.Sequence == 12);
        hub.Published.Should().Contain(entry =>
            entry.Event.EventCase == AGUIEvent.EventOneofCase.TextMessageContent &&
            entry.Event.TextMessageContent.Delta == "replayed");
        hub.Published.Should().Contain(entry => entry.Event.EventCase == AGUIEvent.EventOneofCase.RunFinished);
    }

    [Fact]
    public async Task Projector_ExplicitBlockedReplay_ShouldRestoreRichSnapshotBeforeTerminal()
    {
        var hub = new RecordingSessionEventHub();
        var projector = new NyxIdChatSessionEventProjector(hub);
        var context = new NyxIdChatSessionProjectionContext
        {
            RootActorId = "chat-actor-1",
            SessionId = "turn-blocked-replay",
            ProjectionKind = "nyxid-chat-session",
        };
        var presentation = new ToolPresentationDescriptor
        {
            InvocationName = "nyxid_api-github-work__get_repository",
            DisplayName = "Work GitHub - Get repository",
            Description = "Gets one repository.",
            Kind = ToolPresentationKind.NyxIdOperation,
            Availability = ToolAvailability.Available,
            NyxIdOperation = new NyxIdOperationRef
            {
                ConnectedServiceId = "connected-service-github",
                ServiceSlug = "api-github-work",
                CatalogServiceSlug = "github",
                ConnectionLabel = "Work GitHub",
                ConnectorDisplayName = "GitHub",
                OperationId = "get_repository",
                HttpMethod = "GET",
                PathTemplate = "/repos/{owner}/{repo}",
            },
        };

        await projector.ProjectAsync(
            context,
            CommittedEnvelope(
                context.RootActorId,
                new RoleChatSessionProgressedEvent
                {
                    SessionId = context.SessionId,
                    Sequence = 13,
                    Replay = new RoleChatReplayProgress
                    {
                        Snapshot = new RoleChatSessionCompletedEvent
                        {
                            SessionId = context.SessionId,
                            Content = "partial answer",
                            ReasoningContent = "checking repository access",
                            ToolCalls =
                            {
                                new ToolCallEvent
                                {
                                    CallId = "call-1",
                                    ToolName = presentation.InvocationName,
                                    Presentation = presentation,
                                },
                            },
                            ToolResults =
                            {
                                new ToolResultEvent
                                {
                                    CallId = "call-1",
                                    ResultJson = "{\"authorizationRequired\":true}",
                                    Success = false,
                                    Error = "authorization required",
                                },
                            },
                            OutputParts =
                            {
                                new ChatContentPart
                                {
                                    Kind = ChatContentPartKind.Image,
                                    MediaType = "image/png",
                                    Uri = "nyx://artifact/repository-map",
                                    Name = "repository-map.png",
                                },
                            },
                            Usage = new TokenUsagePayload
                            {
                                PromptTokens = 4,
                                CompletionTokens = 2,
                                TotalTokens = 6,
                            },
                            Model = "nyxid-model",
                            Outcome = RoleChatSessionOutcome.Blocked,
                            AuthorizationRequired = new NyxIdAuthorizationRequiredEvent
                            {
                                ServiceSlug = "api-github-work",
                                ServiceLabel = "Work GitHub",
                                SafeMessage = "Reconnect Work GitHub to continue.",
                            },
                        },
                    },
                }),
            CancellationToken.None);

        hub.Published.Select(entry => entry.Event.EventCase).Should().Equal(
            AGUIEvent.EventOneofCase.ToolCallStart,
            AGUIEvent.EventOneofCase.ToolCallEnd,
            AGUIEvent.EventOneofCase.Custom,
            AGUIEvent.EventOneofCase.Custom,
            AGUIEvent.EventOneofCase.TextMessageStart,
            AGUIEvent.EventOneofCase.TextMessageContent,
            AGUIEvent.EventOneofCase.Usage,
            AGUIEvent.EventOneofCase.TextMessageEnd,
            AGUIEvent.EventOneofCase.Custom,
            AGUIEvent.EventOneofCase.RunFinished);
        hub.Published.Should().OnlyContain(entry => entry.Event.Sequence == 13);
        hub.Published[0].Event.ToolCallStart.Presentation.Should().BeEquivalentTo(presentation);
        hub.Published[2].Event.Custom.Name.Should().Be("aevatar.llm.reasoning");
        hub.Published[2].Event.Custom.Payload.Unpack<RoleChatReasoningDeltaProgress>().Delta.Should()
            .Be("checking repository access");
        hub.Published[3].Event.Custom.Name.Should().Be("MEDIA_CONTENT");
        hub.Published[3].Event.Custom.Payload.Unpack<MediaContentEvent>().Part.Uri.Should()
            .Be("nyx://artifact/repository-map");
        hub.Published[^2].Event.Custom.Name.Should().Be("nyxid.authorization.required");
        hub.Published[^1].Event.RunFinished.Status.Should().Be(RunCompletionStatus.Blocked);
    }

    [Theory]
    [InlineData(RoleChatSessionOutcome.Failed)]
    [InlineData(RoleChatSessionOutcome.OutcomeUncertain)]
    public async Task Projector_ExplicitErrorReplay_ShouldRestoreRichSnapshotBeforeRunError(
        RoleChatSessionOutcome outcome)
    {
        var hub = new RecordingSessionEventHub();
        var projector = new NyxIdChatSessionEventProjector(hub);
        var context = new NyxIdChatSessionProjectionContext
        {
            RootActorId = "chat-actor-1",
            SessionId = "turn-failed-replay",
            ProjectionKind = "nyxid-chat-session",
        };

        await projector.ProjectAsync(
            context,
            CommittedEnvelope(
                context.RootActorId,
                new RoleChatSessionProgressedEvent
                {
                    SessionId = context.SessionId,
                    Sequence = 14,
                    Replay = new RoleChatReplayProgress
                    {
                        Snapshot = new RoleChatSessionCompletedEvent
                        {
                            SessionId = context.SessionId,
                            Content = "partial answer",
                            ReasoningContent = "checking upstream",
                            ToolCalls =
                            {
                                new ToolCallEvent
                                {
                                    CallId = "call-failed",
                                    ToolName = "web_fetch",
                                },
                            },
                            ToolResults =
                            {
                                new ToolResultEvent
                                {
                                    CallId = "call-failed",
                                    Success = false,
                                    Error = "upstream unavailable",
                                },
                            },
                            OutputParts =
                            {
                                new ChatContentPart
                                {
                                    Kind = ChatContentPartKind.Image,
                                    MediaType = "image/png",
                                    Uri = "nyx://artifact/failure-context",
                                },
                            },
                            Usage = new TokenUsagePayload { TotalTokens = 9 },
                            Model = "model-failed",
                            Outcome = outcome,
                            FailureCode = "PROVIDER_FAILURE",
                            SafeMessage = "The provider is unavailable.",
                        },
                    },
                }),
            CancellationToken.None);

        hub.Published.Select(entry => entry.Event.EventCase).Should().Equal(
            AGUIEvent.EventOneofCase.ToolCallStart,
            AGUIEvent.EventOneofCase.ToolCallEnd,
            AGUIEvent.EventOneofCase.Custom,
            AGUIEvent.EventOneofCase.Custom,
            AGUIEvent.EventOneofCase.TextMessageStart,
            AGUIEvent.EventOneofCase.TextMessageContent,
            AGUIEvent.EventOneofCase.Usage,
            AGUIEvent.EventOneofCase.TextMessageEnd,
            AGUIEvent.EventOneofCase.RunError);
        hub.Published.Should().OnlyContain(entry => entry.Event.Sequence == 14);
        hub.Published[^1].Event.RunError.Code.Should().Be("PROVIDER_FAILURE");
        hub.Published[^1].Event.RunError.Message.Should().Be("The provider is unavailable.");
    }

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
    public async Task ProjectionPort_ShouldFenceDuplicateAndStaleSequencedDeliveryPerAttachment()
    {
        var hub = new RecordingSessionEventHub();
        var runtime = new RecordingActorRuntime();
        runtime.MarkExists("projection.session.scope:nyxid-chat-session:chat-actor-1:session-1");
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

        var firstReplayFrame = new AGUIEvent
        {
            Sequence = 7,
            TextMessageContent = new Aevatar.AGUI.Contracts.TextMessageContentEvent
            {
                MessageId = "session-1",
                Delta = "replayed text",
            },
        };
        var secondReplayFrame = new AGUIEvent
        {
            Sequence = 7,
            Custom = new CustomEvent
            {
                Name = "aevatar.llm.reasoning",
                Payload = Any.Pack(new RoleChatReasoningDeltaProgress { Delta = "replayed reasoning" }),
            },
        };
        var terminal = new AGUIEvent
        {
            Sequence = 8,
            RunFinished = new RunFinishedEvent
            {
                RunId = "session-1",
                Status = RunCompletionStatus.Completed,
            },
        };

        await hub.Handler!(firstReplayFrame);
        await hub.Handler(firstReplayFrame.Clone());
        await hub.Handler(secondReplayFrame);
        await hub.Handler(terminal);
        await hub.Handler(secondReplayFrame.Clone());
        await hub.Handler(terminal.Clone());

        sink.Events.Should().Equal(firstReplayFrame, secondReplayFrame, terminal);
    }

    [Fact]
    public async Task ProjectionPort_ShouldDeliverContinuationTerminalAcrossCommittedControllerEvents()
    {
        var hub = new RecordingSessionEventHub();
        var runtime = new RecordingActorRuntime();
        runtime.MarkExists("projection.session.scope:nyxid-chat-session:conversation-alpha:turn-alpha");
        var port = new NyxIdChatSessionProjectionPort(
            new RecordingReleaseService(),
            hub,
            CreateAttachExistingLookup(runtime));
        var sink = new RecordingEventSink();
        var projector = new NyxIdChatSessionEventProjector(hub);
        var context = ControllerContext();
        var admission = new NyxIdChatContinuationAdmissionState
        {
            Kind = NyxIdChatContinuationKind.Action,
            RequestId = "command-action-alpha",
            ClientRequestId = "client-action-alpha",
            ContinuationTurnId = context.SessionId,
            Status = NyxIdChatContinuationAdmissionStatus.Accepted,
            ReasonCode = NyxIdChatBrowserActions.ActionContinuationAccepted,
            OwnerSubject = "owner-alpha",
        };
        var active = ControllerState(NyxIdChatTaskStatus.Active, NyxIdChatTurnStatus.Active);
        active.ProgressSequence = 5;
        active.ContinuationAdmission = admission.Clone();

        var attachment = await port.AttachExistingChatProjectionAsync(
            context.RootActorId,
            context.SessionId,
            sink,
            CancellationToken.None);
        attachment.Should().NotBeNull();
        await projector.ProjectAsync(
            context,
            CommittedEnvelope(
                context.RootActorId,
                new NyxIdChatContinuationAdmissionCommittedEvent
                {
                    Admission = admission,
                    State = active,
                },
                stateVersion: 12),
            CancellationToken.None);
        foreach (var published in hub.Published.ToArray())
            await hub.Handler!(published.Event);

        hub.Published.Clear();
        var blocked = active.Clone();
        blocked.ProgressSequence = 6;
        blocked.ActiveTask.Status = NyxIdChatTaskStatus.Blocked;
        blocked.ActiveTask.ActiveOperationId = string.Empty;
        blocked.ActiveTask.FailureCode = "NYXID_ACTION_POSTCONDITION_STALE";
        blocked.ActiveTask.SafeMessage = "The NyxID action postcondition read model is stale.";
        blocked.ActiveTask.Steps[0].Status = NyxIdChatStepStatus.Waiting;
        blocked.ActiveTurn.Status = NyxIdChatTurnStatus.Blocked;
        blocked.ActiveTurn.FailureCode = blocked.ActiveTask.FailureCode;
        blocked.ActiveTurn.SafeMessage = blocked.ActiveTask.SafeMessage;
        blocked.LatestTurn = blocked.ActiveTurn.Clone();
        await projector.ProjectAsync(
            context,
            CommittedEnvelope(
                context.RootActorId,
                ControllerReconciled(blocked),
                stateVersion: 15),
            CancellationToken.None);
        foreach (var published in hub.Published.ToArray())
            await hub.Handler!(published.Event);

        sink.Events.Select(static entry => entry.Sequence).Should().Equal(5, 5, 5, 6, 6, 6, 6);
        sink.Events.Should().Contain(entry =>
            entry.EventCase == AGUIEvent.EventOneofCase.RunFinished &&
            entry.RunFinished.RunId == context.SessionId &&
            entry.RunFinished.Status == RunCompletionStatus.Blocked);
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
    public async Task Projector_ShouldMapCommittedTerminalTailProgressInSequence()
    {
        var hub = new RecordingSessionEventHub();
        var projector = new NyxIdChatSessionEventProjector(hub);
        var context = new NyxIdChatSessionProjectionContext
        {
            RootActorId = "chat-actor-1",
            SessionId = "session-1",
            ProjectionKind = "nyxid-chat-session",
        };

        var progress = new RoleChatSessionProgressedEvent[]
        {
            new()
            {
                SessionId = context.SessionId,
                Sequence = 1,
                TextStarted = new RoleChatTextStartedProgress { AgentId = context.RootActorId },
            },
            new()
            {
                SessionId = context.SessionId,
                Sequence = 2,
                TextDelta = new RoleChatTextDeltaProgress { Delta = "done" },
            },
            new()
            {
                SessionId = context.SessionId,
                Sequence = 3,
                Usage = new RoleChatUsageProgress
                {
                    Usage = new TokenUsagePayload
                    {
                        PromptTokens = 2,
                        CompletionTokens = 4,
                        TotalTokens = 6,
                    },
                    Model = "nyxid-model",
                },
            },
            new()
            {
                SessionId = context.SessionId,
                Sequence = 4,
                TextEnded = new RoleChatTextEndedProgress { MessageId = context.SessionId },
            },
            new()
            {
                SessionId = context.SessionId,
                Sequence = 5,
                Terminal = new RoleChatTerminalProgress
                {
                    Outcome = RoleChatSessionOutcome.Completed,
                    FinalContent = "done",
                },
            },
        };

        foreach (var item in progress)
        {
            await projector.ProjectAsync(
                context,
                CommittedEnvelope(context.RootActorId, item),
                CancellationToken.None);
        }

        hub.Published.Should().HaveCount(5);
        hub.Published.Should().OnlyContain(p => p.RootActorId == "chat-actor-1" && p.SessionId == "session-1");
        hub.Published.Select(entry => entry.Event.Sequence).Should().Equal(1, 2, 3, 4, 5);
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
    public async Task Projector_ShouldMapCommittedToolProgressWithDescriptorSnapshot()
    {
        var hub = new RecordingSessionEventHub();
        var projector = new NyxIdChatSessionEventProjector(hub);
        var context = new NyxIdChatSessionProjectionContext
        {
            RootActorId = "chat-actor-1",
            SessionId = "session-1",
            ProjectionKind = "nyxid-chat-session",
        };

        var presentation = new ToolPresentationDescriptor
        {
            InvocationName = "nyxid_proxy",
            DisplayName = "Work GitHub - Get repository",
            Description = "Gets one repository.",
            Kind = ToolPresentationKind.NyxIdOperation,
            Availability = ToolAvailability.Available,
            NyxIdOperation = new NyxIdOperationRef
            {
                ConnectedServiceId = "connected-service-github",
                ServiceSlug = "api-github-work",
                CatalogServiceSlug = "github",
                ConnectionLabel = "Work GitHub",
                ConnectorDisplayName = "GitHub",
                OperationId = "get_repository",
            },
        };
        await projector.ProjectAsync(
            context,
            CommittedEnvelope(
                context.RootActorId,
                new RoleChatSessionProgressedEvent
                {
                    SessionId = context.SessionId,
                    Sequence = 8,
                    ToolStarted = new RoleChatToolStartedProgress
                    {
                        ToolName = "nyxid_proxy",
                        CallId = "call-1",
                        Presentation = presentation,
                    },
                }),
            CancellationToken.None);
        presentation.DisplayName = "Renamed after commit";
        await projector.ProjectAsync(
            context,
            CommittedEnvelope(
                context.RootActorId,
                new RoleChatSessionProgressedEvent
                {
                    SessionId = context.SessionId,
                    Sequence = 9,
                    ToolCompleted = new RoleChatToolCompletedProgress
                    {
                        ToolName = "nyxid_proxy",
                        Result = new ToolResultEvent
                        {
                            CallId = "call-1",
                            ResultJson = "{\"ok\":true}",
                            Success = true,
                        },
                    },
                }),
            CancellationToken.None);

        hub.Published.Select(p => p.Event.EventCase).Should().Equal(
            AGUIEvent.EventOneofCase.ToolCallStart,
            AGUIEvent.EventOneofCase.ToolCallEnd);
        hub.Published.Select(entry => entry.Event.Sequence).Should().Equal(8, 9);
        hub.Published[0].Event.ToolCallStart.ToolName.Should().Be("nyxid_proxy");
        hub.Published[0].Event.ToolCallStart.ToolCallId.Should().Be("call-1");
        hub.Published[0].Event.ToolCallStart.Presentation.DisplayName.Should()
            .Be("Work GitHub - Get repository");
        hub.Published[0].Event.ToolCallStart.Presentation.NyxIdOperation.ConnectedServiceId.Should()
            .Be("connected-service-github");
        hub.Published[1].Event.ToolCallEnd.ToolCallId.Should().Be("call-1");
        hub.Published[1].Event.ToolCallEnd.Result.Should().Be("{\"ok\":true}");
    }

    [Fact]
    public async Task Projector_ShouldEmitApprovalFrameFromCommittedSequencedProgress()
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
                new RoleChatSessionProgressedEvent
                {
                    SessionId = "session-1",
                    Sequence = 10,
                    ToolApprovalRequired = new RoleChatToolApprovalRequiredProgress
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
                    },
                }),
            CancellationToken.None);

        var published = hub.Published.Should().ContainSingle().Subject;
        published.RootActorId.Should().Be("chat-actor-1");
        published.SessionId.Should().Be("session-1");
        published.Event.EventCase.Should().Be(AGUIEvent.EventOneofCase.Custom);
        published.Event.Sequence.Should().Be(10);
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
    public async Task Projector_ShouldIgnoreApprovalProgressAndCompletionFactsFromDifferentTurn()
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
                new RoleChatSessionProgressedEvent
                {
                    SessionId = "turn-b",
                    Sequence = 1,
                    ToolApprovalRequired = new RoleChatToolApprovalRequiredProgress
                    {
                        Pending = new PendingToolApprovalState
                        {
                            RequestId = "approval-b",
                            SessionId = "turn-b",
                            ToolName = "shell",
                            ToolCallId = "call-b",
                        },
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
    public async Task Projector_ShouldNotSynthesizeContentFromNormalCompletion()
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

        hub.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task Projector_ShouldExpandOnlyEmbeddedTerminalTailFromCompletion()
    {
        var hub = new RecordingSessionEventHub();
        var projector = new NyxIdChatSessionEventProjector(hub);
        var context = new NyxIdChatSessionProjectionContext
        {
            RootActorId = "chat-actor-1",
            SessionId = "session-atomic-terminal",
            ProjectionKind = "nyxid-chat-session",
        };
        var completion = new RoleChatSessionCompletedEvent
        {
            SessionId = context.SessionId,
            Content = "snapshot content must not be synthesized",
            ContentEmitted = true,
            Outcome = RoleChatSessionOutcome.Completed,
        };
        completion.TerminalProgress.AddRange(
        [
            new RoleChatSessionProgressedEvent
            {
                SessionId = context.SessionId,
                Sequence = 7,
                Usage = new RoleChatUsageProgress
                {
                    Usage = new TokenUsagePayload { TotalTokens = 13 },
                    Model = "model-a",
                },
            },
            new RoleChatSessionProgressedEvent
            {
                SessionId = context.SessionId,
                Sequence = 8,
                TextEnded = new RoleChatTextEndedProgress { MessageId = context.SessionId },
            },
            new RoleChatSessionProgressedEvent
            {
                SessionId = context.SessionId,
                Sequence = 9,
                Terminal = new RoleChatTerminalProgress
                {
                    Outcome = RoleChatSessionOutcome.Completed,
                    FinalContent = completion.Content,
                },
            },
        ]);

        await projector.ProjectAsync(
            context,
            CommittedEnvelope(context.RootActorId, completion),
            CancellationToken.None);

        hub.Published.Select(entry => entry.Event.EventCase).Should().Equal(
            AGUIEvent.EventOneofCase.Usage,
            AGUIEvent.EventOneofCase.TextMessageEnd,
            AGUIEvent.EventOneofCase.RunFinished);
        hub.Published.Select(entry => entry.Event.Sequence).Should().Equal(7, 8, 9);
        hub.Published.Should().NotContain(entry =>
            entry.Event.EventCase == AGUIEvent.EventOneofCase.TextMessageContent);
    }

    [Fact]
    public async Task Projector_ShouldIgnoreRepeatedNormalCommittedCompletionFacts()
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

        hub.Published.Should().BeEmpty();
    }

    [Theory]
    [InlineData(RoleChatSessionOutcome.Failed)]
    [InlineData(RoleChatSessionOutcome.OutcomeUncertain)]
    public async Task Projector_ShouldEmitRunErrorFromCommittedTerminalProgress(
        RoleChatSessionOutcome outcome)
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
                new RoleChatSessionProgressedEvent
                {
                    SessionId = context.SessionId,
                    Sequence = 21,
                    Terminal = new RoleChatTerminalProgress
                    {
                        Outcome = outcome,
                        FailureCode = "PROVIDER_FAILURE",
                        SafeMessage = "upstream unavailable",
                    },
                }),
            CancellationToken.None);

        hub.Published.Should().ContainSingle();
        hub.Published[0].Event.RunError.Message.Should().Be("upstream unavailable");
        hub.Published[0].Event.RunError.RunId.Should().Be("session-1");
        hub.Published[0].Event.RunError.Code.Should().Be("PROVIDER_FAILURE");
        hub.Published[0].Event.Sequence.Should().Be(21);
    }

    [Fact]
    public async Task Projector_ShouldUseTypedTerminalErrorWithoutParsingFinalContent()
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
                new RoleChatSessionProgressedEvent
                {
                    SessionId = context.SessionId,
                    Sequence = 22,
                    Terminal = new RoleChatTerminalProgress
                    {
                        Outcome = RoleChatSessionOutcome.Failed,
                        FailureCode = "CHAT_REQUEST_FAILED",
                        SafeMessage = "provider exploded",
                        FinalContent = "[[AEVATAR_LLM_ERROR]] must not be parsed",
                    },
                }),
            CancellationToken.None);

        var published = hub.Published.Should().ContainSingle().Which;
        published.Event.EventCase.Should().Be(AGUIEvent.EventOneofCase.RunError);
        published.Event.RunError.Message.Should().Be("provider exploded");
        published.Event.RunError.RunId.Should().Be("session-1");
    }

    [Fact]
    public async Task Projector_ControllerStart_ShouldEmitCommittedTaskAndStepFrames()
    {
        var hub = new RecordingSessionEventHub();
        var projector = new NyxIdChatSessionEventProjector(hub);
        var context = ControllerContext();
        var state = ControllerState(NyxIdChatTaskStatus.Active, NyxIdChatTurnStatus.Active);

        await projector.ProjectAsync(
            context,
            CommittedEnvelope(
                context.RootActorId,
                new NyxIdChatTurnStartedEvent { State = state },
                stateVersion: 11),
            CancellationToken.None);

        hub.Published.Select(entry => entry.Event.EventCase).Should().Equal(
            AGUIEvent.EventOneofCase.Custom,
            AGUIEvent.EventOneofCase.Custom,
            AGUIEvent.EventOneofCase.TextMessageStart);
        hub.Published.Should().OnlyContain(entry => entry.Event.Sequence == 5);
        hub.Published[0].Event.Custom.Name.Should().Be("nyxid.task.snapshot");
        hub.Published[0].Event.Custom.Payload.Unpack<NyxIdChatTaskState>()
            .Should().BeEquivalentTo(state.ActiveTask);
        hub.Published[1].Event.Custom.Name.Should().Be("nyxid.task.step.changed");
        hub.Published[1].Event.Custom.Payload.Unpack<NyxIdChatTaskStepState>()
            .Should().BeEquivalentTo(state.ActiveTask.Steps.Single());
        hub.Published[2].Event.TextMessageStart.MessageId.Should().Be("turn-alpha");
    }

    [Fact]
    public async Task Projector_ControllerProgress_ShouldMapTypedTextReasoningAndToolStart()
    {
        var hub = new RecordingSessionEventHub();
        var projector = new NyxIdChatSessionEventProjector(hub);
        var context = ControllerContext();
        var key = ControllerState(
                NyxIdChatTaskStatus.Active,
                NyxIdChatTurnStatus.Active)
            .ActiveTask.Steps.Single().Operation.Key;
        var progress = new NyxIdChatOperationProgressSignal[]
        {
            new()
            {
                Key = key.Clone(),
                Sequence = 1,
                Text = new NyxIdChatTextProgress { Delta = "visible" },
            },
            new()
            {
                Key = key.Clone(),
                Sequence = 2,
                Reasoning = new NyxIdChatReasoningProgress { Delta = "reasoning" },
            },
            new()
            {
                Key = key.Clone(),
                Sequence = 3,
                ToolStarted = new NyxIdChatToolProgress
                {
                    CallId = "call-alpha",
                    ToolName = "repository_update",
                },
            },
        };

        for (var index = 0; index < progress.Length; index++)
        {
            await projector.ProjectAsync(
                context,
                CommittedEnvelope(
                    context.RootActorId,
                    new NyxIdChatOperationProgressedEvent
                    {
                        Progress = progress[index],
                        ProgressSequence = 6 + index,
                    },
                    stateVersion: 12 + index),
                CancellationToken.None);
        }

        hub.Published.Select(entry => entry.Event.EventCase).Should().Equal(
            AGUIEvent.EventOneofCase.TextMessageContent,
            AGUIEvent.EventOneofCase.Custom,
            AGUIEvent.EventOneofCase.ToolCallStart);
        hub.Published.Select(entry => entry.Event.Sequence).Should().Equal(6, 7, 8);
        hub.Published[0].Event.TextMessageContent.Delta.Should().Be("visible");
        hub.Published[1].Event.Custom.Name.Should().Be("aevatar.llm.reasoning");
        hub.Published[1].Event.Custom.Payload.Unpack<NyxIdChatReasoningProgress>().Delta
            .Should().Be("reasoning");
        hub.Published[2].Event.ToolCallStart.ToolCallId.Should().Be("call-alpha");
        hub.Published[2].Event.ToolCallStart.ToolName.Should().Be("repository_update");
    }

    [Fact]
    public async Task Projector_ControllerFailure_ShouldEmitOneErrorTerminalAndNoFinishedTerminal()
    {
        var hub = new RecordingSessionEventHub();
        var projector = new NyxIdChatSessionEventProjector(hub);
        var context = ControllerContext();
        var state = ControllerState(NyxIdChatTaskStatus.Failed, NyxIdChatTurnStatus.Failed);
        state.ActiveTask.FailureCode = "TOOL_FAILED";
        state.ActiveTask.SafeMessage = "The required tool failed.";
        state.ActiveTurn.FailureCode = state.ActiveTask.FailureCode;
        state.ActiveTurn.SafeMessage = state.ActiveTask.SafeMessage;
        state.ActiveTask.Steps[0].Status = NyxIdChatStepStatus.Failed;
        state.ActiveTask.Steps[0].FailureCode = state.ActiveTask.FailureCode;
        state.ActiveTask.Steps[0].SafeMessage = state.ActiveTask.SafeMessage;

        await projector.ProjectAsync(
            context,
            CommittedEnvelope(
                context.RootActorId,
                ControllerReconciled(state),
                stateVersion: 15),
            CancellationToken.None);

        hub.Published.Count(entry => entry.Event.EventCase == AGUIEvent.EventOneofCase.RunError)
            .Should().Be(1);
        hub.Published.Should().NotContain(entry =>
            entry.Event.EventCase == AGUIEvent.EventOneofCase.RunFinished);
        var terminal = hub.Published.Single(entry =>
            entry.Event.EventCase == AGUIEvent.EventOneofCase.RunError).Event;
        terminal.Sequence.Should().Be(9);
        terminal.RunError.Code.Should().Be("TOOL_FAILED");
        terminal.RunError.Message.Should().Be("The required tool failed.");
    }

    [Fact]
    public async Task Projector_ControllerSuccess_ShouldEmitOneCompletedTerminalAndNoErrorTerminal()
    {
        var hub = new RecordingSessionEventHub();
        var projector = new NyxIdChatSessionEventProjector(hub);
        var context = ControllerContext();
        var state = ControllerState(NyxIdChatTaskStatus.Succeeded, NyxIdChatTurnStatus.Succeeded);
        state.ActiveTask.Steps[0].Status = NyxIdChatStepStatus.Done;

        await projector.ProjectAsync(
            context,
            CommittedEnvelope(
                context.RootActorId,
                ControllerReconciled(state),
                stateVersion: 16),
            CancellationToken.None);

        hub.Published.Count(entry => entry.Event.EventCase == AGUIEvent.EventOneofCase.RunFinished)
            .Should().Be(1);
        hub.Published.Should().NotContain(entry =>
            entry.Event.EventCase == AGUIEvent.EventOneofCase.RunError);
        var terminal = hub.Published.Single(entry =>
            entry.Event.EventCase == AGUIEvent.EventOneofCase.RunFinished).Event;
        terminal.Sequence.Should().Be(9);
        terminal.RunFinished.RunId.Should().Be("turn-alpha");
        terminal.RunFinished.Status.Should().Be(RunCompletionStatus.Completed);
    }

    [Fact]
    public async Task Projector_ControllerBlocked_ShouldEmitOneBlockedTerminalAndNoErrorTerminal()
    {
        var hub = new RecordingSessionEventHub();
        var projector = new NyxIdChatSessionEventProjector(hub);
        var context = ControllerContext();
        var state = ControllerState(NyxIdChatTaskStatus.Blocked, NyxIdChatTurnStatus.Blocked);
        state.ActiveTask.Steps[0].Status = NyxIdChatStepStatus.Waiting;

        await projector.ProjectAsync(
            context,
            CommittedEnvelope(
                context.RootActorId,
                ControllerReconciled(state),
                stateVersion: 17),
            CancellationToken.None);

        hub.Published.Count(entry => entry.Event.EventCase == AGUIEvent.EventOneofCase.RunFinished)
            .Should().Be(1);
        hub.Published.Should().NotContain(entry =>
            entry.Event.EventCase == AGUIEvent.EventOneofCase.RunError);
        var terminal = hub.Published.Single(entry =>
            entry.Event.EventCase == AGUIEvent.EventOneofCase.RunFinished).Event;
        terminal.RunFinished.RunId.Should().Be("turn-alpha");
        terminal.RunFinished.Status.Should().Be(RunCompletionStatus.Blocked);
    }

    [Fact]
    public async Task Projector_ControllerStopped_ShouldPreserveStoppedSnapshotAndEmitOneBlockedTransportTerminal()
    {
        var hub = new RecordingSessionEventHub();
        var projector = new NyxIdChatSessionEventProjector(hub);
        var context = ControllerContext();
        var state = ControllerState(NyxIdChatTaskStatus.Stopped, NyxIdChatTurnStatus.Stopped);
        state.ActiveTask.Steps[0].Status = NyxIdChatStepStatus.Cancelled;

        await projector.ProjectAsync(
            context,
            CommittedEnvelope(
                context.RootActorId,
                ControllerReconciled(state),
                stateVersion: 18),
            CancellationToken.None);

        var snapshot = hub.Published.Single(entry =>
            entry.Event.EventCase == AGUIEvent.EventOneofCase.Custom &&
            entry.Event.Custom.Name == NyxIdChatConversationAguiFrameBuilder.TaskSnapshotEventName)
            .Event.Custom.Payload.Unpack<NyxIdChatTaskState>();
        snapshot.Status.Should().Be(NyxIdChatTaskStatus.Stopped);
        hub.Published.Count(entry => entry.Event.EventCase == AGUIEvent.EventOneofCase.RunFinished)
            .Should().Be(1);
        hub.Published.Should().NotContain(entry =>
            entry.Event.EventCase == AGUIEvent.EventOneofCase.RunError);
        var terminal = hub.Published.Single(entry =>
            entry.Event.EventCase == AGUIEvent.EventOneofCase.RunFinished).Event;
        terminal.RunFinished.RunId.Should().Be("turn-alpha");
        terminal.RunFinished.Status.Should().Be(RunCompletionStatus.Blocked);
    }

    [Fact]
    public async Task Projector_ActiveTurnAdmissionRejection_ShouldEmitExactSteeringRequiredError()
    {
        var hub = new RecordingSessionEventHub();
        var projector = new NyxIdChatSessionEventProjector(hub);
        var context = new NyxIdChatSessionProjectionContext
        {
            RootActorId = "conversation-alpha",
            SessionId = "turn-beta",
            ProjectionKind = "nyxid-chat-session",
        };

        await projector.ProjectAsync(
            context,
            CommittedEnvelope(
                context.RootActorId,
                new NyxIdChatTurnAdmissionRejectedEvent
                {
                    ConversationActorId = context.RootActorId,
                    RequestedTurnId = context.SessionId,
                    ActiveTurnId = "turn-alpha",
                    CommandId = "command-beta",
                    CorrelationId = "correlation-beta",
                    ReasonCode = NyxIdChatControlCommands.ActiveTurnRequiresSteering,
                    SafeMessage = NyxIdChatControlCommands.ActiveTurnRequiresSteeringMessage,
                },
                stateVersion: 19),
            CancellationToken.None);

        var terminal = hub.Published.Should().ContainSingle().Which.Event;
        terminal.Sequence.Should().Be(19);
        terminal.EventCase.Should().Be(AGUIEvent.EventOneofCase.RunError);
        terminal.RunError.RunId.Should().Be("turn-beta");
        terminal.RunError.Code.Should().Be("ACTIVE_TURN_REQUIRES_STEERING");
        terminal.RunError.Message.Should().Be(
            NyxIdChatControlCommands.ActiveTurnRequiresSteeringMessage);
    }

    [Fact]
    public async Task Projector_SteeringAdmission_ShouldEmitTypedContinuationFrameForOriginTurn()
    {
        var hub = new RecordingSessionEventHub();
        var projector = new NyxIdChatSessionEventProjector(hub);
        var context = ControllerContext();
        var state = ControllerState(NyxIdChatTaskStatus.Stopped, NyxIdChatTurnStatus.Stopped);
        var admission = new NyxIdChatContinuationAdmissionState
        {
            Kind = NyxIdChatContinuationKind.Steering,
            RequestId = "steering-alpha",
            ClientRequestId = "client-steering-alpha",
            OriginTurnId = "turn-alpha",
            ContinuationTurnId = "turn-beta",
            Status = NyxIdChatContinuationAdmissionStatus.AcceptedForLater,
            ReasonCode = NyxIdChatControlCommands.SteeringAcceptedForLater,
            SafeMessage = "Accepted for later.",
            Instruction = "Use a safer path.",
        };
        state.ContinuationAdmission = admission.Clone();

        await projector.ProjectAsync(
            context,
            CommittedEnvelope(
                context.RootActorId,
                new NyxIdChatContinuationAdmissionCommittedEvent
                {
                    Admission = admission,
                    State = state,
                },
                stateVersion: 20),
            CancellationToken.None);

        var frame = hub.Published.Should().ContainSingle().Which.Event;
        frame.Sequence.Should().Be(state.ProgressSequence);
        frame.EventCase.Should().Be(AGUIEvent.EventOneofCase.Custom);
        frame.Custom.Name.Should().Be("nyxid.continuation.changed");
        frame.Custom.Payload.Unpack<NyxIdChatContinuationAdmissionState>()
            .Should().BeEquivalentTo(admission);
    }

    [Fact]
    public async Task Projector_EmptyActionWakeNoOp_ShouldEmitProgressAndTerminalForContinuationTurn()
    {
        var hub = new RecordingSessionEventHub();
        var projector = new NyxIdChatSessionEventProjector(hub);
        var context = new NyxIdChatSessionProjectionContext
        {
            RootActorId = "conversation-alpha",
            SessionId = "turn-action-alpha",
            ProjectionKind = "nyxid-chat-session",
        };
        var admission = new NyxIdChatContinuationAdmissionState
        {
            Kind = NyxIdChatContinuationKind.Action,
            RequestId = "command-action-alpha",
            ClientRequestId = "client-action-alpha",
            OriginTurnId = string.Empty,
            ContinuationTurnId = context.SessionId,
            Status = NyxIdChatContinuationAdmissionStatus.Accepted,
            ReasonCode = NyxIdChatBrowserActions.ActionContinuationAccepted,
            OwnerSubject = "owner-alpha",
        };
        var state = new NyxIdChatConversationGAgentState
        {
            ConversationActorId = context.RootActorId,
            ScopeId = "scope-alpha",
            ContinuationAdmission = admission.Clone(),
            ActiveTurn = new NyxIdChatTurnState
            {
                TurnId = context.SessionId,
                TaskId = "task-action-alpha",
                Status = NyxIdChatTurnStatus.Succeeded,
            },
            LatestTurn = new NyxIdChatTurnState
            {
                TurnId = context.SessionId,
                TaskId = "task-action-alpha",
                Status = NyxIdChatTurnStatus.Succeeded,
            },
            ActiveTask = new NyxIdChatTaskState
            {
                TurnId = context.SessionId,
                TaskId = "task-action-alpha",
                Status = NyxIdChatTaskStatus.Succeeded,
            },
            ProgressSequence = 21,
        };

        await projector.ProjectAsync(
            context,
            CommittedEnvelope(
                context.RootActorId,
                new NyxIdChatContinuationAdmissionCommittedEvent
                {
                    Admission = admission,
                    State = state,
                },
                stateVersion: 21),
            CancellationToken.None);

        hub.Published.Select(entry => entry.Event.EventCase).Should().Equal(
            AGUIEvent.EventOneofCase.Custom,
            AGUIEvent.EventOneofCase.Custom,
            AGUIEvent.EventOneofCase.TextMessageEnd,
            AGUIEvent.EventOneofCase.RunFinished);
        hub.Published[0].Event.Custom.Name.Should().Be(
            NyxIdChatConversationAguiFrameBuilder.ContinuationChangedEventName);
        hub.Published[1].Event.Custom.Name.Should().Be(
            NyxIdChatConversationAguiFrameBuilder.TaskSnapshotEventName);
        hub.Published[^1].Event.RunFinished.RunId.Should().Be(context.SessionId);
        hub.Published[^1].Event.RunFinished.Status.Should().Be(
            RunCompletionStatus.Completed);
        hub.Published.Should().OnlyContain(entry => entry.Event.Sequence == 21);
    }

    [Fact]
    public async Task Projector_RejectedActionContinuation_ShouldEmitTerminalForContinuationTurn()
    {
        var hub = new RecordingSessionEventHub();
        var projector = new NyxIdChatSessionEventProjector(hub);
        var context = new NyxIdChatSessionProjectionContext
        {
            RootActorId = "conversation-alpha",
            SessionId = "turn-action-alpha",
            ProjectionKind = "nyxid-chat-session",
        };
        var admission = new NyxIdChatContinuationAdmissionState
        {
            Kind = NyxIdChatContinuationKind.Action,
            RequestId = "command-action-alpha",
            ClientRequestId = "client-action-alpha",
            ContinuationTurnId = context.SessionId,
            Status = NyxIdChatContinuationAdmissionStatus.Rejected,
            ReasonCode = NyxIdChatBrowserActions.ActionContinuationActiveTurn,
            SafeMessage = "Another conversation turn is active.",
            OwnerSubject = "owner-alpha",
        };

        await projector.ProjectAsync(
            context,
            CommittedEnvelope(
                context.RootActorId,
                new NyxIdChatContinuationAdmissionCommittedEvent
                {
                    Admission = admission,
                    State = new NyxIdChatConversationGAgentState
                    {
                        ConversationActorId = context.RootActorId,
                        ScopeId = "scope-alpha",
                        ContinuationAdmission = admission.Clone(),
                        ProgressSequence = 10,
                    },
                },
                stateVersion: 22),
            CancellationToken.None);

        hub.Published.Select(entry => entry.Event.EventCase).Should().Equal(
            AGUIEvent.EventOneofCase.Custom,
            AGUIEvent.EventOneofCase.RunError);
        var terminal = hub.Published[^1].Event.RunError;
        terminal.RunId.Should().Be(context.SessionId);
        terminal.Code.Should().Be(NyxIdChatBrowserActions.ActionContinuationActiveTurn);
        terminal.Message.Should().Be("Another conversation turn is active.");
    }

    [Fact]
    public async Task Projector_LateToolEvidence_ShouldRefineStepWithoutRepeatingTerminal()
    {
        var hub = new RecordingSessionEventHub();
        var projector = new NyxIdChatSessionEventProjector(hub);
        var context = ControllerContext();
        var state = ControllerState(NyxIdChatTaskStatus.Stopped, NyxIdChatTurnStatus.Stopped);
        var step = state.ActiveTask.Steps.Single();
        step.Kind = NyxIdChatStepKind.Tool;
        step.Status = NyxIdChatStepStatus.Uncertain;
        step.ExternalEffect = NyxIdChatEffectEvidence.Confirmed;
        step.Operation.Kind = NyxIdChatStepKind.Tool;
        step.Operation.Phase = NyxIdChatOperationPhase.Succeeded;
        state.ProgressSequence = 10;

        await projector.ProjectAsync(
            context,
            CommittedEnvelope(
                context.RootActorId,
                new NyxIdChatLateOperationEvidenceCommittedEvent
                {
                    Key = step.Operation.Key.Clone(),
                    OperationPhase = NyxIdChatOperationPhase.Succeeded,
                    ExternalEffect = NyxIdChatEffectEvidence.Confirmed,
                    ToolReceipt = new AgentToolReceipt
                    {
                        CallId = "call-alpha",
                        ToolName = "repository_update",
                        Status = AgentToolReceiptStatus.Success,
                    },
                    ProgressSequence = state.ProgressSequence,
                    State = state,
                },
                stateVersion: 21),
            CancellationToken.None);

        hub.Published.Should().ContainSingle(entry =>
            entry.Event.EventCase == AGUIEvent.EventOneofCase.Custom &&
            entry.Event.Custom.Name ==
                NyxIdChatConversationAguiFrameBuilder.TaskSnapshotEventName);
        var changed = hub.Published.Should().ContainSingle(entry =>
            entry.Event.EventCase == AGUIEvent.EventOneofCase.Custom &&
            entry.Event.Custom.Name ==
                NyxIdChatConversationAguiFrameBuilder.TaskStepChangedEventName).Which;
        changed.Event.Custom.Payload.Unpack<NyxIdChatTaskStepState>()
            .ExternalEffect.Should().Be(NyxIdChatEffectEvidence.Confirmed);
        hub.Published.Should().ContainSingle(entry =>
            entry.Event.EventCase == AGUIEvent.EventOneofCase.ToolCallEnd &&
            entry.Event.ToolCallEnd.ToolCallId == "call-alpha");
        hub.Published.Should().NotContain(entry =>
            entry.Event.EventCase == AGUIEvent.EventOneofCase.RunFinished ||
            entry.Event.EventCase == AGUIEvent.EventOneofCase.RunError ||
            entry.Event.EventCase == AGUIEvent.EventOneofCase.TextMessageEnd,
            "the stop fence already emitted the origin turn terminal");
    }

    [Theory]
    [InlineData(NyxIdChatStepControlKind.Retry, NyxIdChatTaskStatus.Active,
        NyxIdChatTurnStatus.Active, NyxIdChatStepStatus.Running, false)]
    [InlineData(NyxIdChatStepControlKind.Skip, NyxIdChatTaskStatus.Succeeded,
        NyxIdChatTurnStatus.Succeeded, NyxIdChatStepStatus.Skipped, true)]
    public async Task Projector_StepControl_ShouldEmitTerminalOnlyWhenControlMakesTurnTerminal(
        NyxIdChatStepControlKind kind,
        NyxIdChatTaskStatus taskStatus,
        NyxIdChatTurnStatus turnStatus,
        NyxIdChatStepStatus stepStatus,
        bool shouldEmitTerminal)
    {
        var hub = new RecordingSessionEventHub();
        var projector = new NyxIdChatSessionEventProjector(hub);
        var context = ControllerContext();
        var state = ControllerState(taskStatus, turnStatus);
        state.ActiveTask.Steps.Single().Status = stepStatus;
        state.ProgressSequence = 11;
        var result = new NyxIdChatStepControlResultState
        {
            Kind = kind,
            RequestId = kind == NyxIdChatStepControlKind.Retry
                ? "retry-alpha"
                : "skip-alpha",
            ClientRequestId = "client-control-alpha",
            ScopeId = "scope-alpha",
            ConversationActorId = context.RootActorId,
            TurnId = context.SessionId,
            TaskId = "task-alpha",
            StepId = "step-alpha",
            ExpectedOperationGeneration = 1,
            OperationGeneration = kind == NyxIdChatStepControlKind.Retry ? 2 : 1,
            Outcome = NyxIdChatTransitionOutcome.Accepted,
        };
        state.LatestStepControlResult = result.Clone();

        await projector.ProjectAsync(
            context,
            CommittedEnvelope(
                context.RootActorId,
                new NyxIdChatStepControlCommittedEvent
                {
                    Result = result,
                    State = state,
                },
                stateVersion: 22),
            CancellationToken.None);

        var control = hub.Published.Should().ContainSingle(entry =>
            entry.Event.EventCase == AGUIEvent.EventOneofCase.Custom &&
            entry.Event.Custom.Name ==
                NyxIdChatConversationAguiFrameBuilder.StepControlChangedEventName).Which.Event;
        control.Sequence.Should().Be(state.ProgressSequence);
        control.Custom.Payload.Unpack<NyxIdChatStepControlResultState>()
            .Should().BeEquivalentTo(result);
        hub.Published.Should().ContainSingle(entry =>
            entry.Event.EventCase == AGUIEvent.EventOneofCase.Custom &&
            entry.Event.Custom.Name ==
                NyxIdChatConversationAguiFrameBuilder.TaskSnapshotEventName);
        hub.Published.Should().ContainSingle(entry =>
            entry.Event.EventCase == AGUIEvent.EventOneofCase.Custom &&
            entry.Event.Custom.Name ==
                NyxIdChatConversationAguiFrameBuilder.TaskStepChangedEventName);
        var terminals = hub.Published.Where(entry =>
            entry.Event.EventCase == AGUIEvent.EventOneofCase.RunFinished ||
            entry.Event.EventCase == AGUIEvent.EventOneofCase.RunError ||
            entry.Event.EventCase == AGUIEvent.EventOneofCase.TextMessageEnd).ToArray();
        if (!shouldEmitTerminal)
        {
            terminals.Should().BeEmpty();
            return;
        }

        terminals.Select(entry => entry.Event.EventCase).Should().Equal(
            AGUIEvent.EventOneofCase.TextMessageEnd,
            AGUIEvent.EventOneofCase.RunFinished);
        terminals[^1].Event.RunFinished.RunId.Should().Be(context.SessionId);
        terminals[^1].Event.RunFinished.Status.Should().Be(RunCompletionStatus.Completed);
    }

    [Fact]
    public async Task Projector_ShouldEmitCommandAttemptRejectionWithoutSessionProgress()
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
                new RoleChatCommandAttemptRejectedEvent
                {
                    RequestedSessionId = context.SessionId,
                    CommandAttemptId = "cmd-attempt-rejected",
                    Reason = RoleChatCommandAttemptRejectionReason.PromptMismatch,
                    SafeMessage = "This client request id was already used for different input.",
                },
                stateVersion: 42),
            CancellationToken.None);

        var terminal = hub.Published.Should().ContainSingle().Which.Event;
        terminal.EventCase.Should().Be(AGUIEvent.EventOneofCase.RunError);
        terminal.RunError.RunId.Should().Be(context.SessionId);
        terminal.RunError.Code.Should().Be("IDEMPOTENCY_CONFLICT");
        terminal.RunError.Message.Should().Be("This client request id was already used for different input.");
        terminal.Sequence.Should().Be(42);
    }

    [Fact]
    public async Task Projector_ShouldEmitLegacySessionConflictWireTypeDuringRollingUpgrade()
    {
        var hub = new RecordingSessionEventHub();
        var projector = new NyxIdChatSessionEventProjector(hub);
        var context = new NyxIdChatSessionProjectionContext
        {
            RootActorId = "chat-actor-1",
            SessionId = "turn-legacy-conflict",
            ProjectionKind = "nyxid-chat-session",
        };
        var envelope = CommittedEnvelope(
            context.RootActorId,
            new RoleChatCommandAttemptRejectedEvent
            {
                RequestedSessionId = context.SessionId,
                Reason = RoleChatCommandAttemptRejectionReason.InputPartsMismatch,
                SafeMessage = "legacy input conflict",
            },
            stateVersion: 41);
        var committed = envelope.Payload.Unpack<CommittedStateEventPublished>();
        committed.StateEvent.EventData.TypeUrl =
            "type.googleapis.com/aevatar.ai.RoleChatSessionConflictEvent";
        committed.StateEvent.EventType = "aevatar.ai.RoleChatSessionConflictEvent";
        envelope.Payload = Any.Pack(committed);

        await projector.ProjectAsync(context, envelope, CancellationToken.None);

        var terminal = hub.Published.Should().ContainSingle().Which.Event;
        terminal.EventCase.Should().Be(AGUIEvent.EventOneofCase.RunError);
        terminal.RunError.RunId.Should().Be(context.SessionId);
        terminal.RunError.Code.Should().Be("IDEMPOTENCY_CONFLICT");
        terminal.RunError.Message.Should().Be("legacy input conflict");
        terminal.Sequence.Should().Be(41);
    }

    [Fact]
    public async Task Projector_ShouldEmitCommittedAuthorizationThenBlockedTerminal()
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
            ReasonCode = "NYXID_UNAUTHORIZED",
            SafeMessage = "Connect or reauthorize api-github to continue.",
        };

        await projector.ProjectAsync(
            context,
            CommittedEnvelope(
                context.RootActorId,
                new RoleChatSessionProgressedEvent
                {
                    SessionId = context.SessionId,
                    Sequence = 30,
                    AuthorizationRequired = new RoleChatAuthorizationRequiredProgress
                    {
                        AuthorizationRequired = blocker,
                    },
                }),
            CancellationToken.None);
        await projector.ProjectAsync(
            context,
            CommittedEnvelope(
                context.RootActorId,
                new RoleChatSessionProgressedEvent
                {
                    SessionId = context.SessionId,
                    Sequence = 31,
                    Terminal = new RoleChatTerminalProgress
                    {
                        Outcome = RoleChatSessionOutcome.Blocked,
                        FinalContent = "blocked",
                    },
                }),
            CancellationToken.None);

        hub.Published.Select(entry => entry.Event.EventCase).Should().Equal(
            AGUIEvent.EventOneofCase.Custom,
            AGUIEvent.EventOneofCase.RunFinished);
        hub.Published[0].Event.Custom.Name.Should().Be("nyxid.authorization.required");
        hub.Published[0].Event.Custom.Payload.Unpack<NyxIdAuthorizationRequiredEvent>()
            .Should().BeEquivalentTo(blocker);
        hub.Published.Select(entry => entry.Event.Sequence).Should().Equal(30, 31);
        hub.Published[1].Event.RunFinished.RunId.Should().Be(context.SessionId);
        hub.Published[1].Event.RunFinished.Status.Should().Be(RunCompletionStatus.Blocked);
        hub.Published[1].Event.RunFinished.Result.Unpack<StringValue>().Value.Should().Be("blocked");
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

    [Fact]
    public async Task Projector_InputRequestAndResolution_ShouldEmitCommittedFrames()
    {
        var hub = new RecordingSessionEventHub();
        var projector = new NyxIdChatSessionEventProjector(hub);
        var context = ControllerContext();
        var state = ControllerState(NyxIdChatTaskStatus.Active, NyxIdChatTurnStatus.Active);
        state.ProgressSequence = 31;
        state.PendingInput = new NyxIdChatPendingInputState
        {
            RequestId = "input-alpha",
            TurnId = context.SessionId,
            TaskId = "task-alpha",
            StepId = "step-alpha",
            Prompt = "Choose a deployment region.",
            AskedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-08-01T12:00:00Z")),
            Options =
            {
                new NyxIdChatInputOption { OptionId = "option-singapore", Label = "Singapore" },
                new NyxIdChatInputOption { OptionId = "option-frankfurt", Label = "Frankfurt" },
            },
        };

        await projector.ProjectAsync(
            context,
            CommittedEnvelope(
                context.RootActorId,
                new NyxIdChatInputRequestedEvent
                {
                    PendingInput = state.PendingInput.Clone(),
                    State = state.Clone(),
                },
                stateVersion: 31),
            CancellationToken.None);

        var requested = hub.Published.Should().ContainSingle().Which.Event;
        requested.Sequence.Should().Be(31);
        requested.Custom.Name.Should().Be(
            NyxIdChatConversationAguiFrameBuilder.InputRequestEventName);
        requested.Custom.Payload.Unpack<NyxIdChatPendingInputState>()
            .Should().BeEquivalentTo(state.PendingInput);

        state.PendingInput = null;
        state.ProgressSequence = 32;
        var resolution = new NyxIdChatInputResolutionState
        {
            RequestId = "input-alpha",
            ClientRequestId = "client-input-alpha",
            Outcome = NyxIdChatNeedsYouResolutionOutcome.Accepted,
            CommittedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-08-01T12:01:00Z")),
        };
        await projector.ProjectAsync(
            context,
            CommittedEnvelope(
                context.RootActorId,
                new NyxIdChatInputResolutionCommittedEvent
                {
                    Resolution = resolution,
                    State = state,
                },
                stateVersion: 32),
            CancellationToken.None);

        var changed = hub.Published[^1].Event;
        changed.Sequence.Should().Be(32);
        changed.Custom.Name.Should().Be(
            NyxIdChatConversationAguiFrameBuilder.InputChangedEventName);
        changed.Custom.Payload.Unpack<NyxIdChatInputResolutionState>()
            .Should().BeEquivalentTo(resolution);
    }

    [Fact]
    public async Task Projector_ApprovalRequestAndResolution_ShouldEmitCommittedFrames()
    {
        var hub = new RecordingSessionEventHub();
        var projector = new NyxIdChatSessionEventProjector(hub);
        var context = ControllerContext();
        var state = ControllerState(NyxIdChatTaskStatus.Active, NyxIdChatTurnStatus.Active);
        state.ProgressSequence = 41;
        state.PendingApproval = new NyxIdChatPendingApprovalState
        {
            ApprovalRequestId = "approval-alpha",
            TurnId = context.SessionId,
            TaskId = "task-alpha",
            StepId = "step-alpha",
            ToolName = "repository_delete",
            AskedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-08-01T12:00:00Z")),
            Presentation = new NyxIdChatApprovalPresentation
            {
                Action = "delete",
                Target = "repository:repo-alpha",
                ActorLabel = "Aevatar Assistant",
                Reversibility = NyxIdChatApprovalReversibility.Irreversible,
                GrantBoundary = "within_grant",
            },
        };

        await projector.ProjectAsync(
            context,
            CommittedEnvelope(
                context.RootActorId,
                ControllerReconciled(state),
                stateVersion: 41),
            CancellationToken.None);

        var request = hub.Published.Select(static entry => entry.Event)
            .Single(frame => frame.Custom?.Name ==
                NyxIdChatConversationAguiFrameBuilder.ApprovalRequestEventName);
        request.Sequence.Should().Be(41);
        request.Custom.Payload.Unpack<NyxIdChatPendingApprovalState>()
            .Should().BeEquivalentTo(state.PendingApproval);

        state.PendingApproval = null;
        state.ProgressSequence = 42;
        var resolution = new NyxIdChatApprovalResolutionState
        {
            RequestId = "approval-alpha",
            ClientRequestId = "client-approval-alpha",
            Outcome = NyxIdChatNeedsYouResolutionOutcome.Accepted,
            Approved = false,
            CommittedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-08-01T12:01:00Z")),
        };
        await projector.ProjectAsync(
            context,
            CommittedEnvelope(
                context.RootActorId,
                new NyxIdChatApprovalResolutionCommittedEvent
                {
                    Resolution = resolution,
                    State = state,
                },
                stateVersion: 42),
            CancellationToken.None);

        var changed = hub.Published[^1].Event;
        changed.Sequence.Should().Be(42);
        changed.Custom.Name.Should().Be(
            NyxIdChatConversationAguiFrameBuilder.ApprovalChangedEventName);
        changed.Custom.Payload.Unpack<NyxIdChatApprovalResolutionState>()
            .Should().BeEquivalentTo(resolution);
    }

    private static EventEnvelope CommittedEnvelope(string actorId, IMessage evt, long stateVersion = 1) => new()
    {
        Payload = Any.Pack(new CommittedStateEventPublished
        {
            StateEvent = new StateEvent
            {
                EventId = Guid.NewGuid().ToString("N"),
                Version = stateVersion,
                EventData = Any.Pack(evt),
                Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            },
            StateRoot = Any.Pack(new RoleGAgentState()),
        }),
        Route = EnvelopeRouteSemantics.CreateObserverPublication(actorId),
        Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
    };

    private static NyxIdChatSessionProjectionContext ControllerContext() => new()
    {
        RootActorId = "conversation-alpha",
        SessionId = "turn-alpha",
        ProjectionKind = "nyxid-chat-session",
    };

    private static NyxIdChatConversationGAgentState ControllerState(
        NyxIdChatTaskStatus taskStatus,
        NyxIdChatTurnStatus turnStatus)
    {
        var key = new NyxIdChatOperationKey
        {
            ConversationActorId = "conversation-alpha",
            TurnId = "turn-alpha",
            TaskId = "task-alpha",
            StepId = "step-alpha",
            OperationId = "operation-alpha",
            OperationGeneration = 1,
        };
        var step = new NyxIdChatTaskStepState
        {
            StepId = key.StepId,
            Order = 1,
            Kind = NyxIdChatStepKind.Llm,
            Status = NyxIdChatStepStatus.Running,
            Required = true,
            Operation = new NyxIdChatOperationState
            {
                Key = key,
                Kind = NyxIdChatStepKind.Llm,
                Phase = NyxIdChatOperationPhase.Dispatched,
            },
        };
        var task = new NyxIdChatTaskState
        {
            TaskId = "task-alpha",
            TurnId = "turn-alpha",
            Status = taskStatus,
            ActiveStepId = taskStatus == NyxIdChatTaskStatus.Active ? key.StepId : string.Empty,
            ActiveOperationId = taskStatus == NyxIdChatTaskStatus.Active ? key.OperationId : string.Empty,
        };
        task.Steps.Add(step);
        return new NyxIdChatConversationGAgentState
        {
            ConversationActorId = "conversation-alpha",
            ScopeId = "scope-alpha",
            ActiveTurn = new NyxIdChatTurnState
            {
                TurnId = "turn-alpha",
                TaskId = "task-alpha",
                Status = turnStatus,
            },
            LatestTurn = new NyxIdChatTurnState
            {
                TurnId = "turn-alpha",
                TaskId = "task-alpha",
                Status = turnStatus,
            },
            ActiveTask = task,
            ProgressSequence = taskStatus == NyxIdChatTaskStatus.Active ? 5 : 9,
        };
    }

    private static NyxIdChatOperationReconciledEvent ControllerReconciled(
        NyxIdChatConversationGAgentState state) => new()
    {
        Result = new NyxIdChatOperationResultSignal
        {
            Key = state.ActiveTask.Steps[0].Operation.Key.Clone(),
            Llm = new NyxIdChatLLMOperationResult(),
        },
        Task = state.ActiveTask.Clone(),
        Turn = state.ActiveTurn.Clone(),
        State = state.Clone(),
        ProgressSequence = state.ProgressSequence,
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
