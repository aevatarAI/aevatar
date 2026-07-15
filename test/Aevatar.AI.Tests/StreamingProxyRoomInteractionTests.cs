using System.IO;
using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Threading.Channels;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Core.Commands;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Google.Protobuf;
using Any = Google.Protobuf.WellKnownTypes.Any;
using Google.Protobuf.WellKnownTypes;
using Aevatar.GAgents.StreamingProxy;
using Aevatar.GAgents.StreamingProxy.Application.Rooms;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using static Aevatar.GAgents.StreamingProxy.StreamingProxyEndpoints;

namespace Aevatar.AI.Tests;

public sealed class StreamingProxyRoomInteractionTests : StreamingProxyTestBase
{
        [Fact]
        public void AddStreamingProxy_ShouldResolveRealRoomInteractionGraph()
        {
            var runtime = new StubActorRuntime();
            var services = new ServiceCollection()
                .AddLogging()
                .AddSingleton<IActorRuntime>(runtime)
                .AddSingleton<IActorDispatchPort>(new StubActorDispatchPort(runtime))
                .AddSingleton<IStreamingProxyRoomSessionProjectionPort>(new StubRoomSessionProjectionPort())
                .AddSingleton<IStreamingProxyChatSessionTerminalQueryPort>(new StubTerminalQueryPort())
                .AddSingleton<IStreamingProxyRoomParticipantsQueryPort>(new StubRoomParticipantsQueryPort())
                .AddStreamingProxy()
                .BuildServiceProvider();

            services.GetRequiredService<
                ICommandInteractionService<StreamingProxyRoomChatCommand, StreamingProxyRoomChatAcceptedReceipt, StreamingProxyRoomChatStartError, StreamingProxyRoomSessionEnvelope, StreamingProxyProjectionCompletionStatus>>()
                .Should().NotBeNull();
            services.GetRequiredService<ICommandEnvelopeFactory<StreamingProxyRoomChatCommand>>()
                .Should().BeOfType<StreamingProxyRoomChatCommandEnvelopeFactory>();
            services.GetRequiredService<ICommandObservationLifecycle<StreamingProxyRoomChatCommand, StreamingProxyRoomChatCommandTarget, StreamingProxyRoomChatAcceptedReceipt, StreamingProxyRoomChatStartError>>()
                .Should().BeOfType<StreamingProxyRoomObservationLifecycle>();
            services.GetRequiredService<IStreamingProxyRoomSubscriptionObservationPort>()
                .Should().BeOfType<StreamingProxyRoomSubscriptionObservationPort>();
        }

        [Fact]
        public async Task HandleChatAsync_ShouldAttachProjectionSession_AndEmitRunFinished()
        {
            var context = CreateScopedHttpContext();
            context.Response.Body = new MemoryStream();
            var roomCommandService = new StubRoomCommandService();
            var interactionService = new StubStreamingProxyRoomChatInteractionService();
            var durableCompletionResolver = new StreamingProxyChatDurableCompletionResolver(
                new StubTerminalQueryPort(StreamingProxyChatSessionTerminalStatus.Completed));
            var actorStore = new StubGAgentActorStore();
            var request = new ChatTopicRequest("Discuss webhook relay", "session-123");
            interactionService.Frames.Add(new StreamingProxyRoomSessionEnvelope
            {
                Envelope = CreateCommittedEnvelope(
                    new GroupChatTopicEvent
                    {
                        Prompt = "Discuss webhook relay",
                        SessionId = "session-123",
                    },
                new StreamingProxyGAgentState
                    {
                        RoomName = "Room A",
                        Messages =
                        {
                            new StreamingProxyChatMessage
                            {
                                Sequence = 1,
                                SenderAgentId = "system",
                                SenderName = "system",
                                Content = "Discuss webhook relay",
                                Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                                IsTopic = true,
                            },
                },
                },
                version: 2),
            });
            interactionService.Frames.Add(new StreamingProxyRoomSessionEnvelope
            {
                Envelope = CreateCommittedEnvelope(
                    new GroupChatMessageEvent
                    {
                        AgentId = "agent-1",
                        AgentName = "Alice",
                        Content = "I can help with that.",
                        SessionId = "session-123",
                    },
                new StreamingProxyGAgentState
                    {
                        RoomName = "Room A",
                        Messages =
                        {
                            new StreamingProxyChatMessage
                            {
                                Sequence = 1,
                                SenderAgentId = "system",
                                SenderName = "system",
                                Content = "Discuss webhook relay",
                                Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                                IsTopic = true,
                            },
                new StreamingProxyChatMessage
                            {
                                Sequence = 2,
                                SenderAgentId = "agent-1",
                                SenderName = "Alice",
                                Content = "I can help with that.",
                                Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                            },
                },
                },
                version: 3),
            });
            interactionService.Frames.Add(new StreamingProxyRoomSessionEnvelope
            {
                Envelope = CreateCommittedEnvelope(
                    new StreamingProxyChatSessionTerminalStateChanged
                    {
                        SessionId = "session-123",
                        Status = StreamingProxyChatSessionTerminalStatus.Completed,
                        TerminalAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                    },
                new StreamingProxyGAgentState
                    {
                        RoomName = "Room A",
                    },
                version: 4),
            });

            await InvokeTaskAsync(
                "HandleChatAsync",
                context,
                "scope-a",
                "room-a",
                request,
                roomCommandService,
                actorStore,
                interactionService,
                durableCompletionResolver,
                NullLoggerFactory.Instance,
                CancellationToken.None);

            interactionService.Commands.Should().ContainSingle().Which.Should().Be(new StreamingProxyRoomChatCommand(
                "room-a",
                "scope-a",
                "Discuss webhook relay",
                "session-123"));
            roomCommandService.TerminalCommands.Should().BeEmpty();

            context.Response.Body.Position = 0;
            var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
            body.Should().Contain("TOPIC_STARTED");
            body.Should().Contain("AGENT_MESSAGE");
            body.Should().Contain("RUN_FINISHED");
        }

