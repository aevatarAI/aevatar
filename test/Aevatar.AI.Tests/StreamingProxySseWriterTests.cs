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

public sealed class StreamingProxySseWriterTests : StreamingProxyTestBase
{
        [Fact]
        public async Task MapAndWriteRoomSessionEventAsync_ShouldEmitRunFinished_ForObservedTerminalCompletion()
        {
            var context = new DefaultHttpContext();
            context.Response.Body = new MemoryStream();
            var writer = new StreamingProxySseWriter(context.Response);
            await AgentCoverageTestSupport.InvokeAsync(writer, "StartAsync", CancellationToken.None);

            var signal = await WriteRoomSessionEventAsync(
                new StreamingProxyRoomSessionEnvelope
                {
                    Envelope = CreateCommittedEnvelope(
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
                version: 22),
                },
                writer);

            signal.Should().Be(StreamingProxyStreamSignal.RunFinished);
            context.Response.Body.Position = 0;
            var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
            body.Should().Contain("RUN_FINISHED");
        }

        [Fact]
        public async Task MapAndWriteRoomSessionEventAsync_ShouldWriteTopicAndAgentFrames()
        {
            var context = new DefaultHttpContext();
            context.Response.Body = new MemoryStream();
            var writer = AgentCoverageTestSupport.CreateNonPublicInstance(
                typeof(StreamingProxyGAgent).Assembly,
                "Aevatar.GAgents.StreamingProxy.StreamingProxySseWriter",
                context.Response);

            var methodCalls = new[]
            {
                CreateTopologyEnvelope(new GroupChatTopicEvent { Prompt = "topic", SessionId = "s1" }),
                CreateTopologyEnvelope(new GroupChatMessageEvent { AgentId = "a1", AgentName = "A1", Content = "hi", SessionId = "s1" }),
                CreateTopologyEnvelope(new GroupChatParticipantJoinedEvent { AgentId = "a1", DisplayName = "A1" }),
                CreateTopologyEnvelope(new GroupChatParticipantLeftEvent { AgentId = "a1" }),
            };

            foreach (var envelope in methodCalls)
            {
                await WriteRoomSessionEventAsync(
                    new StreamingProxyRoomSessionEnvelope { Envelope = envelope },
                writer);
            }

            context.Response.Body.Position = 0;
            var body = new StreamReader(context.Response.Body).ReadToEnd();
            body.Should().Contain("TOPIC_STARTED");
            body.Should().Contain("AGENT_MESSAGE");
            body.Should().Contain("PARTICIPANT_JOINED");
            body.Should().Contain("PARTICIPANT_LEFT");
        }

        [Fact]
        public async Task MapAndWriteRoomSessionEventAsync_ShouldWriteCommittedObservedRoomFrames()
        {
            var context = new DefaultHttpContext();
            context.Response.Body = new MemoryStream();
            var writer = AgentCoverageTestSupport.CreateNonPublicInstance(
                typeof(StreamingProxyGAgent).Assembly,
                "Aevatar.GAgents.StreamingProxy.StreamingProxySseWriter",
                context.Response);

            var methodCalls = new[]
            {
                CreateCommittedEnvelope(
                    new GroupChatTopicEvent { Prompt = "topic", SessionId = "s1" },
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
                                Content = "topic",
                                Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                                IsTopic = true,
                            },
                },
                },
                version: 1),
                CreateCommittedEnvelope(
                    new GroupChatMessageEvent { AgentId = "a1", AgentName = "A1", Content = "hi", SessionId = "s1" },
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
                                Content = "topic",
                                Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                                IsTopic = true,
                            },
                new StreamingProxyChatMessage
                            {
                                Sequence = 2,
                                SenderAgentId = "a1",
                                SenderName = "A1",
                                Content = "hi",
                                Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                            },
                },
                },
                version: 2),
            };

            foreach (var envelope in methodCalls)
            {
                await WriteRoomSessionEventAsync(
                    new StreamingProxyRoomSessionEnvelope { Envelope = envelope },
                writer);
            }

            context.Response.Body.Position = 0;
            var body = new StreamReader(context.Response.Body).ReadToEnd();
            body.Should().Contain("TOPIC_STARTED");
            body.Should().Contain("AGENT_MESSAGE");
            body.Should().Contain("topic");
            body.Should().Contain("hi");
        }

        [Fact]
        public async Task MapAndWriteRoomSessionEventAsync_ShouldIgnoreDirectInboundEvents()
        {
            var context = new DefaultHttpContext();
            context.Response.Body = new MemoryStream();
            var writer = AgentCoverageTestSupport.CreateNonPublicInstance(
                typeof(StreamingProxyGAgent).Assembly,
                "Aevatar.GAgents.StreamingProxy.StreamingProxySseWriter",
                context.Response);

            await WriteRoomSessionEventAsync(
                new StreamingProxyRoomSessionEnvelope
                {
                    Envelope = new EventEnvelope
                    {
                        Payload = Any.Pack(new GroupChatMessageEvent
                        {
                            AgentId = "a1",
                            AgentName = "A1",
                            Content = "hi",
                            SessionId = "s1",
                        }),
                        Route = EnvelopeRouteSemantics.CreateDirect("api", "room-1"),
                    },
                },
                writer);

            context.Response.Body.Position = 0;
            var body = new StreamReader(context.Response.Body).ReadToEnd();
            body.Should().BeEmpty();
        }

        [Fact]
        public async Task StreamingProxySseWriter_ShouldStartStream_AndSerializeRoomFrames()
        {
            var context = new DefaultHttpContext();
            context.Response.Body = new MemoryStream();
            var writer = AgentCoverageTestSupport.CreateNonPublicInstance(
                typeof(StreamingProxyGAgent).Assembly,
                "Aevatar.GAgents.StreamingProxy.StreamingProxySseWriter",
                context.Response);

            await AgentCoverageTestSupport.InvokeAsync(writer, "WriteRoomCreatedAsync", "room-1", "Main Room", CancellationToken.None);
            await AgentCoverageTestSupport.InvokeAsync(writer, "WriteAgentMessageAsync", "agent-1", "Alice", "hello", 7L, CancellationToken.None);
            await AgentCoverageTestSupport.InvokeAsync(writer, "WriteRunErrorAsync", "boom", CancellationToken.None);

            AgentCoverageTestSupport.GetBooleanProperty(writer, "Started").Should().BeTrue();
            context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
            context.Response.Headers.ContentType.ToString().Should().Be("text/event-stream; charset=utf-8");
            context.Response.Body.Position = 0;
            var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
            body.Should().Contain("ROOM_CREATED");
            body.Should().Contain("AGENT_MESSAGE");
            body.Should().Contain("\"sequence\":7");
            body.Should().Contain("RUN_ERROR");
        }
}
