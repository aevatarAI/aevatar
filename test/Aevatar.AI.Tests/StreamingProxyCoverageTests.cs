using System.IO;
using System.Reflection;
using System.Security.Claims;
using System.Threading.Channels;
using Aevatar.Foundation.Abstractions;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Studio.Application.Studio.Abstractions;
using StreamingProxyParticipant = Aevatar.Studio.Application.Studio.Abstractions.StreamingProxyParticipant;
using Google.Protobuf;
using Any = Google.Protobuf.WellKnownTypes.Any;
using Google.Protobuf.WellKnownTypes;
using Aevatar.GAgents.StreamingProxy;
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

public class StreamingProxyCoverageTests
{
    [Fact]
    public void AddStreamingProxy_ShouldRegisterSingletonCoordinator()
    {
        var services = new ServiceCollection();
        services.AddStreamingProxy();

        var coordinatorDescriptor = services.FirstOrDefault(d =>
            d.ServiceType == typeof(StreamingProxyNyxParticipantCoordinator));
        var projectionDescriptor = services.FirstOrDefault(d =>
            d.ServiceType == typeof(IStreamingProxyRoomSessionProjectionPort));
        var terminalQueryDescriptor = services.FirstOrDefault(d =>
            d.ServiceType == typeof(IStreamingProxyChatSessionTerminalQueryPort));

        coordinatorDescriptor.Should().NotBeNull();
        coordinatorDescriptor!.Lifetime.Should().Be(ServiceLifetime.Singleton);
        projectionDescriptor.Should().NotBeNull();
        projectionDescriptor!.Lifetime.Should().Be(ServiceLifetime.Singleton);
        terminalQueryDescriptor.Should().NotBeNull();
        terminalQueryDescriptor!.Lifetime.Should().Be(ServiceLifetime.Singleton);
    }

    [Fact]
    public void MapStreamingProxyEndpoints_ShouldRegisterExpectedRoutes()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        var app = builder.Build();
        var routeBuilder = (IEndpointRouteBuilder)app;

        app.MapStreamingProxyEndpoints();

        var routes = routeBuilder.DataSources
            .SelectMany(x => x.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(x => x.RoutePattern.RawText)
            .ToHashSet(StringComparer.Ordinal);

        routes.Should().Contain("/api/scopes/{scopeId}/streaming-proxy/rooms");
        routes.Should().Contain("/api/scopes/{scopeId}/streaming-proxy/rooms/{roomId}:chat");
        routes.Should().Contain("/api/scopes/{scopeId}/streaming-proxy/rooms/{roomId}/messages");
        routes.Should().Contain("/api/scopes/{scopeId}/streaming-proxy/rooms/{roomId}/messages:stream");
        routes.Should().Contain("/api/scopes/{scopeId}/streaming-proxy/rooms/{roomId}/participants");
    }

    [Fact]
    public async Task HandleCreateRoomAsync_ShouldCreateRoomAndInitActor()
    {
        var actorStore = new StubGAgentActorStore();
        var runtime = new StubActorRuntime();
        var request = new CreateRoomRequest("Project X");

        var result = await InvokeResultAsync(
            "HandleCreateRoomAsync",
            CreateScopedHttpContext(),
            "scope-a",
            request,
            actorStore,
            runtime,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status200OK);
        response.Body.Should().Contain("roomName");
        actorStore.AddedActors.Should().ContainSingle(x =>
            x.scopeId == "scope-a" &&
            x.gagentType == StreamingProxyDefaults.GAgentTypeName);
        runtime.CreateCalls.Should().ContainSingle();
        runtime.CreateCalls[0].agentType.Should().Be(typeof(StreamingProxyGAgent));
    }

    [Fact]
    public async Task HandleListRoomsAsync_ShouldReturnRoomsForScope()
    {
        var actorStore = new StubGAgentActorStore();
        actorStore.Groups.Add(new GAgentActorGroup(
            StreamingProxyDefaults.GAgentTypeName,
            new[] { "room-001" }));

        var result = await InvokeResultAsync(
            "HandleListRoomsAsync",
            CreateScopedHttpContext(),
            "scope-a",
            actorStore,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status200OK);
        response.Body.Should().Contain("room-001");
    }

    [Fact]
    public async Task HandleDeleteRoomAsync_ShouldReturnOk_AndRemoveFromBothStores()
    {
        var actorStore = new StubGAgentActorStore();
        var participantStore = new StubParticipantStore();

        var result = await InvokeResultAsync(
            "HandleDeleteRoomAsync",
            CreateScopedHttpContext(),
            "scope-a",
            "room-1",
            actorStore,
            actorStore,
            participantStore,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status200OK);
        actorStore.RemovedActors.Should().ContainSingle(x =>
            x.scopeId == "scope-a" &&
            x.gagentType == StreamingProxyDefaults.GAgentTypeName && x.actorId == "room-1");
        participantStore.RemovedRooms.Should().ContainSingle(x => x == "room-1");
    }