        [Fact]
        public async Task HandleChatAsync_ShouldNotPublishEndpointOwnedTerminalState_WhenCancelled()
        {
            var context = CreateScopedHttpContext();
            context.Response.Body = new MemoryStream();
            var roomCommandService = new StubRoomCommandService();
            var interactionService = new StubStreamingProxyRoomChatInteractionService
            {
                WaitForCancellation = true,
            };
            var durableCompletionResolver = new StreamingProxyChatDurableCompletionResolver(new StubTerminalQueryPort());
            var actorStore = new StubGAgentActorStore();
            using var cts = new CancellationTokenSource();

            var task = InvokeTaskAsync(
                "HandleChatAsync",
                context,
                "scope-a",
                "room-a",
                new ChatTopicRequest("Cancel me", "session-cancel"),
                roomCommandService,
                actorStore,
                interactionService,
                durableCompletionResolver,
                NullLoggerFactory.Instance,
                cts.Token);

            await interactionService.Started.Task;
            cts.Cancel();
            await task;

            roomCommandService.TerminalCommands.Should().BeEmpty();
        }

        [Fact]
        public async Task StreamingProxyRoomInteraction_ShouldBindDispatchEmitFinalizeAndCleanup()
        {
            var actor = new StubActor("room-a");
            var runtime = new StubActorRuntime([actor]);
            var projectionPort = new StubRoomSessionProjectionPort();
            projectionPort.Messages.Add(new StreamingProxyRoomSessionEnvelope
            {
                Envelope = CreateCommittedEnvelope(
                    new GroupChatMessageEvent
                    {
                        AgentId = "agent-1",
                        AgentName = "Alice",
                        Content = "hello",
                        SessionId = "session-123",
                    },
                new StreamingProxyGAgentState { RoomName = "Room A" },
                version: 2),
            });
            projectionPort.Messages.Add(new StreamingProxyRoomSessionEnvelope
            {
                Envelope = StreamingProxyRoomInteractionHelpers.CreateTerminalEnvelope(
                    actor.Id,
                    "session-123",
                    StreamingProxyChatSessionTerminalStatus.Completed,
                    null),
            });
            var dispatchPort = new StubActorDispatchPort(runtime);
            var services = new ServiceCollection()
                .AddLogging()
                .AddSingleton<IActorRuntime>(runtime)
                .AddSingleton<IActorDispatchPort>(dispatchPort)
                .AddSingleton<IStreamingProxyRoomSessionProjectionPort>(projectionPort)
                .AddSingleton<IStreamingProxyChatSessionTerminalQueryPort>(new StubTerminalQueryPort())
                .AddStreamingProxy()
                .BuildServiceProvider();
            var interaction = services.GetRequiredService<
                ICommandInteractionService<StreamingProxyRoomChatCommand, StreamingProxyRoomChatAcceptedReceipt, StreamingProxyRoomChatStartError, StreamingProxyRoomSessionEnvelope, StreamingProxyProjectionCompletionStatus>>();
            var emitted = new List<StreamingProxyRoomSessionEnvelope>();

            var result = await interaction.ExecuteAsync(
                new StreamingProxyRoomChatCommand(actor.Id, "scope-a", "Discuss claims", "session-123"),
                (frame, _) =>
                {
                    emitted.Add(frame);
                    return ValueTask.CompletedTask;
                });

            result.Succeeded.Should().BeTrue();
            result.Receipt.Should().NotBeNull();
            result.Receipt!.ActorId.Should().Be(actor.Id);
            result.Receipt.CommandId.Should().NotBeNullOrWhiteSpace();
            result.Receipt.CommandId.Should().NotBe("session-123");
            result.Receipt.CorrelationId.Should().Be(result.Receipt.CommandId);
            result.Receipt.SessionId.Should().Be("session-123");
            result.FinalizeResult.Should().NotBeNull();
            result.FinalizeResult!.Completed.Should().BeTrue();
            result.FinalizeResult.Completion.Should().Be(StreamingProxyProjectionCompletionStatus.Completed);
            projectionPort.AttachExistingCalls.Should().ContainSingle(x =>
                x.actorId == actor.Id &&
                x.sessionId == "session-123");
            projectionPort.AttachCount.Should().Be(1);
            projectionPort.DetachCount.Should().Be(1);
            projectionPort.ReleaseCount.Should().Be(1);
            dispatchPort.Dispatches.Should().ContainSingle();
            var request = dispatchPort.Dispatches.Single().Envelope.Payload.Unpack<ChatRequestEvent>();
            request.Prompt.Should().Be("Discuss claims");
            request.SessionId.Should().Be("session-123");
            request.ScopeId.Should().Be("scope-a");
            emitted.Should().HaveCount(2);
            emitted.Last().Envelope.Payload.Unpack<StreamingProxyChatSessionTerminalStateChanged>().Status
                .Should().Be(StreamingProxyChatSessionTerminalStatus.Completed);
        }

        [Fact]
        public async Task StreamingProxyRoomInteraction_ShouldPreserveExplicitCommandAndCorrelationIdentity()
        {
            var actor = new StubActor("room-a");
            var runtime = new StubActorRuntime([actor]);
            var projectionPort = new StubRoomSessionProjectionPort();
            projectionPort.Messages.Add(new StreamingProxyRoomSessionEnvelope
            {
                Envelope = StreamingProxyRoomInteractionHelpers.CreateTerminalEnvelope(
                    actor.Id,
                    "session-123",
                    StreamingProxyChatSessionTerminalStatus.Completed,
                    null),
            });
            var dispatchPort = new StubActorDispatchPort(runtime);
            var services = new ServiceCollection()
                .AddLogging()
                .AddSingleton<IActorRuntime>(runtime)
                .AddSingleton<IActorDispatchPort>(dispatchPort)
                .AddSingleton<IStreamingProxyRoomSessionProjectionPort>(projectionPort)
                .AddSingleton<IStreamingProxyChatSessionTerminalQueryPort>(new StubTerminalQueryPort())
                .AddStreamingProxy()
                .BuildServiceProvider();
            var interaction = services.GetRequiredService<
                ICommandInteractionService<StreamingProxyRoomChatCommand, StreamingProxyRoomChatAcceptedReceipt, StreamingProxyRoomChatStartError, StreamingProxyRoomSessionEnvelope, StreamingProxyProjectionCompletionStatus>>();

            var result = await interaction.ExecuteAsync(
                new StreamingProxyRoomChatCommand(
                    actor.Id,
                    "scope-a",
                    "Discuss claims",
                    "session-123",
                    CommandId: "room-command-explicit",
                    CorrelationId: "room-correlation-explicit"),
                (_, _) => ValueTask.CompletedTask);

            result.Succeeded.Should().BeTrue();
            result.Receipt.Should().Be(new StreamingProxyRoomChatAcceptedReceipt(
                actor.Id,
                "room-command-explicit",
                "room-correlation-explicit",
                "session-123"));
            projectionPort.AttachExistingCalls.Should().ContainSingle(x =>
                x.actorId == actor.Id &&
                x.sessionId == "session-123");
            dispatchPort.Dispatches.Should().ContainSingle();
            var envelope = dispatchPort.Dispatches.Single().Envelope;
            envelope.Propagation?.CorrelationId.Should().Be("room-correlation-explicit");
            var request = envelope.Payload.Unpack<ChatRequestEvent>();
            request.SessionId.Should().Be("session-123");
        }

