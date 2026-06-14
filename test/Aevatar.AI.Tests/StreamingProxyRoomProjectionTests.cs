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

public sealed class StreamingProxyRoomProjectionTests : StreamingProxyTestBase
{
        [Fact]
        public async Task StreamingProxyRoomSubscriptionObservationPort_ShouldAttachNormalizedRoomSessionAndDispose()
        {
            var projectionPort = new StubRoomSessionProjectionPort();
            var observationPort = new StreamingProxyRoomSubscriptionObservationPort(projectionPort);
            await using var sink = new EventChannel<StreamingProxyRoomSessionEnvelope>();

            var attachment = await observationPort.AttachAsync(" room-a ", sink, CancellationToken.None);
            attachment.Should().NotBeNull();
            await observationPort.DetachAndDisposeAsync(attachment!, sink, CancellationToken.None);

            attachment!.ProjectionLease.ActorId.Should().Be("room-a");
            attachment.ProjectionLease.SessionId.Should().Be("room:room-a:subscription");
            projectionPort.AttachExistingSubscriptionCalls.Should().ContainSingle()
                .Which.Should().Be(("room-a", "room:room-a:subscription"));
            projectionPort.AttachCount.Should().Be(1);
            projectionPort.AttachedLeases.Should().ContainSingle(x =>
                x.ActorId == "room-a" &&
                x.SessionId == "room:room-a:subscription");
            projectionPort.DetachCount.Should().Be(1);
            projectionPort.ReleaseCount.Should().Be(0);
        }

        [Fact]
        public async Task StreamingProxyRoomSessionProjectionPort_ShouldAttachOnlyWhenProjectionSessionExists()
        {
            var runtime = new StubActorRuntime();
            runtime.Actors["projection.session.scope:streaming-proxy-room-chat-session:room-a:session-123"] =
                new StubActor("projection.session.scope:streaming-proxy-room-chat-session:room-a:session-123");
            var hub = new RecordingRoomSessionEventHub();
            var port = new StreamingProxyRoomSessionProjectionPort(
                new RecordingRoomSessionReleaseService(),
                hub,
                CreateRoomSessionAttachExistingLookup(runtime));
            await using var sink = new EventChannel<StreamingProxyRoomSessionEnvelope>();

            var attachment = await port.AttachExistingChatProjectionAsync("room-a", "session-123", sink, CancellationToken.None);

            attachment.Should().NotBeNull();
            attachment!.ProjectionLease.ActorId.Should().Be("room-a");
            attachment.ProjectionLease.SessionId.Should().Be("session-123");
            hub.SubscribeCalls.Should().Be(1);
            hub.LastRootActorId.Should().Be("room-a");
            hub.LastSessionId.Should().Be("session-123");
        }

        [Fact]
        public async Task StreamingProxyRoomSessionProjectionPort_ShouldReturnNull_WhenProjectionSessionIsCold()
        {
            var hub = new RecordingRoomSessionEventHub();
            var port = new StreamingProxyRoomSessionProjectionPort(
                new RecordingRoomSessionReleaseService(),
                hub,
                CreateRoomSessionAttachExistingLookup(new StubActorRuntime()));
            await using var sink = new EventChannel<StreamingProxyRoomSessionEnvelope>();

            var attachment = await port.AttachExistingChatProjectionAsync("room-a", "session-123", sink, CancellationToken.None);

            attachment.Should().BeNull();
            hub.SubscribeCalls.Should().Be(0);
        }

        [Fact]
        public void StreamingProxyRoomSessionProjectionPort_ShouldNotExposePublicEnsureProjectionApi()
        {
            typeof(IStreamingProxyRoomSessionProjectionPort)
                .GetMethods()
                .Select(method => method.Name)
                .Should()
                .NotContain(name => name.StartsWith("Ensure", StringComparison.Ordinal));
        }