    [Fact]
    public async Task HandleChatAsync_ShouldRejectEmptyPrompt()
    {
        var context = CreateScopedHttpContext();
        var runtime = new StubActorRuntime();
        var projectionPort = new StubRoomSessionProjectionPort();
        var durableCompletionResolver = new StreamingProxyChatDurableCompletionResolver(new StubTerminalQueryPort());
        var participantStore = new StubParticipantStore();
        var actorStore = new StubGAgentActorStore();
        var coordinator = CreateNyxParticipantCoordinator();

        var method = typeof(StreamingProxyEndpoints).GetMethod(
            "HandleChatAsync",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var task = method.Invoke(null, [context, "scope-a", "room-a", new ChatTopicRequest(null), runtime, actorStore, projectionPort, durableCompletionResolver, participantStore, coordinator, NullLoggerFactory.Instance, CancellationToken.None]);
        await InvokeTaskAsync(task);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task HandleChatAsync_ShouldRejectMismatchedAuthenticatedScope()
    {
        var context = CreateScopedHttpContext("scope-b");
        context.Response.Body = new MemoryStream();
        var runtime = new StubActorRuntime(new List<IActor> { new StubActor("room-a") });
        var projectionPort = new StubRoomSessionProjectionPort();
        var durableCompletionResolver = new StreamingProxyChatDurableCompletionResolver(new StubTerminalQueryPort());
        var participantStore = new StubParticipantStore();
        var actorStore = new StubGAgentActorStore();
        var coordinator = CreateNyxParticipantCoordinator();

        var method = typeof(StreamingProxyEndpoints).GetMethod(
            "HandleChatAsync",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var task = method.Invoke(
            null,
            [context, "scope-a", "room-a", new ChatTopicRequest("hello"), runtime, actorStore, projectionPort, durableCompletionResolver, participantStore, coordinator, NullLoggerFactory.Instance, CancellationToken.None]);
        await InvokeTaskAsync(task);

        context.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        body.Should().Contain("SCOPE_ACCESS_DENIED");
        body.Should().Contain("Authenticated scope does not match requested scope.");
    }

    [Fact]
    public async Task HandleMessageStreamAsync_ShouldRejectMissingRoom()
    {
        var context = CreateScopedHttpContext();
        var runtime = new StubActorRuntime();
        var projectionPort = new StubRoomSessionProjectionPort();
        var actorStore = new StubGAgentActorStore();
        var method = typeof(StreamingProxyEndpoints).GetMethod(
            "HandleMessageStreamAsync",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        var task = method.Invoke(
            null,
            [context, "scope-a", "missing", runtime, actorStore, projectionPort, NullLoggerFactory.Instance, CancellationToken.None]);
        await InvokeTaskAsync(task);

        context.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task HandleMessageStreamAsync_ShouldAttachProjectionSession_AndWriteRoomEvents()
    {
        var context = CreateScopedHttpContext();
        context.Response.Body = new MemoryStream();
        var runtime = new StubActorRuntime(new List<IActor> { new StubActor("room-a") });
        var projectionPort = new StubRoomSessionProjectionPort();
        var actorStore = new StubGAgentActorStore();
        using var cts = new CancellationTokenSource();

        var method = typeof(StreamingProxyEndpoints).GetMethod(
            "HandleMessageStreamAsync",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var task = InvokeTaskAsync(method.Invoke(
            null,
            [context, "scope-a", "room-a", runtime, actorStore, projectionPort, NullLoggerFactory.Instance, cts.Token]));

        await projectionPort.Attached.Task;
        await projectionPort.PublishAsync(
            CreateCommittedEnvelope(
                new GroupChatMessageEvent
                {
                    AgentId = "agent-1",
                    AgentName = "Alice",
                    Content = "hello from projection",
                    SessionId = "stream-session",
                },
                new StreamingProxyGAgentState
                {
                    RoomName = "Room A",
                    Messages =
                    {
                        new StreamingProxyChatMessage
                        {
                            Sequence = 1,
                            SenderAgentId = "agent-1",
                            SenderName = "Alice",
                            Content = "hello from projection",
                            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                        },
                    },
                },
                version: 2));

        cts.Cancel();
        await task;

        projectionPort.EnsureCalls.Should().ContainSingle(x =>
            x.actorId == "room-a" &&
            x.projectionKind == StreamingProxyProjectionKinds.RoomSubscriptionSession);
        projectionPort.EnsureCalls.Single().sessionId.Should().NotBeNullOrWhiteSpace();
        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        body.Should().Contain("AGENT_MESSAGE");
        body.Should().Contain("hello from projection");
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
        published.ScopeId.Should().Be("room-a");
        published.SessionId.Should().Be("sub-1");
        published.Event.Envelope.Should().NotBeNull();
    }

    [Fact]
    public async Task HandleChatAsync_ShouldAttachProjectionSession_AndEmitRunFinished()
    {
        var context = CreateScopedHttpContext();
        context.Response.Body = new MemoryStream();
        var runtime = new StubActorRuntime(new List<IActor> { new StubActor("room-a") });
        var projectionPort = new StubRoomSessionProjectionPort();
        var durableCompletionResolver = new StreamingProxyChatDurableCompletionResolver(
            new StubTerminalQueryPort(StreamingProxyChatSessionTerminalStatus.Completed));
        var participantStore = new StubParticipantStore();
        var actorStore = new StubGAgentActorStore();
        var coordinator = CreateNyxParticipantCoordinator();
        var request = new ChatTopicRequest("Discuss webhook relay", "session-123");

        var method = typeof(StreamingProxyEndpoints).GetMethod(
            "HandleChatAsync",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var task = InvokeTaskAsync(method.Invoke(
            null,
            [context, "scope-a", "room-a", request, runtime, actorStore, projectionPort, durableCompletionResolver, participantStore, coordinator, NullLoggerFactory.Instance, CancellationToken.None]));

        await projectionPort.Attached.Task;
        await projectionPort.PublishAsync(
            CreateCommittedEnvelope(
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
                version: 2));
        await projectionPort.PublishAsync(
            CreateCommittedEnvelope(
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
                version: 3));

        await task;

        projectionPort.EnsureCalls.Should().ContainSingle(x =>
            x.actorId == "room-a" && x.sessionId == "session-123");
        ((StubActor)runtime.Actors["room-a"]).HandleEventCalls.Should().Be(2);

        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        body.Should().Contain("TOPIC_STARTED");
        body.Should().Contain("AGENT_MESSAGE");
        body.Should().Contain("RUN_FINISHED");
    }

    [Fact]
    public async Task HandleChatAsync_ShouldPublishFailedTerminalState_WhenCancelled()
    {
        var context = CreateScopedHttpContext();
        context.Response.Body = new MemoryStream();
        var actor = new StubActor("room-a");
        var runtime = new StubActorRuntime(new List<IActor> { actor });
        var projectionPort = new StubRoomSessionProjectionPort();
        var durableCompletionResolver = new StreamingProxyChatDurableCompletionResolver(new StubTerminalQueryPort());
        var participantStore = new StubParticipantStore();
        var actorStore = new StubGAgentActorStore();
        var coordinator = CreateNyxParticipantCoordinator();
        using var cts = new CancellationTokenSource();

        var method = typeof(StreamingProxyEndpoints).GetMethod(
            "HandleChatAsync",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var task = InvokeTaskAsync(method.Invoke(
            null,
            [context, "scope-a", "room-a", new ChatTopicRequest("Cancel me", "session-cancel"), runtime, actorStore, projectionPort, durableCompletionResolver, participantStore, coordinator, NullLoggerFactory.Instance, cts.Token]));

        await projectionPort.Attached.Task;
        cts.Cancel();
        await task;

        actor.ReceivedEnvelopes.Should().Contain(envelope =>
            envelope.Payload.Is(StreamingProxyChatSessionTerminalStateChanged.Descriptor) &&
            envelope.Payload.Unpack<StreamingProxyChatSessionTerminalStateChanged>().SessionId == "session-cancel" &&
            envelope.Payload.Unpack<StreamingProxyChatSessionTerminalStateChanged>().Status == StreamingProxyChatSessionTerminalStatus.Failed &&
            envelope.Payload.Unpack<StreamingProxyChatSessionTerminalStateChanged>().ErrorMessage == "StreamingProxy chat was cancelled before completion.");
    }

    [Fact]
    public async Task FinalizeFromLiveOrDurableCompletionAsync_ShouldUseSingleDurableFallback_AfterLiveTimeout()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var writer = AgentCoverageTestSupport.CreateNonPublicInstance(
            typeof(StreamingProxyGAgent).Assembly,
            "Aevatar.GAgents.StreamingProxy.StreamingProxySseWriter",
            context.Response);
        var terminalQueryPort = new StubTerminalQueryPort(StreamingProxyChatSessionTerminalStatus.Completed);
        var durableCompletionResolver = new StreamingProxyChatDurableCompletionResolver(terminalQueryPort);
        var signalChannel = Channel.CreateUnbounded<StreamingProxyStreamSignal>();
        signalChannel.Writer.TryComplete();

        var method = typeof(StreamingProxyEndpoints).GetMethod(
            "FinalizeFromLiveOrDurableCompletionAsync",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        await InvokeTaskAsync(method.Invoke(
            null,
            ["room-a", "session-123", signalChannel.Reader, durableCompletionResolver, writer, TimeSpan.FromMilliseconds(50), CancellationToken.None]));

        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        body.Should().Contain("RUN_FINISHED");
        body.Should().NotContain("RUN_ERROR");
        terminalQueryPort.QueryCount.Should().Be(1);
    }

    [Fact]
    public async Task FinalizeFromLiveOrDurableCompletionAsync_ShouldEmitRunError_WhenTerminalStateNeverAppears()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var writer = AgentCoverageTestSupport.CreateNonPublicInstance(
            typeof(StreamingProxyGAgent).Assembly,
            "Aevatar.GAgents.StreamingProxy.StreamingProxySseWriter",
            context.Response);
        var durableCompletionResolver = new StreamingProxyChatDurableCompletionResolver(new StubTerminalQueryPort());
        var signalChannel = Channel.CreateUnbounded<StreamingProxyStreamSignal>();
        signalChannel.Writer.TryComplete();

        var method = typeof(StreamingProxyEndpoints).GetMethod(
            "FinalizeFromLiveOrDurableCompletionAsync",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        await InvokeTaskAsync(method.Invoke(
            null,
            [
                "room-a",
                "session-123",
                signalChannel.Reader,
                durableCompletionResolver,
                writer,
                TimeSpan.FromMilliseconds(50),
                CancellationToken.None,
            ]));

        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        body.Should().Contain("RUN_ERROR");
        body.Should().Contain("StreamingProxy completion timed out.");
    }

    [Fact]
    public void DetermineParticipantTerminalState_ShouldFail_WhenNoRepliesWereProduced()
    {
        var method = typeof(StreamingProxyEndpoints).GetMethod(
            "DetermineParticipantTerminalState",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        var failed = ((StreamingProxyChatSessionTerminalStatus Status, string? ErrorMessage))method.Invoke(null, [0])!;
        failed.Status.Should().Be(StreamingProxyChatSessionTerminalStatus.Failed);
        failed.ErrorMessage.Should().Be("StreamingProxy chat completed without any participant replies.");

        var completed = ((StreamingProxyChatSessionTerminalStatus Status, string? ErrorMessage))method.Invoke(null, [1])!;
        completed.Status.Should().Be(StreamingProxyChatSessionTerminalStatus.Completed);
        completed.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public void DetermineIdleTerminalState_ShouldFail_WhenNoAgentMessageWasObserved()
    {
        var method = typeof(StreamingProxyEndpoints).GetMethod(
            "DetermineIdleTerminalState",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        var failed = ((StreamingProxyChatSessionTerminalStatus Status, string? ErrorMessage))method.Invoke(null, [false])!;
        failed.Status.Should().Be(StreamingProxyChatSessionTerminalStatus.Failed);
        failed.ErrorMessage.Should().Be("StreamingProxy chat timed out without any agent replies.");

        var completed = ((StreamingProxyChatSessionTerminalStatus Status, string? ErrorMessage))method.Invoke(null, [true])!;
        completed.Status.Should().Be(StreamingProxyChatSessionTerminalStatus.Completed);
        completed.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task TryPublishFailedTerminalStateAsync_ShouldEmitFailedTerminalEvent_WhenCompletionIsUnknown()
    {
        var actor = new StubActor("room-a");
        var durableCompletionResolver = new StreamingProxyChatDurableCompletionResolver(new StubTerminalQueryPort());
        var logger = NullLogger.Instance;
        var method = typeof(StreamingProxyEndpoints).GetMethod(
            "TryPublishFailedTerminalStateAsync",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        await InvokeTaskAsync(method.Invoke(
            null,
            [actor, "session-123", "StreamingProxy chat failed before completion.", durableCompletionResolver, logger]));

        actor.ReceivedEnvelopes.Should().Contain(envelope =>
            envelope.Payload.Is(StreamingProxyChatSessionTerminalStateChanged.Descriptor) &&
            envelope.Payload.Unpack<StreamingProxyChatSessionTerminalStateChanged>().SessionId == "session-123" &&
            envelope.Payload.Unpack<StreamingProxyChatSessionTerminalStateChanged>().Status == StreamingProxyChatSessionTerminalStatus.Failed &&
            envelope.Payload.Unpack<StreamingProxyChatSessionTerminalStateChanged>().ErrorMessage == "StreamingProxy chat failed before completion.");
    }

    [Fact]
    public async Task TryPublishFailedTerminalStateAsync_ShouldNotEmitTerminalEvent_WhenCompletionAlreadyVisible()
    {
        var actor = new StubActor("room-a");
        var durableCompletionResolver = new StreamingProxyChatDurableCompletionResolver(
            new StubTerminalQueryPort(StreamingProxyChatSessionTerminalStatus.Completed));
        var logger = NullLogger.Instance;
        var method = typeof(StreamingProxyEndpoints).GetMethod(
            "TryPublishFailedTerminalStateAsync",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        await InvokeTaskAsync(method.Invoke(
            null,
            [actor, "session-123", "StreamingProxy chat failed before completion.", durableCompletionResolver, logger]));

        actor.ReceivedEnvelopes.Should().NotContain(envelope =>
            envelope.Payload.Is(StreamingProxyChatSessionTerminalStateChanged.Descriptor));
    }

    [Fact]
    public async Task TerminalProjector_ShouldMaterializeCommittedTerminalSnapshot()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddStreamingProxy();
        await using var provider = services.BuildServiceProvider();

        var projector = provider
            .GetServices<ICurrentStateProjectionMaterializer<StreamingProxyCurrentStateProjectionContext>>()
            .OfType<StreamingProxyChatSessionTerminalProjector>()
            .Single();
        var queryPort = provider.GetRequiredService<IStreamingProxyChatSessionTerminalQueryPort>();

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

        var snapshot = await queryPort.GetAsync("room-a", "session-1", CancellationToken.None);

        snapshot.Should().NotBeNull();
        snapshot!.ActorId.Should().Be("room-a");
        snapshot.RootActorId.Should().Be("room-a");
        snapshot.SessionId.Should().Be("session-1");
        snapshot.StateVersion.Should().Be(12);
        snapshot.Status.Should().Be(StreamingProxyChatSessionTerminalStatus.Completed);
    }

    [Fact]
    public async Task TerminalProjector_ShouldIgnoreNonTerminalCommittedEvents()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddStreamingProxy();
        await using var provider = services.BuildServiceProvider();

        var projector = provider
            .GetServices<ICurrentStateProjectionMaterializer<StreamingProxyCurrentStateProjectionContext>>()
            .OfType<StreamingProxyChatSessionTerminalProjector>()
            .Single();
        var queryPort = provider.GetRequiredService<IStreamingProxyChatSessionTerminalQueryPort>();

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

        var snapshot = await queryPort.GetAsync("room-a", "session-1", CancellationToken.None);
        snapshot.Should().BeNull();
    }

    [Fact]
    public async Task MapAndWriteEventAsync_ShouldEmitRunFinished_ForObservedTerminalCompletion()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var writer = new StreamingProxySseWriter(context.Response);
        await AgentCoverageTestSupport.InvokeAsync(writer, "StartAsync", CancellationToken.None);

        var method = typeof(StreamingProxyEndpoints).GetMethod(
            "MapAndWriteEventAsync",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var task = (ValueTask<StreamingProxyStreamSignal?>)method.Invoke(
            null,
            [
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
                    version: 22),
                writer,
            ])!;

        var signal = await task;

        signal.Should().Be(StreamingProxyStreamSignal.RunFinished);
        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        body.Should().Contain("RUN_FINISHED");
    }

    [Fact]
    public async Task HandlePostMessageAsync_ShouldRejectMissingFieldsAndReturnAccepted()
    {
        var result = await InvokeResultAsync(
            "HandlePostMessageAsync",
            CreateScopedHttpContext(),
            "scope-a",
            "room-a",
            new PostMessageRequest(null, "name", "content"),
            new StubActorRuntime(),
            new StubGAgentActorStore(),
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);

        result = await InvokeResultAsync(
            "HandlePostMessageAsync",
            CreateScopedHttpContext(),
            "scope-a",
            "missing-room",
            new PostMessageRequest("agent", null, "content"),
            new StubActorRuntime(),
            new StubGAgentActorStore(),
            CancellationToken.None);

        response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status404NotFound);

        var runtime = new StubActorRuntime(new List<IActor> { new StubActor("room-a") });
        result = await InvokeResultAsync(
            "HandlePostMessageAsync",
            CreateScopedHttpContext(),
            "scope-a",
            "room-a",
            new PostMessageRequest("agent", null, "content"),
            runtime,
            new StubGAgentActorStore(),
            CancellationToken.None);

        response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status200OK);
        ((StubActor)runtime.Actors["room-a"]).HandleEventCalls.Should().Be(1);
    }

    [Fact]
    public async Task HandleJoinAsync_ShouldRejectMissingAgentIdAndAddParticipant()
    {
        var participantStore = new StubParticipantStore();
        var runtime = new StubActorRuntime(new List<IActor> { new StubActor("room-a") });
        var actorStore = new StubGAgentActorStore();

        var result = await InvokeResultAsync(
            "HandleJoinAsync",
            CreateScopedHttpContext(),
            "scope-a",
            "room-a",
            new JoinRoomRequest(null, null),
            runtime,
            actorStore,
            participantStore,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);

        var joinRequest = new JoinRoomRequest("agent-1", "Alice");
        result = await InvokeResultAsync(
            "HandleJoinAsync",
            CreateScopedHttpContext(),
            "scope-a",
            "room-a",
            joinRequest,
            runtime,
            actorStore,
            participantStore,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status200OK);
        participantStore.AddedParticipants.Should().ContainSingle(x =>
            x.roomId == "room-a" && x.agentId == "agent-1" && x.displayName == "Alice");
        ((StubActor)runtime.Actors["room-a"]).HandleEventCalls.Should().Be(1);
    }

    [Fact]
    public async Task MapAndWriteEventAsync_ShouldWriteTopicAndAgentFrames()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var writer = AgentCoverageTestSupport.CreateNonPublicInstance(
            typeof(StreamingProxyGAgent).Assembly,
            "Aevatar.GAgents.StreamingProxy.StreamingProxySseWriter",
            context.Response);

            var method = typeof(StreamingProxyEndpoints).GetMethod(
                "MapAndWriteEventAsync",
                BindingFlags.NonPublic | BindingFlags.Static)!;
        var methodCalls = new[]
        {
            CreateTopologyEnvelope(new GroupChatTopicEvent { Prompt = "topic", SessionId = "s1" }),
            CreateTopologyEnvelope(new GroupChatMessageEvent { AgentId = "a1", AgentName = "A1", Content = "hi", SessionId = "s1" }),
            CreateTopologyEnvelope(new GroupChatParticipantJoinedEvent { AgentId = "a1", DisplayName = "A1" }),
            CreateTopologyEnvelope(new GroupChatParticipantLeftEvent { AgentId = "a1" }),
        };

        foreach (var envelope in methodCalls)
        {
            var result = method.Invoke(null, [envelope, writer])!;
            switch (result)
            {
                case ValueTask valueTask:
                    await valueTask;
                    break;
                case Task task:
                    await task;
                    break;
                default:
                    break;
            }
        }

        context.Response.Body.Position = 0;
        var body = new StreamReader(context.Response.Body).ReadToEnd();
        body.Should().Contain("TOPIC_STARTED");
        body.Should().Contain("AGENT_MESSAGE");
        body.Should().Contain("PARTICIPANT_JOINED");
        body.Should().Contain("PARTICIPANT_LEFT");
    }

    [Fact]
    public async Task MapAndWriteEventAsync_ShouldWriteCommittedObservedRoomFrames()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var writer = AgentCoverageTestSupport.CreateNonPublicInstance(
            typeof(StreamingProxyGAgent).Assembly,
            "Aevatar.GAgents.StreamingProxy.StreamingProxySseWriter",
            context.Response);

        var method = typeof(StreamingProxyEndpoints).GetMethod(
            "MapAndWriteEventAsync",
            BindingFlags.NonPublic | BindingFlags.Static)!;
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
            var result = method.Invoke(null, [envelope, writer])!;
            switch (result)
            {
                case ValueTask valueTask:
                    await valueTask;
                    break;
                case Task task:
                    await task;
                    break;
                default:
                    break;
            }
        }

        context.Response.Body.Position = 0;
        var body = new StreamReader(context.Response.Body).ReadToEnd();
        body.Should().Contain("TOPIC_STARTED");
        body.Should().Contain("AGENT_MESSAGE");
        body.Should().Contain("topic");
        body.Should().Contain("hi");
    }

    [Fact]
    public async Task MapAndWriteEventAsync_ShouldIgnoreDirectInboundEvents()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var writer = AgentCoverageTestSupport.CreateNonPublicInstance(
            typeof(StreamingProxyGAgent).Assembly,
            "Aevatar.GAgents.StreamingProxy.StreamingProxySseWriter",
            context.Response);

        var method = typeof(StreamingProxyEndpoints).GetMethod(
            "MapAndWriteEventAsync",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        var result = method.Invoke(null, [new EventEnvelope
        {
            Payload = Any.Pack(new GroupChatMessageEvent
            {
                AgentId = "a1",
                AgentName = "A1",
                Content = "hi",
                SessionId = "s1",
            }),
            Route = EnvelopeRouteSemantics.CreateDirect("api", "room-1"),
        }, writer])!;

        switch (result)
        {
            case ValueTask valueTask:
                await valueTask;
                break;
            case Task task:
                await task;
                break;
        }

        context.Response.Body.Position = 0;
        var body = new StreamReader(context.Response.Body).ReadToEnd();
        body.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleListParticipantsAsync_ShouldReturnStoreParticipants()
    {
        var participantStore = new StubParticipantStore();
        participantStore.Participants["room-a"] =
        [
            new StreamingProxyParticipant("agent-1", "Alice", DateTimeOffset.UtcNow),
        ];

        var result = await InvokeResultAsync(
            "HandleListParticipantsAsync",
            CreateScopedHttpContext(),
            "scope-a",
            "room-a",
            new StubGAgentActorStore(),
            participantStore,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status200OK);
        response.Body.Should().Contain("Alice");
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
        state.Messages[1].IsTopic.Should().BeFalse();
        state.Messages[1].SenderAgentId.Should().Be("agent-2");
        state.Messages[1].SenderName.Should().Be("Bob");
        state.Participants.Should().BeEmpty();

        publisher.Published.OfType<GroupChatParticipantJoinedEvent>().Should().HaveCount(2);
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

    [Fact]
    public void GenerateRoomId_ShouldUseStablePrefix_AndProduceUniqueValues()
    {
        var first = StreamingProxyDefaults.GenerateRoomId();
        var second = StreamingProxyDefaults.GenerateRoomId();

        first.Should().StartWith($"{StreamingProxyDefaults.ActorIdPrefix}-");
        second.Should().StartWith($"{StreamingProxyDefaults.ActorIdPrefix}-");
        first.Should().NotBe(second);
    }

    private static StreamingProxyGAgent CreateAgent(IServiceProvider provider, string actorId)
    {
        var agent = new StreamingProxyGAgent
        {
            Services = provider,
            EventSourcingBehaviorFactory = provider.GetRequiredService<IEventSourcingBehaviorFactory<StreamingProxyGAgentState>>(),
        };

        AgentCoverageTestSupport.AssignActorId(agent, actorId);
        return agent;
    }

    private static EventEnvelope CreateTopologyEnvelope(IMessage payload) =>
        new()
        {
            Payload = Any.Pack(payload),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication(
                "streaming-proxy-room",
                TopologyAudience.Parent),
        };

    private static EventEnvelope CreateCommittedEnvelope(
        IMessage payload,
        StreamingProxyGAgentState state,
        long version)
    {
        var eventId = Guid.NewGuid().ToString("N");
        var timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
        return new EventEnvelope
        {
            Id = eventId,
            Timestamp = timestamp,
            Payload = Any.Pack(
                new CommittedStateEventPublished
                {
                    StateEvent = new StateEvent
                    {
                        EventId = eventId,
                        Timestamp = timestamp,
                        Version = version,
                        EventType = payload.Descriptor.FullName,
                        EventData = Any.Pack(payload),
                        AgentId = "room-a",
                    },
                    StateRoot = Any.Pack(state),
                }),
        };
    }

    private static async Task<(int StatusCode, string Body)> ExecuteResultAsync(IResult result)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddLogging()
                .BuildServiceProvider(),
        };

        await using var body = new MemoryStream();
        context.Response.Body = body;

        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        return (context.Response.StatusCode, await new StreamReader(context.Response.Body).ReadToEndAsync());
    }

    private static DefaultHttpContext CreateScopedHttpContext(string claimedScopeId = "scope-a")
    {
        return new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddLogging()
                .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
                .AddSingleton<IHostEnvironment>(new TestHostEnvironment())
                .BuildServiceProvider(),
            User = new ClaimsPrincipal(
                new ClaimsIdentity(
                [
                    new Claim("scope_id", claimedScopeId),
                ],
                authenticationType: "TestAuth")),
        };
    }

    private static async Task<IResult> InvokeResultAsync(string methodName, params object[] args)
    {
        var method = typeof(StreamingProxyEndpoints).GetMethod(
            methodName,
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = method.Invoke(null, args)
            ?? throw new InvalidOperationException($"Method {methodName} returned null.");

        return result switch
        {
            Task<IResult> task => await task,
            _ => throw new InvalidOperationException($"Unexpected return type: {result.GetType()}"),
        };
    }

    private static async Task InvokeTaskAsync(object? result)
    {
        result.Should().NotBeNull();

        switch (result)
        {
            case Task task:
                await task;
                return;
            case ValueTask valueTask:
                await valueTask;
                return;
            default:
                throw new InvalidOperationException($"Unexpected return type: {result!.GetType()}");
        }
    }

    private sealed class StubActorRuntime : IActorRuntime
    {
        public StubActorRuntime(IEnumerable<IActor>? initialActors = null)
        {
            if (initialActors is not null)
            {
                foreach (var actor in initialActors)
                    Actors[actor.Id] = actor;
            }
        }

        public Dictionary<string, IActor> Actors { get; } = [];

        public List<(System.Type agentType, string actorId)> CreateCalls { get; } = [];

        public Task<IActor?> GetAsync(string id) => Task.FromResult<IActor?>(Actors.TryGetValue(id, out var actor) ? actor : null);

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent => CreateAsync(typeof(TAgent), id, ct);

        public Task<IActor> CreateAsync(System.Type agentType, string? id = null, CancellationToken ct = default)
        {
            var actorId = id ?? Guid.NewGuid().ToString("N");
            var actor = new StubActor(actorId);
            Actors[actorId] = actor;
            CreateCalls.Add((agentType, actorId));
            return Task.FromResult<IActor>(actor);
        }

        public Task DestroyAsync(string id, CancellationToken ct = default)
        {
            Actors.Remove(id);
            return Task.CompletedTask;
        }

        public Task<bool> ExistsAsync(string id) => Task.FromResult(Actors.ContainsKey(id));
        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;
        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StubActor : IActor
    {
        public StubActor(string id) => Id = id;

        public int HandleEventCalls { get; private set; }
        public List<EventEnvelope> ReceivedEnvelopes { get; } = [];

        public string Id { get; }

        public IAgent Agent => new StubAgent();

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
        {
            ReceivedEnvelopes.Add(envelope);
            _ = ct;
            HandleEventCalls++;
            return Task.CompletedTask;
        }

        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class StubAgent : IAgent
    {
        public string Id => "agent";
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GetDescriptionAsync() => Task.FromResult("stub");
        public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<System.Type>>([]);
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StubRoomSessionProjectionPort : IStreamingProxyRoomSessionProjectionPort
    {
        private IEventSink<StreamingProxyRoomSessionEnvelope>? _sink;
        private IStreamingProxyRoomSessionProjectionLease? _lease;

        public bool ProjectionEnabled => true;

        public List<(string actorId, string sessionId, string projectionKind)> EnsureCalls { get; } = [];

        public TaskCompletionSource<bool> Attached { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IStreamingProxyRoomSessionProjectionLease?> EnsureRoomProjectionAsync(
            string actorId,
            string sessionId,
            CancellationToken ct = default)
        {
            return EnsureProjectionAsync(actorId, sessionId, StreamingProxyProjectionKinds.RoomChatSession, ct);
        }

        public Task<IStreamingProxyRoomSessionProjectionLease?> EnsureChatProjectionAsync(
            string actorId,
            string sessionId,
            CancellationToken ct = default)
        {
            return EnsureProjectionAsync(actorId, sessionId, StreamingProxyProjectionKinds.RoomChatSession, ct);
        }

        public Task<IStreamingProxyRoomSessionProjectionLease?> EnsureSubscriptionProjectionAsync(
            string actorId,
            string subscriptionId,
            CancellationToken ct = default)
        {
            return EnsureProjectionAsync(actorId, subscriptionId, StreamingProxyProjectionKinds.RoomSubscriptionSession, ct);
        }

        private Task<IStreamingProxyRoomSessionProjectionLease?> EnsureProjectionAsync(
            string actorId,
            string sessionId,
            string projectionKind,
            CancellationToken ct)
        {
            _ = ct;

            EnsureCalls.Add((actorId, sessionId, projectionKind));
            _lease = new StubRoomSessionProjectionLease(actorId, sessionId);
            return Task.FromResult<IStreamingProxyRoomSessionProjectionLease?>(_lease);
        }

        public Task AttachLiveSinkAsync(
            IStreamingProxyRoomSessionProjectionLease lease,
            IEventSink<StreamingProxyRoomSessionEnvelope> sink,
            CancellationToken ct = default)
        {
            _ = ct;
            _lease = lease;
            _sink = sink;
            Attached.TrySetResult(true);
            return Task.CompletedTask;
        }

        public Task DetachLiveSinkAsync(
            IStreamingProxyRoomSessionProjectionLease lease,
            IEventSink<StreamingProxyRoomSessionEnvelope> sink,
            CancellationToken ct = default)
        {
            _ = lease;
            _ = sink;
            _ = ct;
            return Task.CompletedTask;
        }

        public Task ReleaseActorProjectionAsync(
            IStreamingProxyRoomSessionProjectionLease lease,
            CancellationToken ct = default)
        {
            _ = lease;
            _ = ct;
            return Task.CompletedTask;
        }

        public async Task PublishAsync(EventEnvelope envelope, CancellationToken ct = default)
        {
            _ = _lease ?? throw new InvalidOperationException("Projection lease was not created.");
            if (_sink == null)
                throw new InvalidOperationException("Projection sink is not attached.");

            await _sink.PushAsync(
                new StreamingProxyRoomSessionEnvelope
                {
                    Envelope = envelope,
                },
                ct);
        }
    }

    private sealed class RecordingRoomSessionEventHub
        : IProjectionSessionEventHub<StreamingProxyRoomSessionEnvelope>
    {
        public List<(string ScopeId, string SessionId, StreamingProxyRoomSessionEnvelope Event)> Published { get; } = [];

        public Task PublishAsync(
            string scopeId,
            string sessionId,
            StreamingProxyRoomSessionEnvelope evt,
            CancellationToken ct = default)
        {
            _ = ct;
            Published.Add((scopeId, sessionId, evt));
            return Task.CompletedTask;
        }

        public Task<IAsyncDisposable> SubscribeAsync(
            string scopeId,
            string sessionId,
            Func<StreamingProxyRoomSessionEnvelope, ValueTask> handler,
            CancellationToken ct = default)
        {
            _ = scopeId;
            _ = sessionId;
            _ = handler;
            _ = ct;
            return Task.FromResult<IAsyncDisposable>(new NoopAsyncDisposable());
        }
    }

    private sealed class NoopAsyncDisposable : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed record StubRoomSessionProjectionLease(string ActorId, string SessionId)
        : IStreamingProxyRoomSessionProjectionLease;

    private sealed class StubGAgentActorStore :
        IGAgentActorRegistryCommandPort,
        IGAgentActorRegistryQueryPort,
        IScopeResourceAdmissionPort
    {
        public List<GAgentActorGroup> Groups { get; } = [];
        public List<(string scopeId, string gagentType, string actorId)> AddedActors { get; } = [];
        public List<(string scopeId, string gagentType, string actorId)> RemovedActors { get; } = [];

        public Task<GAgentActorRegistrySnapshot> ListActorsAsync(
            string scopeId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new GAgentActorRegistrySnapshot(
                scopeId,
                Groups.AsReadOnly(),
                1,
                DateTimeOffset.Parse("2026-04-27T09:30:00Z"),
                DateTimeOffset.UtcNow));

        public Task<GAgentActorRegistryCommandReceipt> RegisterActorAsync(
            GAgentActorRegistration registration,
            CancellationToken cancellationToken = default)
        {
            AddedActors.Add((registration.ScopeId, registration.GAgentType, registration.ActorId));
            return Task.FromResult(new GAgentActorRegistryCommandReceipt(
                registration,
                GAgentActorRegistryCommandStage.AdmissionVisible));
        }

        public Task<GAgentActorRegistryCommandReceipt> UnregisterActorAsync(
            GAgentActorRegistration registration,
            CancellationToken cancellationToken = default)
        {
            RemovedActors.Add((registration.ScopeId, registration.GAgentType, registration.ActorId));
            return Task.FromResult(new GAgentActorRegistryCommandReceipt(
                registration,
                GAgentActorRegistryCommandStage.AdmissionVisible));
        }

        public Task<ScopeResourceAdmissionResult> AuthorizeTargetAsync(
            ScopeResourceTarget target,
            CancellationToken cancellationToken = default)
            => Task.FromResult(ScopeResourceAdmissionResult.Allowed());
    }

    private sealed class StubParticipantStore : IStreamingProxyParticipantStore
    {
        public Dictionary<string, List<StreamingProxyParticipant>> Participants { get; } = new(StringComparer.Ordinal);
        public List<(string roomId, string agentId, string displayName)> AddedParticipants { get; } = [];
        public List<string> RemovedRooms { get; } = [];

        public Task<IReadOnlyList<StreamingProxyParticipant>> ListAsync(
            string roomId, CancellationToken cancellationToken = default)
        {
            if (Participants.TryGetValue(roomId, out var list))
                return Task.FromResult<IReadOnlyList<StreamingProxyParticipant>>(list.AsReadOnly());
            return Task.FromResult<IReadOnlyList<StreamingProxyParticipant>>([]);
        }

        public Task AddAsync(
            string roomId, string agentId, string displayName,
            CancellationToken cancellationToken = default)
        {
            AddedParticipants.Add((roomId, agentId, displayName));
            return Task.CompletedTask;
        }

        public Task RemoveParticipantAsync(
            string roomId, string agentId,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task RemoveRoomAsync(
            string roomId, CancellationToken cancellationToken = default)
        {
            RemovedRooms.Add(roomId);
            return Task.CompletedTask;
        }
    }

    private sealed class StubTerminalQueryPort : IStreamingProxyChatSessionTerminalQueryPort
    {
        private readonly StreamingProxyChatSessionTerminalSnapshot? _snapshot;

        public StubTerminalQueryPort(StreamingProxyChatSessionTerminalStatus? status = null)
        {
            if (!status.HasValue)
                return;

            _snapshot = new StreamingProxyChatSessionTerminalSnapshot
            {
                RootActorId = "room-a",
                SessionId = "session-123",
                Status = status.Value,
            };
        }

        public int QueryCount { get; private set; }

        public Task<StreamingProxyChatSessionTerminalSnapshot?> GetAsync(
            string rootActorId,
            string sessionId,
            CancellationToken ct = default)
        {
            _ = rootActorId;
            _ = sessionId;
            _ = ct;
            QueryCount++;
            return Task.FromResult(_snapshot);
        }
    }

    private static StreamingProxyNyxParticipantCoordinator CreateNyxParticipantCoordinator()
    {
        var stubProvider = new StubLlmProvider();
        var llmFactory = new StubLlmProviderFactory(stubProvider);
        var configuration = new ConfigurationBuilder().Build();
        var httpClientFactory = new StubHttpClientFactory();

        return new StreamingProxyNyxParticipantCoordinator(
            llmFactory,
            configuration,
            httpClientFactory,
            NullLogger<StreamingProxyNyxParticipantCoordinator>.Instance);
    }

    private sealed class StubLlmProvider : ILLMProvider
    {
        public string Name => "stub";
        public Task<LLMResponse> ChatAsync(LLMRequest request, CancellationToken ct = default)
            => Task.FromResult(new LLMResponse { Content = string.Empty });
        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(LLMRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class StubLlmProviderFactory(ILLMProvider provider) : ILLMProviderFactory
    {
        public ILLMProvider GetProvider(string name) => provider;
        public ILLMProvider GetDefault() => provider;
        public IReadOnlyList<string> GetAvailableProviders() => [];
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "StreamingProxyCoverageTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