        [Fact]
        public async Task StreamingProxyRoomInteraction_ShouldReturnProjectionUnavailableAndDisposeSink_WhenBinderCannotAttach()
        {
            var actor = new StubActor("room-a");
            var runtime = new StubActorRuntime([actor]);
            var projectionPort = new StubRoomSessionProjectionPort { ReturnNullLease = true };
            var services = new ServiceCollection()
                .AddLogging()
                .AddSingleton<IActorRuntime>(runtime)
                .AddSingleton<IActorDispatchPort>(new StubActorDispatchPort(runtime))
                .AddSingleton<IStreamingProxyRoomSessionProjectionPort>(projectionPort)
                .AddSingleton<IStreamingProxyChatSessionTerminalQueryPort>(new StubTerminalQueryPort())
                .AddStreamingProxy()
                .BuildServiceProvider();
            var interaction = services.GetRequiredService<
                ICommandInteractionService<StreamingProxyRoomChatCommand, StreamingProxyRoomChatAcceptedReceipt, StreamingProxyRoomChatStartError, StreamingProxyRoomSessionEnvelope, StreamingProxyProjectionCompletionStatus>>();

            var result = await interaction.ExecuteAsync(
                new StreamingProxyRoomChatCommand(actor.Id, "scope-a", "prompt", "session-123"),
                (_, _) => ValueTask.CompletedTask);

            result.Succeeded.Should().BeFalse();
            result.Error.Should().Be(StreamingProxyRoomChatStartError.ProjectionUnavailable);
            projectionPort.AttachExistingCalls.Should().ContainSingle(x =>
                x.actorId == actor.Id &&
                x.sessionId == "session-123");
            projectionPort.AttachCount.Should().Be(0);
            projectionPort.DetachCount.Should().Be(0);
            projectionPort.ReleaseCount.Should().Be(0);
        }

        [Fact]
        public async Task StreamingProxyRoomInteraction_ShouldCleanupBoundObservation_WhenDispatchFails()
        {
            var actor = new StubActor("room-a");
            var runtime = new StubActorRuntime([actor]);
            var projectionPort = new StubRoomSessionProjectionPort();
            var services = new ServiceCollection()
                .AddLogging()
                .AddSingleton<IActorRuntime>(runtime)
                .AddSingleton<IActorDispatchPort>(new ThrowingActorDispatchPort(new InvalidOperationException("dispatch failed")))
                .AddSingleton<IStreamingProxyRoomSessionProjectionPort>(projectionPort)
                .AddSingleton<IStreamingProxyChatSessionTerminalQueryPort>(new StubTerminalQueryPort())
                .AddStreamingProxy()
                .BuildServiceProvider();
            var interaction = services.GetRequiredService<
                ICommandInteractionService<StreamingProxyRoomChatCommand, StreamingProxyRoomChatAcceptedReceipt, StreamingProxyRoomChatStartError, StreamingProxyRoomSessionEnvelope, StreamingProxyProjectionCompletionStatus>>();

            var act = async () => await interaction.ExecuteAsync(
                new StreamingProxyRoomChatCommand(actor.Id, "scope-a", "prompt", "session-123"),
                (_, _) => ValueTask.CompletedTask);

            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("dispatch failed");
            projectionPort.AttachExistingCalls.Should().ContainSingle(x =>
                x.actorId == actor.Id &&
                x.sessionId == "session-123");
            projectionPort.AttachCount.Should().Be(1);
            projectionPort.DetachCount.Should().Be(1);
            projectionPort.ReleaseCount.Should().Be(1);
        }

        [Fact]
        public void StreamingProxyRoomChatEnvelopeFactory_ShouldBuildTypedChatEnvelope()
        {
            var factory = new StreamingProxyRoomChatCommandEnvelopeFactory();
            var envelope = factory.CreateEnvelope(
                new StreamingProxyRoomChatCommand("room-a", "scope-a", "topic", "session-123"),
                new CommandContext("room-a", "command-1", "correlation-1", new Dictionary<string, string>()));

            envelope.Route?.Direct?.TargetActorId.Should().Be("room-a");
            envelope.Propagation?.CorrelationId.Should().Be("correlation-1");
            var request = envelope.Payload.Unpack<ChatRequestEvent>();
            request.Prompt.Should().Be("topic");
            request.ScopeId.Should().Be("scope-a");
            request.SessionId.Should().Be("session-123");
        }

        [Fact]
        public async Task StreamingProxyRoomChatFinalizeEmitter_ShouldEmitFailedTerminalOnlyWhenCompletionMissing()
        {
            var emitter = new StreamingProxyRoomChatFinalizeEmitter();
            var emitted = new List<StreamingProxyRoomSessionEnvelope>();

            await emitter.EmitAsync(
                new StreamingProxyRoomChatAcceptedReceipt("room-a", "command-1", "correlation-1", "session-123"),
                StreamingProxyProjectionCompletionStatus.Unknown,
                completed: false,
                (frame, _) =>
                {
                    emitted.Add(frame);
                    return ValueTask.CompletedTask;
                });

            emitted.Should().ContainSingle();
            var terminal = emitted[0].Envelope.Payload.Unpack<StreamingProxyChatSessionTerminalStateChanged>();
            terminal.SessionId.Should().Be("session-123");
            terminal.Status.Should().Be(StreamingProxyChatSessionTerminalStatus.Failed);
            terminal.ErrorMessage.Should().Be("StreamingProxy completion timed out.");

            await emitter.EmitAsync(
                new StreamingProxyRoomChatAcceptedReceipt("room-a", "command-1", "correlation-1", "session-123"),
                StreamingProxyProjectionCompletionStatus.Completed,
                completed: true,
                (frame, _) =>
                {
                    emitted.Add(frame);
                    return ValueTask.CompletedTask;
                });

            emitted.Should().HaveCount(1);
        }