        [Fact]
        public async Task StreamingProxyRoomSessionEventProjector_ShouldIgnoreDifferentChatSessionEvents()
        {
            var sessionHub = new RecordingRoomSessionEventHub();
            var projector = new StreamingProxyRoomSessionEventProjector(sessionHub);
            var context = new StreamingProxyRoomSessionProjectionContext
            {
                RootActorId = "room-a",
                SessionId = "session-1",
                ProjectionKind = StreamingProxyProjectionKinds.RoomChatSession,
            };

            await projector.ProjectAsync(
                context,
                CreateTopologyEnvelope(new GroupChatMessageEvent
                {
                    AgentId = "agent-2",
                    AgentName = "Bob",
                    Content = "not for this run",
                    SessionId = "session-2",
                }),
                CancellationToken.None);

            sessionHub.Published.Should().BeEmpty();
        }

        [Fact]
        public async Task StreamingProxyRoomSessionEventProjector_ShouldPublishAllRoomEvents_ForSubscriptionScopedSession()
        {
            var sessionHub = new RecordingRoomSessionEventHub();
            var projector = new StreamingProxyRoomSessionEventProjector(sessionHub);
            var context = new StreamingProxyRoomSessionProjectionContext
            {
                RootActorId = "room-a",
                SessionId = "sub-1",
                ProjectionKind = StreamingProxyProjectionKinds.RoomSubscriptionSession,
            };

            await projector.ProjectAsync(
                context,
                CreateTopologyEnvelope(new GroupChatMessageEvent
                {
                    AgentId = "agent-2",
                    AgentName = "Bob",
                    Content = "visible to passive subscribers",
                    SessionId = "session-2",
                }),
                CancellationToken.None);

            var published = sessionHub.Published.Should().ContainSingle().Subject;
            published.RootActorId.Should().Be("room-a");
            published.SessionId.Should().Be("sub-1");
            published.Event.Envelope.Should().NotBeNull();
        }

        [Fact]
        public async Task TerminalProjector_ShouldMaterializeCommittedTerminalSnapshot()
        {
            var writer = new RecordingProjectionWriteDispatcher<StreamingProxyChatSessionTerminalSnapshot>();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddStreamingProxy();
            services.AddSingleton<IProjectionWriteDispatcher<StreamingProxyChatSessionTerminalSnapshot>>(writer);
            await using var provider = services.BuildServiceProvider();

            var projector = provider.GetRequiredService<StreamingProxyChatSessionTerminalProjector>();

            await projector.ProjectAsync(
                new StreamingProxyCurrentStateProjectionContext
                {
                    RootActorId = "room-a",
                    ProjectionKind = StreamingProxyProjectionKinds.CurrentState,
                },
                CreateCommittedEnvelope(
                    new StreamingProxyChatSessionTerminalStateChanged
                    {
                        SessionId = "session-1",
                        Status = StreamingProxyChatSessionTerminalStatus.Completed,
                        TerminalAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                    },
                new StreamingProxyGAgentState
                    {
                        RoomName = "Room A",
                        TerminalSessions =
                        {
                            ["session-1"] = new StreamingProxyChatSessionTerminalRecord
                            {
                                SessionId = "session-1",
                                Status = StreamingProxyChatSessionTerminalStatus.Completed,
                                TerminalAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                            },
                },
                },
                version: 12),
                CancellationToken.None);

            writer.Upserts.Should().ContainSingle();
            var snapshot = writer.Upserts[0];
            snapshot.Should().NotBeNull();
            snapshot.ActorId.Should().Be("room-a");
            snapshot.RootActorId.Should().Be("room-a");
            snapshot.SessionId.Should().Be("session-1");
            snapshot.StateVersion.Should().Be(12);
            snapshot.Status.Should().Be(StreamingProxyChatSessionTerminalStatus.Completed);
        }