        [Fact]
        public async Task StreamingProxyRoomChatOutputStream_ShouldStopOnTerminalEvent()
        {
            var stream = new StreamingProxyRoomChatOutputStream();
            var channel = Channel.CreateUnbounded<StreamingProxyRoomSessionEnvelope>();
            await channel.Writer.WriteAsync(new StreamingProxyRoomSessionEnvelope
            {
                Envelope = StreamingProxyRoomInteractionHelpers.CreateTerminalEnvelope(
                    "room-a",
                    "session-123",
                    StreamingProxyChatSessionTerminalStatus.Completed,
                    null),
            });
            await channel.Writer.WriteAsync(new StreamingProxyRoomSessionEnvelope
            {
                Envelope = CreateTopologyEnvelope(new GroupChatMessageEvent
                {
                    AgentId = "agent-1",
                    AgentName = "Alice",
                    Content = "should not emit",
                    SessionId = "session-123",
                }),
            });
            channel.Writer.TryComplete();
            var emitted = new List<StreamingProxyRoomSessionEnvelope>();

            await stream.PumpAsync(
                channel.Reader.ReadAllAsync(),
                (frame, _) =>
                {
                    emitted.Add(frame);
                    return ValueTask.CompletedTask;
                },
                frame => StreamingProxyRoomInteractionHelpers.ResolveSignal(frame) is
                    StreamingProxyStreamSignal.RunFinished or StreamingProxyStreamSignal.RunFailed);

            emitted.Should().ContainSingle();
            emitted[0].Envelope.Payload.Unpack<StreamingProxyChatSessionTerminalStateChanged>().Status
                .Should().Be(StreamingProxyChatSessionTerminalStatus.Completed);
        }

        [Fact]
        public async Task StreamingProxyRoomChatOutputStream_ShouldTimeout_WhenNoInitialEventArrives()
        {
            var stream = new StreamingProxyRoomChatOutputStream();
            var channel = Channel.CreateUnbounded<StreamingProxyRoomSessionEnvelope>();
            var emitted = new List<StreamingProxyRoomSessionEnvelope>();

            await stream.PumpAsync(
                channel.Reader.ReadAllAsync(),
                (frame, _) =>
                {
                    emitted.Add(frame);
                    return ValueTask.CompletedTask;
                });

            emitted.Should().BeEmpty();
        }

        [Fact]
        public async Task HandleChatAsync_ShouldNotPublishEndpointOwnedTerminalFallback_WhenInteractionFails()
        {
            var context = CreateScopedHttpContext();
            context.Response.Body = new MemoryStream();
            var roomCommandService = new StubRoomCommandService();
            var interactionService = new StubStreamingProxyRoomChatInteractionService
            {
                ThrowOnExecute = new InvalidOperationException("boom"),
            };

            await InvokeTaskAsync(
                "HandleChatAsync",
                context,
                "scope-a",
                "room-a",
                new ChatTopicRequest("hello", "session-123"),
                roomCommandService,
                new StubGAgentActorStore(),
                interactionService,
                new StreamingProxyChatDurableCompletionResolver(new StubTerminalQueryPort()),
                NullLoggerFactory.Instance,
                CancellationToken.None);

            context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
            context.Response.Body.Position = 0;
            var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
            body.Should().Contain("RUN_ERROR");
            roomCommandService.TerminalCommands.Should().BeEmpty();
        }

        [Fact]
        public async Task GAgent_ShouldTrackRoomMessagesAndParticipantLifecycle()
        {
            using var provider = AgentCoverageTestSupport.BuildServiceProvider();
            var agent = CreateAgent(provider, "streaming-proxy-agent");
            var publisher = new TestRecordingEventPublisher();
            agent.EventPublisher = publisher;

            await agent.ActivateAsync();
            await agent.HandleGroupChatRoomInitialized(new GroupChatRoomInitializedEvent { RoomName = "Nyx Room" });
            await agent.HandleGroupChatParticipantJoined(new GroupChatParticipantJoinedEvent
            {
                AgentId = "agent-1",
                DisplayName = "Alice",
            });
            await agent.HandleGroupChatParticipantJoined(new GroupChatParticipantJoinedEvent
            {
                AgentId = "agent-1",
                DisplayName = "Alice Updated",
            });
            await agent.HandleChatRequest(new ChatRequestEvent
            {
                Prompt = "Discuss the webhook setup",
                SessionId = "room-session",
                ToolContext = (AgentToolExecutionContext.Empty with
                {
                    Credentials = AgentToolCredentials.Empty with
                    {
                        CredentialRef = " typed-access ",
                    },
                Routing = LLMRequestRoutingContext.Empty with
                    {
                        NyxIdRoutePreference = " typed-route ",
                        ModelOverride = " typed-model ",
                    },
                }).ToPayload(),
            });
            await agent.HandleGroupChatMessage(new GroupChatMessageEvent
            {
                AgentId = "agent-2",
                AgentName = "Bob",
                Content = "I can help with that.",
                SessionId = "room-session",
            });
            await agent.HandleGroupChatParticipantLeft(new GroupChatParticipantLeftEvent { AgentId = "agent-1" });

            var state = agent.State;
            state.RoomName.Should().Be("Nyx Room");
            state.NextSequence.Should().Be(2);
            state.Messages.Should().HaveCount(2);
            state.Messages[0].IsTopic.Should().BeTrue();
            state.Messages[0].SenderAgentId.Should().Be("user");
            state.Messages[0].Content.Should().Be("Discuss the webhook setup");
            state.ChatLifecycles["room-session"].AccessToken.Should().Be("typed-access");
            state.ChatLifecycles["room-session"].PreferredRoute.Should().Be("typed-route");
            state.ChatLifecycles["room-session"].DefaultModel.Should().Be("typed-model");
            state.Messages[1].IsTopic.Should().BeFalse();
            state.Messages[1].SenderAgentId.Should().Be("agent-2");
            state.Messages[1].SenderName.Should().Be("Bob");
            state.Participants.Should().BeEmpty();
            publisher.Published.OfType<GroupChatParticipantJoinedEvent>().Should().HaveCount(1);
            publisher.Published.OfType<GroupChatTopicEvent>()
                .Should()
                .ContainSingle(x => x.Prompt == "Discuss the webhook setup" && x.SessionId == "room-session");
            publisher.Published.OfType<GroupChatMessageEvent>()
                .Should()
                .ContainSingle(x => x.AgentId == "agent-2" && x.Content == "I can help with that.");
            publisher.Published.OfType<GroupChatParticipantLeftEvent>()
                .Should()
                .ContainSingle(x => x.AgentId == "agent-1");
        }

        [Fact]
        public async Task GAgent_HandleChatRequest_WithNyxToken_ShouldPublishTypedContinuationToRunner()
        {
            using var provider = AgentCoverageTestSupport.BuildServiceProvider();
            var agent = CreateAgent(provider, "streaming-proxy-agent");
            var publisher = new TestRecordingEventPublisher();
            agent.EventPublisher = publisher;

            await agent.ActivateAsync();
            await agent.HandleChatRequest(new ChatRequestEvent
            {
                Prompt = "  Discuss the webhook setup  ",
                SessionId = " session-1 ",
                ScopeId = " scope-1 ",
                ToolContext = (AgentToolExecutionContext.Empty with
                {
                    Credentials = AgentToolCredentials.Empty with
                    {
                        CredentialRef = " access-token ",
                    },
                    Routing = LLMRequestRoutingContext.Empty with
                    {
                        NyxIdRoutePreference = " route-a ",
                        ModelOverride = " model-a ",
                    },
                }).ToPayload(),
            });

            var sent = publisher.Sent.Should().ContainSingle().Subject;
            sent.TargetActorId.Should().Be(StreamingProxyGAgent.ChatLifecycleContinuationRunnerStreamId);
            var continuation = sent.Event.Should().BeOfType<StreamingProxyChatLifecycleContinuationRequested>().Subject;
            continuation.SessionId.Should().Be("session-1");
            continuation.ScopeId.Should().Be("scope-1");
            continuation.Prompt.Should().Be("Discuss the webhook setup");
            continuation.AccessToken.Should().Be("access-token");
            continuation.PreferredRoute.Should().Be("route-a");
            continuation.DefaultModel.Should().Be("model-a");
        }

        [Fact]
        public async Task GAgent_HandleChatRequest_WithoutNyxToken_ShouldNotPublishContinuation()
        {
            using var provider = AgentCoverageTestSupport.BuildServiceProvider();
            var agent = CreateAgent(provider, "streaming-proxy-agent");
            var publisher = new TestRecordingEventPublisher();
            agent.EventPublisher = publisher;

            await agent.ActivateAsync();
            await agent.HandleChatRequest(new ChatRequestEvent
            {
                Prompt = "Discuss the webhook setup",
                SessionId = "session-1",
                ScopeId = "scope-1",
            });

            publisher.Sent.Should().BeEmpty();
            publisher.Published.OfType<StreamingProxyChatLifecycleContinuationRequested>().Should().BeEmpty();
        }

        [Fact]
        public async Task GAgent_HandleChatLifecycleContinuationRequested_ShouldForwardCompatRequestToRunner()
        {
            using var provider = AgentCoverageTestSupport.BuildServiceProvider();
            var agent = CreateAgent(provider, "streaming-proxy-agent");
            var publisher = new TestRecordingEventPublisher();
            agent.EventPublisher = publisher;

            await agent.ActivateAsync();
            await agent.HandleChatLifecycleContinuationRequested(new StreamingProxyChatLifecycleContinuationRequested
            {
                SessionId = "session-1",
                ScopeId = "scope-1",
                Prompt = "prompt",
                AccessToken = "access-token",
            });

            var sent = publisher.Sent.Should().ContainSingle().Subject;
            sent.TargetActorId.Should().Be(StreamingProxyGAgent.ChatLifecycleContinuationRunnerStreamId);
            sent.Event.Should().BeOfType<StreamingProxyChatLifecycleContinuationRequested>();
        }

        [Fact]
        public async Task GAgent_HandleChatParticipantsResolvedRequested_ShouldCommitParticipantsAndRequestFirstParticipant()
        {
            using var provider = AgentCoverageTestSupport.BuildServiceProvider();
            var agent = CreateAgent(provider, "room-1");
            var publisher = new TestRecordingEventPublisher();
            agent.EventPublisher = publisher;

            await agent.ActivateAsync();
            await agent.HandleChatRequest(new ChatRequestEvent
            {
                Prompt = "Discuss the roadmap.",
                SessionId = "session-1",
                ScopeId = "scope-1",
                ToolContext = (AgentToolExecutionContext.Empty with
                {
                    Credentials = AgentToolCredentials.Empty with
                    {
                        CredentialRef = "access-token",
                    },
                    Routing = LLMRequestRoutingContext.Empty with
                    {
                        NyxIdRoutePreference = "route-default",
                        ModelOverride = "model-default",
                    },
                }).ToPayload(),
            });
            publisher.Sent.Clear();

            await agent.HandleChatParticipantsResolvedRequested(new StreamingProxyChatParticipantsResolvedRequested
            {
                SessionId = "session-1",
                Participants =
                {
                    new StreamingProxyChatLifecycleParticipant
                    {
                        ParticipantId = " participant-1 ",
                        DisplayName = " Participant 1 ",
                        RoutePreference = " route-a ",
                        Model = " model-a ",
                    },
                    new StreamingProxyChatLifecycleParticipant
                    {
                        ParticipantId = "participant-2",
                        DisplayName = "Participant 2",
                        RoutePreference = "route-b",
                        Model = "model-b",
                    },
                },
            });

            var lifecycle = agent.State.ChatLifecycles["session-1"];
            lifecycle.MaxRounds.Should().Be(StreamingProxyDefaults.MaxDiscussionRounds);
            lifecycle.CurrentRound.Should().Be(1);
            lifecycle.NextParticipantIndex.Should().Be(0);
            lifecycle.Participants.Should().HaveCount(2);
            lifecycle.Participants[0].ParticipantId.Should().Be("participant-1");
            lifecycle.Participants[0].Status.Should().Be(StreamingProxyChatLifecycleParticipantStatus.Active);

            var sent = publisher.Sent.Should().ContainSingle().Subject;
            sent.TargetActorId.Should().Be(StreamingProxyGAgent.ChatLifecycleContinuationRunnerStreamId);
            var request = sent.Event.Should().BeOfType<StreamingProxyChatParticipantReplyRequested>().Subject;
            request.RoomId.Should().Be("room-1");
            request.SessionId.Should().Be("session-1");
            request.ParticipantId.Should().Be("participant-1");
            request.Round.Should().Be(1);
            request.ParticipantIndex.Should().Be(0);
            request.ActiveParticipants.Select(participant => participant.ParticipantId)
                .Should()
                .Equal("participant-1", "participant-2");
        }