        [Fact]
        public async Task TerminalProjector_ShouldIgnoreNonTerminalCommittedEvents()
        {
            var writer = new RecordingProjectionWriteDispatcher<StreamingProxyChatSessionTerminalSnapshot>();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddStreamingProxy();
            services.AddSingleton<IProjectionWriteDispatcher<StreamingProxyChatSessionTerminalSnapshot>>(writer);
            await using var provider = services.BuildServiceProvider();

            var projector = provider.GetRequiredService<StreamingProxyChatSessionTerminalProjector>();

            await projector.ProjectAsync(
                new StreamingProxyCurrentStateProjectionContext
                {
                    RootActorId = "room-a",
                    ProjectionKind = StreamingProxyProjectionKinds.CurrentState,
                },
                CreateCommittedEnvelope(
                    new GroupChatMessageEvent
                    {
                        AgentId = "agent-1",
                        AgentName = "Alice",
                        Content = "hello",
                        SessionId = "session-1",
                    },
                new StreamingProxyGAgentState
                    {
                        RoomName = "Room A",
                    },
                version: 13),
                CancellationToken.None);

            writer.Upserts.Should().BeEmpty();
        }

        [Fact]
        public async Task RoomParticipantsProjector_ShouldMaterializeJoinedAndLeftParticipantsFromRoomState()
        {
            var writer = new RecordingProjectionWriteDispatcher<StreamingProxyRoomParticipantsSnapshot>();
            var projector = new StreamingProxyRoomParticipantsProjector(writer, new SystemProjectionClock());
            var context = new StreamingProxyCurrentStateProjectionContext
            {
                RootActorId = "room-a",
                ProjectionKind = StreamingProxyProjectionKinds.CurrentState,
            };

            await projector.ProjectAsync(
                context,
                CreateCommittedEnvelope(
                    new GroupChatParticipantJoinedEvent
                    {
                        AgentId = "agent-1",
                        DisplayName = "Alice",
                    },
                new StreamingProxyGAgentState
                    {
                        Participants =
                        {
                            new StreamingProxyParticipant
                            {
                                AgentId = "agent-1",
                                DisplayName = "Alice",
                                JoinedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                            },
                },
                },
                version: 6),
                CancellationToken.None);

            writer.Upserts.Should().ContainSingle();
            var joinedSnapshot = writer.Upserts[0];
            joinedSnapshot.Id.Should().Be("room-a");
            joinedSnapshot.ActorId.Should().Be("room-a");
            joinedSnapshot.RootActorId.Should().Be("room-a");
            joinedSnapshot.StateVersion.Should().Be(6);
            joinedSnapshot.Participants.Should().ContainSingle(x =>
                x.AgentId == "agent-1" && x.DisplayName == "Alice");

            await projector.ProjectAsync(
                context,
                CreateCommittedEnvelope(
                    new GroupChatParticipantLeftEvent { AgentId = "agent-1" },
                new StreamingProxyGAgentState(),
                    version: 7),
                CancellationToken.None);

            writer.Upserts.Should().HaveCount(2);
            var leftSnapshot = writer.Upserts[1];
            leftSnapshot.StateVersion.Should().Be(7);
            leftSnapshot.Participants.Should().BeEmpty();
        }

        [Fact]
        public async Task RoomParticipantsProjector_ShouldIgnoreNonParticipantRoomEvents()
        {
            var writer = new RecordingProjectionWriteDispatcher<StreamingProxyRoomParticipantsSnapshot>();
            var projector = new StreamingProxyRoomParticipantsProjector(writer, new SystemProjectionClock());

            await projector.ProjectAsync(
                new StreamingProxyCurrentStateProjectionContext
                {
                    RootActorId = "room-a",
                    ProjectionKind = StreamingProxyProjectionKinds.CurrentState,
                },
                CreateCommittedEnvelope(
                    new GroupChatMessageEvent
                    {
                        AgentId = "agent-1",
                        AgentName = "Alice",
                        Content = "hello",
                        SessionId = "session-1",
                    },
                new StreamingProxyGAgentState
                    {
                        Participants =
                        {
                            new StreamingProxyParticipant
                            {
                                AgentId = "agent-1",
                                DisplayName = "Alice",
                                JoinedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                            },
                },
                },
                version: 8),
                CancellationToken.None);

            writer.Upserts.Should().BeEmpty();
        }
}