        [Fact]
        public async Task GAgent_HandleParticipantReplyObservedRequested_ShouldRecordReplyAndRequestNextParticipant()
        {
            using var provider = AgentCoverageTestSupport.BuildServiceProvider();
            var agent = CreateAgent(provider, "room-1");
            var publisher = new TestRecordingEventPublisher();
            agent.EventPublisher = publisher;

            await SeedTwoParticipantLifecycleAsync(agent);
            publisher.Sent.Clear();
            publisher.Published.Clear();

            await agent.HandleParticipantReplyObservedRequested(new StreamingProxyChatParticipantReplyObservedRequested
            {
                SessionId = "session-1",
                ParticipantId = "participant-1",
                Round = 1,
                ParticipantIndex = 0,
                Content = " first reply ",
            });

            var lifecycle = agent.State.ChatLifecycles["session-1"];
            lifecycle.SuccessfulReplyCount.Should().Be(1);
            lifecycle.CurrentRound.Should().Be(1);
            lifecycle.NextParticipantIndex.Should().Be(1);
            agent.State.Messages.Should().Contain(message =>
                message.SenderAgentId == "participant-1" &&
                message.Content == "first reply");
            publisher.Published.OfType<GroupChatMessageEvent>()
                .Should()
                .ContainSingle(message => message.AgentId == "participant-1" && message.Content == "first reply");

            var sent = publisher.Sent.Should().ContainSingle().Subject;
            var next = sent.Event.Should().BeOfType<StreamingProxyChatParticipantReplyRequested>().Subject;
            next.ParticipantId.Should().Be("participant-2");
            next.Round.Should().Be(1);
            next.ParticipantIndex.Should().Be(1);
            next.Transcript.Should().ContainSingle(entry =>
                entry.Speaker == "Participant 1" &&
                entry.Content == "first reply");
        }

        [Fact]
        public async Task GAgent_HandleParticipantReplyObservedRequested_ShouldCompleteTerminal_WhenFinalReplyExhaustsLifecycle()
        {
            using var provider = AgentCoverageTestSupport.BuildServiceProvider();
            var agent = CreateAgent(provider, "room-1");
            var publisher = new TestRecordingEventPublisher();
            agent.EventPublisher = publisher;

            await agent.ActivateAsync();
            await agent.HandleChatRequest(new ChatRequestEvent
            {
                Prompt = "Discuss the roadmap.",
                SessionId = "session-1",
                ScopeId = "scope-1",
                ToolContext = (AgentToolExecutionContext.Empty with
                {
                    Credentials = AgentToolCredentials.Empty with
                    {
                        CredentialRef = "access-token",
                    },
                }).ToPayload(),
            });
            await agent.HandleChatParticipantsResolvedRequested(new StreamingProxyChatParticipantsResolvedRequested
            {
                SessionId = "session-1",
                Participants =
                {
                    new StreamingProxyChatLifecycleParticipant
                    {
                        ParticipantId = "participant-1",
                        DisplayName = "Participant 1",
                    },
                },
            });
            publisher.Sent.Clear();
            publisher.Published.Clear();

            await agent.HandleParticipantReplyObservedRequested(new StreamingProxyChatParticipantReplyObservedRequested
            {
                SessionId = "session-1",
                ParticipantId = "participant-1",
                Round = 1,
                ParticipantIndex = 0,
                Content = " final reply ",
            });

            agent.State.ChatLifecycles.Should().NotContainKey("session-1");
            var terminal = agent.State.TerminalSessions["session-1"];
            terminal.Status.Should().Be(StreamingProxyChatSessionTerminalStatus.Completed);
            terminal.ErrorMessage.Should().BeEmpty();
            agent.State.Messages.Should().Contain(message =>
                message.SenderAgentId == "participant-1" &&
                message.Content == "final reply");
            publisher.Sent.Should().BeEmpty();
            publisher.Published.OfType<GroupChatMessageEvent>()
                .Should()
                .ContainSingle(message => message.AgentId == "participant-1" && message.Content == "final reply");
        }

        [Fact]
        public async Task GAgent_HandleParticipantReplyObservedRequested_ShouldIgnoreStaleCursorObservation()
        {
            using var provider = AgentCoverageTestSupport.BuildServiceProvider();
            var agent = CreateAgent(provider, "room-1");
            var publisher = new TestRecordingEventPublisher();
            agent.EventPublisher = publisher;

            await SeedTwoParticipantLifecycleAsync(agent);
            publisher.Sent.Clear();
            publisher.Published.Clear();

            await agent.HandleParticipantReplyObservedRequested(new StreamingProxyChatParticipantReplyObservedRequested
            {
                SessionId = "session-1",
                ParticipantId = "participant-2",
                Round = 1,
                ParticipantIndex = 1,
                Content = "out of order",
            });

            var lifecycle = agent.State.ChatLifecycles["session-1"];
            lifecycle.SuccessfulReplyCount.Should().Be(0);
            lifecycle.NextParticipantIndex.Should().Be(0);
            agent.State.Messages.Should().ContainSingle(message => message.IsTopic);
            publisher.Sent.Should().BeEmpty();
            publisher.Published.OfType<GroupChatMessageEvent>().Should().BeEmpty();
        }

        [Fact]
        public async Task GAgent_HandleParticipantReplyFailedRequested_ShouldPruneFailedParticipantAndRequestNextActive()
        {
            using var provider = AgentCoverageTestSupport.BuildServiceProvider();
            var agent = CreateAgent(provider, "room-1");
            var publisher = new TestRecordingEventPublisher();
            agent.EventPublisher = publisher;

            await SeedTwoParticipantLifecycleAsync(agent);
            await agent.HandleGroupChatParticipantJoined(new GroupChatParticipantJoinedEvent
            {
                AgentId = "participant-1",
                DisplayName = "Participant 1",
            });
            publisher.Sent.Clear();
            publisher.Published.Clear();

            await agent.HandleParticipantReplyFailedRequested(new StreamingProxyChatParticipantReplyFailedRequested
            {
                SessionId = "session-1",
                ParticipantId = "participant-1",
                Round = 1,
                ParticipantIndex = 0,
                FailureKind = StreamingProxyChatParticipantReplyFailureKind.Error,
                ErrorMessage = "provider failed",
            });

            var lifecycle = agent.State.ChatLifecycles["session-1"];
            lifecycle.Participants[0].Status.Should().Be(StreamingProxyChatLifecycleParticipantStatus.Failed);
            lifecycle.Participants[0].FailedRound.Should().Be(1);
            lifecycle.Participants[0].FailureReason.Should().Be("provider failed");
            lifecycle.NextParticipantIndex.Should().Be(1);
            publisher.Published.OfType<GroupChatParticipantLeftEvent>()
                .Should()
                .ContainSingle(evt => evt.AgentId == "participant-1");

            var sent = publisher.Sent.Should().ContainSingle().Subject;
            var next = sent.Event.Should().BeOfType<StreamingProxyChatParticipantReplyRequested>().Subject;
            next.ParticipantId.Should().Be("participant-2");
            next.ParticipantIndex.Should().Be(1);
            next.ActiveParticipants.Select(participant => participant.ParticipantId)
                .Should()
                .Equal("participant-2");
        }

        [Fact]
        public async Task GAgent_HandleParticipantReplyFailedRequested_ShouldCommitFailedTerminal_WhenAllParticipantsFail()
        {
            using var provider = AgentCoverageTestSupport.BuildServiceProvider();
            var agent = CreateAgent(provider, "room-1");
            var publisher = new TestRecordingEventPublisher();
            agent.EventPublisher = publisher;

            await agent.ActivateAsync();
            await agent.HandleChatRequest(new ChatRequestEvent
            {
                Prompt = "Discuss the roadmap.",
                SessionId = "session-1",
                ScopeId = "scope-1",
                ToolContext = (AgentToolExecutionContext.Empty with
                {
                    Credentials = AgentToolCredentials.Empty with
                    {
                        CredentialRef = "access-token",
                    },
                }).ToPayload(),
            });
            await agent.HandleGroupChatParticipantJoined(new GroupChatParticipantJoinedEvent
            {
                AgentId = "participant-1",
                DisplayName = "Participant 1",
            });
            await agent.HandleChatParticipantsResolvedRequested(new StreamingProxyChatParticipantsResolvedRequested
            {
                SessionId = "session-1",
                Participants =
                {
                    new StreamingProxyChatLifecycleParticipant
                    {
                        ParticipantId = "participant-1",
                        DisplayName = "Participant 1",
                    },
                },
            });
            publisher.Sent.Clear();

            await agent.HandleParticipantReplyFailedRequested(new StreamingProxyChatParticipantReplyFailedRequested
            {
                SessionId = "session-1",
                ParticipantId = "participant-1",
                Round = 1,
                ParticipantIndex = 0,
                FailureKind = StreamingProxyChatParticipantReplyFailureKind.EmptyReply,
                ErrorMessage = "empty reply",
            });

            agent.State.ChatLifecycles.Should().NotContainKey("session-1");
            agent.State.TerminalSessions["session-1"].Status.Should().Be(StreamingProxyChatSessionTerminalStatus.Failed);
            agent.State.TerminalSessions["session-1"].ErrorMessage
                .Should()
                .Be("StreamingProxy chat completed without any participant replies.");
            publisher.Sent.Should().BeEmpty();
        }

        [Fact]
        public async Task ChatLifecycleContinuationRunner_ShouldResolveParticipantsWithoutCommittingTerminalState()
        {
            var roomCommands = new StubRoomCommandService();
            var coordinator = CreateNyxCoordinator(roomCommands);
            var streamProvider = new StubStreamProvider();
            var runner = new StreamingProxyChatLifecycleContinuationRunner(
                streamProvider,
                new StubActorEventSubscriptionProvider(streamProvider),
                coordinator,
                roomCommands,
                NullLogger<StreamingProxyChatLifecycleContinuationRunner>.Instance);

            await runner.RunAsync(
                new StreamingProxyChatLifecycleContinuationRequested
                {
                    RoomId = "room-1",
                    SessionId = "session-1",
                    ScopeId = "scope-1",
                    Prompt = "Discuss the roadmap.",
                    AccessToken = "access-token",
                });

            roomCommands.JoinCommands.Should().HaveCount(3);
            roomCommands.ParticipantsResolvedCommands.Should().ContainSingle(command =>
                command.RoomId == "room-1" &&
                command.SessionId == "session-1" &&
                command.Participants.Count == 3);
            roomCommands.PostMessageCommands.Should().BeEmpty();
            roomCommands.TerminalCommands.Should().BeEmpty();
        }

        [Fact]
        public async Task ChatLifecycleContinuationRunner_ShouldReportParticipantReplyFailureOutcome()
        {
            var roomCommands = new StubRoomCommandService();
            var coordinator = CreateNyxCoordinator(
                roomCommands,
                responseFactory: _ => new LLMResponse { Content = "当前暂时不可用: Service request failed." });
            var streamProvider = new StubStreamProvider();
            var runner = new StreamingProxyChatLifecycleContinuationRunner(
                streamProvider,
                new StubActorEventSubscriptionProvider(streamProvider),
                coordinator,
                roomCommands,
                NullLogger<StreamingProxyChatLifecycleContinuationRunner>.Instance);

            await runner.RunParticipantReplyAsync(new StreamingProxyChatParticipantReplyRequested
            {
                RoomId = "room-1",
                SessionId = "session-1",
                ParticipantId = "participant-1",
                DisplayName = "Participant 1",
                RoutePreference = "/api/v1/proxy/s/openclaw/node-a",
                Round = 1,
                ParticipantIndex = 0,
                Prompt = "Discuss the roadmap.",
                AccessToken = "access-token",
                MaxRounds = 1,
                ActiveParticipants =
                {
                    new StreamingProxyChatLifecycleParticipant
                    {
                        ParticipantId = "participant-1",
                        DisplayName = "Participant 1",
                        RoutePreference = "/api/v1/proxy/s/openclaw/node-a",
                        Status = StreamingProxyChatLifecycleParticipantStatus.Active,
                    },
                },
            });

            roomCommands.PostMessageCommands.Should().BeEmpty();
            roomCommands.TerminalCommands.Should().BeEmpty();
            roomCommands.ReplyFailedCommands.Should().ContainSingle(command =>
                command.RoomId == "room-1" &&
                command.SessionId == "session-1" &&
                command.ParticipantId == "participant-1" &&
                command.FailureKind == StreamingProxyChatParticipantReplyFailureKind.ParticipantUnavailable);
        }

        [Fact]
        public async Task ChatLifecycleContinuationRunner_ShouldReportSuccessfulParticipantReplyObservation()
        {
            var roomCommands = new StubRoomCommandService();
            var coordinator = CreateNyxCoordinator(
                roomCommands,
                responseFactory: _ => new LLMResponse { Content = " useful reply " });
            var streamProvider = new StubStreamProvider();
            var runner = new StreamingProxyChatLifecycleContinuationRunner(
                streamProvider,
                new StubActorEventSubscriptionProvider(streamProvider),
                coordinator,
                roomCommands,
                NullLogger<StreamingProxyChatLifecycleContinuationRunner>.Instance);

            await runner.RunParticipantReplyAsync(new StreamingProxyChatParticipantReplyRequested
            {
                RoomId = "room-1",
                SessionId = "session-1",
                ParticipantId = "participant-1",
                DisplayName = "Participant 1",
                RoutePreference = "/api/v1/proxy/s/openclaw/node-a",
                Round = 2,
                ParticipantIndex = 1,
                Prompt = "Discuss the roadmap.",
                AccessToken = "access-token",
                MaxRounds = 2,
                ActiveParticipants =
                {
                    new StreamingProxyChatLifecycleParticipant
                    {
                        ParticipantId = "participant-1",
                        DisplayName = "Participant 1",
                        RoutePreference = "/api/v1/proxy/s/openclaw/node-a",
                        Status = StreamingProxyChatLifecycleParticipantStatus.Active,
                    },
                },
            });

            roomCommands.ReplyObservedCommands.Should().ContainSingle(command =>
                command.RoomId == "room-1" &&
                command.SessionId == "session-1" &&
                command.ParticipantId == "participant-1" &&
                command.Round == 2 &&
                command.ParticipantIndex == 1 &&
                command.Content == "useful reply");
            roomCommands.ReplyFailedCommands.Should().BeEmpty();
            roomCommands.PostMessageCommands.Should().BeEmpty();
            roomCommands.TerminalCommands.Should().BeEmpty();
        }

        [Fact]
        public async Task ChatLifecycleContinuationRunner_ShouldConsumeTypedContinuationFromRunnerStream()
        {
            var roomCommands = new StubRoomCommandService();
            var coordinator = CreateNyxCoordinator(roomCommands);
            var streamProvider = new StubStreamProvider();
            var runner = new StreamingProxyChatLifecycleContinuationRunner(
                streamProvider,
                new StubActorEventSubscriptionProvider(streamProvider),
                coordinator,
                roomCommands,
                NullLogger<StreamingProxyChatLifecycleContinuationRunner>.Instance);

            await runner.StartAsync(CancellationToken.None);
            await streamProvider
                .GetStream(StreamingProxyGAgent.ChatLifecycleContinuationRunnerStreamId)
                .ProduceAsync(new StreamingProxyChatLifecycleContinuationRequested
                {
                    RoomId = "room-from-message",
                    SessionId = "session-1",
                    ScopeId = "scope-1",
                    Prompt = "Discuss the roadmap.",
                    AccessToken = "access-token",
                });

            roomCommands.ParticipantsResolvedCommands.Should().ContainSingle(command => command.RoomId == "room-from-message");
            await runner.StopAsync(CancellationToken.None);
        }

        [Fact]
        public async Task GAgent_RequestPayloads_ShouldCommitExistingRoomFacts()
        {
            using var provider = AgentCoverageTestSupport.BuildServiceProvider();
            var agent = CreateAgent(provider, "streaming-proxy-agent");
            var publisher = new TestRecordingEventPublisher();
            agent.EventPublisher = publisher;

            await agent.ActivateAsync();
            await agent.HandleParticipantJoinRequested(new StreamingProxyParticipantJoinRequested
            {
                AgentId = "agent-1",
                DisplayName = "Alice",
            });
            await agent.HandleParticipantJoinRequested(new StreamingProxyParticipantJoinRequested
            {
                AgentId = "agent-1",
                DisplayName = "Alice Again",
            });
            await agent.HandleParticipantMessageRequested(new StreamingProxyParticipantMessageRequested
            {
                AgentId = "agent-1",
                AgentName = "Alice",
                Content = "room-owned message",
                SessionId = "session-1",
            });
            await agent.HandleParticipantLeaveRequested(new StreamingProxyParticipantLeaveRequested
            {
                AgentId = "agent-1",
                Reason = "done",
            });
            await agent.HandleParticipantLeaveRequested(new StreamingProxyParticipantLeaveRequested
            {
                AgentId = "missing",
                Reason = "stale",
            });
            await agent.HandleSessionTerminalStateRequested(new StreamingProxySessionTerminalStateRequested
            {
                SessionId = "session-1",
                Status = StreamingProxyChatSessionTerminalStatus.Completed,
            });

            agent.State.Participants.Should().BeEmpty();
            agent.State.Messages.Should().ContainSingle(message =>
                message.SenderAgentId == "agent-1" &&
                message.Content == "room-owned message");
            agent.State.TerminalSessions["session-1"].Status
                .Should()
                .Be(StreamingProxyChatSessionTerminalStatus.Completed);
            agent.State.TerminalSessions["session-1"].TerminalAt.Should().NotBeNull();

            publisher.Published.OfType<GroupChatParticipantJoinedEvent>()
                .Should()
                .ContainSingle(x => x.AgentId == "agent-1" && x.DisplayName == "Alice");
            publisher.Published.OfType<GroupChatMessageEvent>()
                .Should()
                .ContainSingle(x => x.AgentId == "agent-1" && x.Content == "room-owned message");
            publisher.Published.OfType<GroupChatParticipantLeftEvent>()
                .Should()
                .ContainSingle(x => x.AgentId == "agent-1");
        }
}
