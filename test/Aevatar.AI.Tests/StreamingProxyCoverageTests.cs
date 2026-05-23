using System.IO;
using System.Reflection;
using System.Security.Claims;
using System.Threading.Channels;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
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
        var roomCommandDescriptor = services.FirstOrDefault(d =>
            d.ServiceType == typeof(IStreamingProxyRoomCommandService));

        coordinatorDescriptor.Should().NotBeNull();
        coordinatorDescriptor!.Lifetime.Should().Be(ServiceLifetime.Singleton);
        projectionDescriptor.Should().NotBeNull();
        projectionDescriptor!.Lifetime.Should().Be(ServiceLifetime.Singleton);
        terminalQueryDescriptor.Should().NotBeNull();
        terminalQueryDescriptor!.Lifetime.Should().Be(ServiceLifetime.Singleton);
        roomCommandDescriptor.Should().NotBeNull();
        roomCommandDescriptor!.Lifetime.Should().Be(ServiceLifetime.Singleton);
    }

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
    public void StreamingProxyEndpointSource_ShouldNotInlineDispatchActorEvents()
    {
        var root = GetRepositoryRoot();
        var endpoints = File.ReadAllText(Path.Combine(
            root,
            "agents/Aevatar.GAgents.StreamingProxy/StreamingProxyEndpoints.cs"));

        endpoints.Should().NotContain("actor.HandleEventAsync");
        endpoints.Should().NotContain("EnsureAndAttachLeaseAsync");
        endpoints.Should().NotContain("EnsureChatProjectionAsync");
        endpoints.Should().NotContain("EnsureSubscriptionProjectionAsync");
    }

    [Fact]
    public void StreamingProxyEndpointSource_ShouldDelegateRoomCommandsToRoomCommandService()
    {
        var root = GetRepositoryRoot();
        var endpoints = File.ReadAllText(Path.Combine(
            root,
            "agents/Aevatar.GAgents.StreamingProxy/StreamingProxyEndpoints.cs"));

        endpoints.Should().NotContain("IActorDispatchPort");
        endpoints.Should().NotContain("DispatchRoomEnvelopeAsync");
        endpoints.Should().NotContain("Any.Pack");
        endpoints.Should().Contain("IStreamingProxyRoomCommandService");
        endpoints.Should().Contain("StreamingProxyRoomChatCommand");
        endpoints.Should().Contain("StreamingProxyRoomPostMessageCommand");
        endpoints.Should().Contain("StreamingProxyRoomJoinCommand");
        endpoints.Should().NotContain("StreamingProxyRoomTerminalStateCommand");
    }

    [Fact]
    public void StreamingProxyRoomSources_ShouldNotIntroduceParallelRoomInteractionPort()
    {
        var root = GetRepositoryRoot();
        var roomSources = Directory
            .EnumerateFiles(
                Path.Combine(root, "agents/Aevatar.GAgents.StreamingProxy"),
                "*.cs",
                SearchOption.AllDirectories)
            .Where(path => path.Contains($"{Path.DirectorySeparatorChar}Application{Path.DirectorySeparatorChar}Rooms{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                           Path.GetFileName(path).Equals("StreamingProxyEndpoints.cs", StringComparison.Ordinal))
            .Select(File.ReadAllText);

        roomSources.Should().OnlyContain(source =>
            !source.Contains("IStreamingProxyRoomInteractionPort", StringComparison.Ordinal));
        roomSources.Should().OnlyContain(source =>
            !source.Contains("RoomInteractionPort", StringComparison.Ordinal));
    }

    [Fact]
    public void StreamingProxyRoomAndCoordinatorSource_ShouldNotInlineDispatchActorEvents()
    {
        var root = GetRepositoryRoot();
        var roomCommandService = File.ReadAllText(Path.Combine(
            root,
            "agents/Aevatar.GAgents.StreamingProxy/Application/Rooms/StreamingProxyRoomCommandService.cs"));
        var nyxCoordinator = File.ReadAllText(Path.Combine(
            root,
            "agents/Aevatar.GAgents.StreamingProxy/StreamingProxyNyxParticipantCoordinator.cs"));

        roomCommandService.Should().NotContain("actor.HandleEventAsync(");
        roomCommandService.Should().NotContain(".HandleEventAsync(");
        nyxCoordinator.Should().NotContain("actor.HandleEventAsync(");
        nyxCoordinator.Should().NotContain(".HandleEventAsync(");
    }

    [Fact]
    public async Task HandleCreateRoomAsync_ShouldCreateRoomAndInitActor()
    {
        var roomCommandService = new StubRoomCommandService(
            new StreamingProxyRoomCreateResult(
                StreamingProxyRoomCreateStatus.Created,
                "room-project-x",
                "Project X"));
        var request = new CreateRoomRequest("Project X");

        var result = await InvokeResultAsync(
            "HandleCreateRoomAsync",
            CreateScopedHttpContext(),
            "scope-a",
            request,
            roomCommandService,
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status200OK);
        response.Body.Should().Contain("roomName");
        response.Body.Should().Contain("room-project-x");
        roomCommandService.Commands.Should().ContainSingle();
        roomCommandService.Commands[0].Should().Be(new StreamingProxyRoomCreateCommand("scope-a", "Project X"));
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
    public async Task HandleDeleteRoomAsync_ShouldReturnOk_AndOnlyUnregisterRoom()
    {
        var actorStore = new StubGAgentActorStore();

        var result = await InvokeResultAsync(
            "HandleDeleteRoomAsync",
            CreateScopedHttpContext(),
            "scope-a",
            "room-1",
            actorStore,
            actorStore,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status200OK);
        actorStore.RemovedActors.Should().ContainSingle(x =>
            x.scopeId == "scope-a" &&
            x.gagentType == StreamingProxyDefaults.GAgentTypeName && x.actorId == "room-1");
    }

    [Fact]
    public async Task HandleDeleteRoomAsync_UnregisterFailure_ShouldReturnUnavailable()
    {
        var actorStore = new StubGAgentActorStore
        {
            UnregisterException = new InvalidOperationException("registry unavailable"),
        };

        var result = await InvokeResultAsync(
            "HandleDeleteRoomAsync",
            CreateScopedHttpContext(),
            "scope-a",
            "room-1",
            actorStore,
            actorStore,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    [Fact]
    public async Task HandleChatAsync_ShouldRejectEmptyPrompt()
    {
        var context = CreateScopedHttpContext();
        var interactionService = new StubStreamingProxyRoomChatInteractionService();
        var actorStore = new StubGAgentActorStore();

        var method = typeof(StreamingProxyEndpoints).GetMethod(
            "HandleChatAsync",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var task = method.Invoke(null, [context, "scope-a", "room-a", new ChatTopicRequest(null), actorStore, interactionService, NullLoggerFactory.Instance, CancellationToken.None]);
        await InvokeTaskAsync(task);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        interactionService.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleChatAsync_ShouldRejectMismatchedAuthenticatedScope()
    {
        var context = CreateScopedHttpContext("scope-b");
        context.Response.Body = new MemoryStream();
        var interactionService = new StubStreamingProxyRoomChatInteractionService();
        var actorStore = new StubGAgentActorStore();

        var method = typeof(StreamingProxyEndpoints).GetMethod(
            "HandleChatAsync",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var task = method.Invoke(
            null,
            [context, "scope-a", "room-a", new ChatTopicRequest("hello"), actorStore, interactionService, NullLoggerFactory.Instance, CancellationToken.None]);
        await InvokeTaskAsync(task);

        context.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        body.Should().Contain("SCOPE_ACCESS_DENIED");
        body.Should().Contain("Authenticated scope does not match requested scope.");
    }

    [Fact]
    public async Task HandleMessageStreamAsync_ShouldAttachProjectionSession_AndWriteRoomEvents()
    {
        var context = CreateScopedHttpContext();
        context.Response.Body = new MemoryStream();
        var runtime = new StubActorRuntime(new List<IActor> { new StubActor("room-a") });
        var actorStore = new StubGAgentActorStore();
        var observationPort = new StubRoomSubscriptionObservationPort();
        using var cts = new CancellationTokenSource();

        var method = typeof(StreamingProxyEndpoints).GetMethod(
            "HandleMessageStreamAsync",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var task = InvokeTaskAsync(method.Invoke(
            null,
            [context, "scope-a", "room-a", actorStore, observationPort, NullLoggerFactory.Instance, cts.Token]));

        await observationPort.Attached.Task;
        await observationPort.PublishAsync(
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

        observationPort.AttachCalls.Should().ContainSingle(x => x.RoomId == "room-a");
        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        body.Should().Contain("AGENT_MESSAGE");
        body.Should().Contain("hello from projection");
    }

    [Fact]
    public async Task StreamingProxyRoomSubscriptionObservationPort_ShouldAttachNormalizedRoomSessionAndDispose()
    {
        var projectionPort = new StubRoomSessionProjectionPort();
        var observationPort = new StreamingProxyRoomSubscriptionObservationPort(projectionPort);
        await using var sink = new EventChannel<StreamingProxyRoomSessionEnvelope>();

        var attachment = await observationPort.AttachAsync(" room-a ", sink, CancellationToken.None);
        await observationPort.DetachAndDisposeAsync(attachment, sink, CancellationToken.None);

        attachment.ProjectionLease.ActorId.Should().Be("room-a");
        attachment.ProjectionLease.SessionId.Should().Be("room:room-a:subscription");
        projectionPort.EnsureCalls.Should().BeEmpty();
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
            new RecordingRoomSessionActivationService(),
            new RecordingRoomSessionReleaseService(),
            hub,
            runtime);
        await using var sink = new EventChannel<StreamingProxyRoomSessionEnvelope>();

        var attachment = await port.AttachExistingChatProjectionAsync("room-a", "session-123", sink, CancellationToken.None);

        attachment.Should().NotBeNull();
        attachment!.ProjectionLease.ActorId.Should().Be("room-a");
        attachment.ProjectionLease.SessionId.Should().Be("session-123");
        hub.SubscribeCalls.Should().Be(1);
        hub.LastScopeId.Should().Be("room-a");
        hub.LastSessionId.Should().Be("session-123");
    }

    [Fact]
    public async Task StreamingProxyRoomSessionProjectionPort_ShouldReturnNull_WhenProjectionSessionIsCold()
    {
        var hub = new RecordingRoomSessionEventHub();
        var port = new StreamingProxyRoomSessionProjectionPort(
            new RecordingRoomSessionActivationService(),
            new RecordingRoomSessionReleaseService(),
            hub,
            new StubActorRuntime());
        await using var sink = new EventChannel<StreamingProxyRoomSessionEnvelope>();

        var attachment = await port.AttachExistingChatProjectionAsync("room-a", "session-123", sink, CancellationToken.None);

        attachment.Should().BeNull();
        hub.SubscribeCalls.Should().Be(0);
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
        var interactionService = new StubStreamingProxyRoomChatInteractionService();
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

        var method = typeof(StreamingProxyEndpoints).GetMethod(
            "HandleChatAsync",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        await InvokeTaskAsync(method.Invoke(
            null,
            [context, "scope-a", "room-a", request, actorStore, interactionService, NullLoggerFactory.Instance, CancellationToken.None]));

        interactionService.Commands.Should().ContainSingle().Which.Should().Be(new StreamingProxyRoomChatCommand(
            "room-a",
            "scope-a",
            "Discuss webhook relay",
            "session-123",
            null,
            null,
            null));

        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        body.Should().Contain("TOPIC_STARTED");
        body.Should().Contain("AGENT_MESSAGE");
        body.Should().Contain("RUN_FINISHED");
    }

    [Fact]
    public async Task HandleChatAsync_ShouldNotPublishTerminalState_WhenCancelled()
    {
        var context = CreateScopedHttpContext();
        context.Response.Body = new MemoryStream();
        var interactionService = new StubStreamingProxyRoomChatInteractionService
        {
            WaitForCancellation = true,
        };
        var actorStore = new StubGAgentActorStore();
        using var cts = new CancellationTokenSource();

        var method = typeof(StreamingProxyEndpoints).GetMethod(
            "HandleChatAsync",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var task = InvokeTaskAsync(method.Invoke(
            null,
            [context, "scope-a", "room-a", new ChatTopicRequest("Cancel me", "session-cancel"), actorStore, interactionService, NullLoggerFactory.Instance, cts.Token]));

        await interactionService.Started.Task;
        cts.Cancel();
        await task;

        interactionService.Commands.Should().ContainSingle(command =>
            command.SessionId == "session-cancel");
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
            new StreamingProxyRoomChatCommand(actor.Id, "scope-a", "Discuss claims", "session-123", "token-1", "route-1", "model-1"),
            (frame, _) =>
            {
                emitted.Add(frame);
                return ValueTask.CompletedTask;
            });

        result.Succeeded.Should().BeTrue();
        result.Receipt.Should().Be(new StreamingProxyRoomChatAcceptedReceipt(actor.Id, "session-123", "session-123", "session-123"));
        result.FinalizeResult.Should().NotBeNull();
        result.FinalizeResult!.Completed.Should().BeTrue();
        result.FinalizeResult.Completion.Should().Be(StreamingProxyProjectionCompletionStatus.Completed);
        projectionPort.EnsureCalls.Should().BeEmpty();
        projectionPort.AttachExistingCalls.Should().ContainSingle(x =>
            x.actorId == actor.Id &&
            x.sessionId == "session-123");
        projectionPort.AttachCount.Should().Be(1);
        projectionPort.DetachCount.Should().Be(1);
        projectionPort.ReleaseCount.Should().Be(1);
        dispatchPort.Dispatches.Should().ContainSingle();
        var request = dispatchPort.Dispatches.Single().Envelope.Payload.Unpack<StreamingProxyRoomChatRequested>();
        request.Prompt.Should().Be("Discuss claims");
        request.SessionId.Should().Be("session-123");
        request.ScopeId.Should().Be("scope-a");
        request.AccessToken.Should().Be("token-1");
        request.PreferredRoute.Should().Be("route-1");
        request.DefaultModel.Should().Be("model-1");
        emitted.Should().HaveCount(2);
        emitted.Last().Envelope.Payload.Unpack<StreamingProxyChatSessionTerminalStateChanged>().Status
            .Should().Be(StreamingProxyChatSessionTerminalStatus.Completed);
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
            new StreamingProxyRoomChatCommand(actor.Id, "scope-a", "prompt", "session-123", null, null, null),
            (_, _) => ValueTask.CompletedTask);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(StreamingProxyRoomChatStartError.ProjectionUnavailable);
        projectionPort.EnsureCalls.Should().BeEmpty();
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
            new StreamingProxyRoomChatCommand(actor.Id, "scope-a", "prompt", "session-123", null, null, null),
            (_, _) => ValueTask.CompletedTask);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("dispatch failed");
        projectionPort.EnsureCalls.Should().BeEmpty();
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
            new StreamingProxyRoomChatCommand("room-a", "scope-a", "topic", "session-123", "token-1", "route-1", "model-1"),
            new CommandContext("room-a", "command-1", "correlation-1", new Dictionary<string, string>()));

        envelope.Route?.Direct?.TargetActorId.Should().Be("room-a");
        envelope.Propagation?.CorrelationId.Should().Be("correlation-1");
        var request = envelope.Payload.Unpack<StreamingProxyRoomChatRequested>();
        request.Prompt.Should().Be("topic");
        request.ScopeId.Should().Be("scope-a");
        request.SessionId.Should().Be("session-123");
        request.AccessToken.Should().Be("token-1");
        request.PreferredRoute.Should().Be("route-1");
        request.DefaultModel.Should().Be("model-1");
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
    public async Task GAgent_ShouldOwnRoomChatProgression_ForTypedChatCommand()
    {
        var provider = new StreamingReplyLlmProvider("Room-owned reply.");
        await using var services = new ServiceCollection()
            .AddSingleton<IEventStore, InMemoryEventStoreForTests>()
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
            .AddSingleton<IConfiguration>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Cli:App:NyxId:Authority"] = "https://nyx.example.com",
                })
                .Build())
            .AddSingleton<IHttpClientFactory>(new StubHttpClientFactory(new StaticHttpMessageHandler("""
                {
                  "services": [
                    {
                      "user_service_id": "svc-node-a",
                      "service_slug": "openclaw",
                      "display_name": "OpenClaw",
                      "status": "ready",
                      "route_value": "/api/v1/proxy/s/openclaw/node-a",
                      "node_id": "node-a",
                      "allowed": true,
                      "models": ["model-a"]
                    }
                  ]
                }
                """)))
            .AddSingleton<ILLMProviderFactory>(new StubLlmProviderFactory(provider, includeNyxId: true))
            .AddLogging()
            .AddStreamingProxy()
            .BuildServiceProvider();
        var agent = CreateAgent(services, "streaming-proxy-agent");
        var publisher = new TestRecordingEventPublisher();
        agent.EventPublisher = publisher;

        await agent.ActivateAsync();
        await agent.HandleRoomChatRequested(new StreamingProxyRoomChatRequested
        {
            ScopeId = "scope-a",
            Prompt = "Discuss actor-owned orchestration",
            SessionId = "session-123",
            AccessToken = "token-1",
            PreferredRoute = "/api/v1/proxy/s/openclaw/node-a",
            DefaultModel = "model-a",
        });

        agent.State.Messages.Should().HaveCount(2);
        agent.State.Messages[0].IsTopic.Should().BeTrue();
        agent.State.Messages[0].Content.Should().Be("Discuss actor-owned orchestration");
        agent.State.Messages[1].SenderAgentId.Should().Contain("node-a");
        agent.State.Messages[1].Content.Should().Be("Room-owned reply.");
        agent.State.Participants.Should().ContainSingle(participant =>
            participant.DisplayName == "OpenClaw" &&
            participant.AgentId.Contains("node-a", StringComparison.Ordinal));
        agent.State.TerminalSessions.Should().ContainKey("session-123");
        agent.State.TerminalSessions["session-123"].Status.Should()
            .Be(StreamingProxyChatSessionTerminalStatus.Completed);

        publisher.Published.OfType<GroupChatTopicEvent>().Should().ContainSingle();
        publisher.Published.OfType<GroupChatParticipantJoinedEvent>().Should().ContainSingle();
        publisher.Published.OfType<GroupChatMessageEvent>().Should().ContainSingle(message =>
            message.SessionId == "session-123" &&
            message.Content == "Room-owned reply.");
        provider.Requests.Should().ContainSingle(request =>
            request.Metadata![LLMRequestMetadataKeys.NyxIdRoutePreference] == "/api/v1/proxy/s/openclaw/node-a");
    }

    [Fact]
    public async Task TerminalProjector_ShouldMaterializeCommittedTerminalSnapshot()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddStreamingProxy();
        await using var provider = services.BuildServiceProvider();

        var projector = provider.GetRequiredService<StreamingProxyChatSessionTerminalProjector>();
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

        var projector = provider.GetRequiredService<StreamingProxyChatSessionTerminalProjector>();
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
    public async Task HandlePostMessageAsync_ShouldRejectMissingFieldsAndReturnAccepted()
    {
        var roomCommandService = new StubRoomCommandService
        {
            PostMessageResult = new StreamingProxyRoomPostMessageResult(StreamingProxyRoomPostMessageStatus.RoomNotFound),
        };
        var result = await InvokeResultAsync(
            "HandlePostMessageAsync",
            CreateScopedHttpContext(),
            "scope-a",
            "room-a",
            new PostMessageRequest(null, "name", "content"),
            roomCommandService,
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
            roomCommandService,
            new StubGAgentActorStore(),
            CancellationToken.None);

        response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status404NotFound);

        roomCommandService = new StubRoomCommandService();
        result = await InvokeResultAsync(
            "HandlePostMessageAsync",
            CreateScopedHttpContext(),
            "scope-a",
            "room-a",
            new PostMessageRequest("agent", null, "content"),
            roomCommandService,
            new StubGAgentActorStore(),
            CancellationToken.None);

        response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status200OK);
        roomCommandService.PostMessageCommands.Should().ContainSingle(x => x.RoomId == "room-a");
    }

    [Fact]
    public async Task HandleJoinAsync_ShouldRejectMissingAgentIdAndSubmitRoomCommandOnly()
    {
        var roomCommandService = new StubRoomCommandService();
        var actorStore = new StubGAgentActorStore();

        var result = await InvokeResultAsync(
            "HandleJoinAsync",
            CreateScopedHttpContext(),
            "scope-a",
            "room-a",
            new JoinRoomRequest(null, null),
            roomCommandService,
            actorStore,
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);

        roomCommandService = new StubRoomCommandService
        {
            JoinResult = new StreamingProxyRoomJoinResult(
                StreamingProxyRoomJoinStatus.RoomNotFound,
                null,
                null),
        };
        result = await InvokeResultAsync(
            "HandleJoinAsync",
            CreateScopedHttpContext(),
            "scope-a",
            "missing-room",
            new JoinRoomRequest("agent-1", "Alice"),
            roomCommandService,
            actorStore,
            CancellationToken.None);

        response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status404NotFound);

        roomCommandService = new StubRoomCommandService();
        var joinRequest = new JoinRoomRequest("agent-1", "Alice");
        result = await InvokeResultAsync(
            "HandleJoinAsync",
            CreateScopedHttpContext(),
            "scope-a",
            "room-a",
            joinRequest,
            roomCommandService,
            actorStore,
            CancellationToken.None);

        response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status200OK);
        roomCommandService.JoinCommands.Should().ContainSingle(x => x.RoomId == "room-a");
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
    public async Task HandleListParticipantsAsync_ShouldReturnReadModelParticipants()
    {
        var queryPort = new StubRoomParticipantsQueryPort(new StreamingProxyRoomParticipantsSnapshot
        {
            Id = "room-a",
            ActorId = "room-a",
            RootActorId = "room-a",
            StateVersion = 7,
            UpdatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Participants =
            {
                new Aevatar.GAgents.StreamingProxy.StreamingProxyParticipant
                {
                    AgentId = "agent-1",
                    DisplayName = "Alice",
                    JoinedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                },
            },
        });

        var result = await InvokeResultAsync(
            "HandleListParticipantsAsync",
            CreateScopedHttpContext(),
            "scope-a",
            "room-a",
            new StubGAgentActorStore(),
            queryPort,
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
        state.TerminalSessions.Should().ContainKey("room-session");
        state.TerminalSessions["room-session"].Status.Should().Be(StreamingProxyChatSessionTerminalStatus.Completed);

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

    private static string GetRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "aevatar.slnx")))
            directory = directory.Parent;

        return directory?.FullName
            ?? throw new InvalidOperationException("Repository root could not be resolved.");
    }

    private static async Task<StreamingProxyStreamSignal?> WriteRoomSessionEventAsync(
        StreamingProxyRoomSessionEnvelope envelope,
        object writer)
    {
        var method = typeof(StreamingProxyEndpoints).GetMethod(
            "MapAndWriteRoomSessionEventAsync",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = method.Invoke(null, [envelope, writer])!;
        return result switch
        {
            ValueTask<StreamingProxyStreamSignal?> valueTask => await valueTask,
            Task<StreamingProxyStreamSignal?> task => await task,
            _ => throw new InvalidOperationException($"Unexpected return type: {result.GetType()}"),
        };
    }

    private static async Task<IResult> InvokeResultAsync(string methodName, params object[] args)
    {
        var method = typeof(StreamingProxyEndpoints).GetMethod(
            methodName,
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = method.Invoke(null, NormalizeEndpointArgs(method, args))
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

    private static object[] NormalizeEndpointArgs(MethodInfo method, object[] args)
    {
        var parameters = method.GetParameters();
        if (parameters.Length == args.Length)
            return args;

        if (parameters.Length == args.Length + 1 && args.All(arg => arg is not IActorDispatchPort))
        {
            var dispatchPortIndex = Array.FindIndex(
                parameters,
                parameter => parameter.ParameterType == typeof(IActorDispatchPort));
            if (dispatchPortIndex >= 0)
            {
                var actorRuntime = args.OfType<IActorRuntime>().FirstOrDefault()
                    ?? throw new InvalidOperationException("Endpoint test invocation needs IActorRuntime before IActorDispatchPort can be inferred.");
                var normalized = args.ToList();
                normalized.Insert(dispatchPortIndex, new StubActorDispatchPort(actorRuntime));
                return normalized.ToArray();
            }
        }

        return args;
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

    private sealed class StubActorDispatchPort(IActorRuntime runtime) : IActorDispatchPort
    {
        public List<(string ActorId, EventEnvelope Envelope)> Dispatches { get; } = [];

        public async Task DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            Dispatches.Add((actorId, envelope));
            var actor = await runtime.GetAsync(actorId);
            if (actor is not null)
                await actor.HandleEventAsync(envelope, ct);
        }
    }

    private sealed class ThrowingActorDispatchPort(Exception exception) : IActorDispatchPort
    {
        public Task DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            _ = actorId;
            _ = envelope;
            _ = ct;
            return Task.FromException(exception);
        }
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

    private sealed class StubStreamingProxyRoomChatInteractionService
        : ICommandInteractionService<StreamingProxyRoomChatCommand, StreamingProxyRoomChatAcceptedReceipt, StreamingProxyRoomChatStartError, StreamingProxyRoomSessionEnvelope, StreamingProxyProjectionCompletionStatus>
    {
        public List<StreamingProxyRoomChatCommand> Commands { get; } = [];
        public List<StreamingProxyRoomSessionEnvelope> Frames { get; } = [];
        public bool WaitForCancellation { get; init; }
        public StreamingProxyRoomChatStartError? Failure { get; init; }
        public TaskCompletionSource<bool> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<CommandInteractionResult<StreamingProxyRoomChatAcceptedReceipt, StreamingProxyRoomChatStartError, StreamingProxyProjectionCompletionStatus>> ExecuteAsync(
            StreamingProxyRoomChatCommand command,
            Func<StreamingProxyRoomSessionEnvelope, CancellationToken, ValueTask> emitAsync,
            Func<StreamingProxyRoomChatAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync = null,
            CancellationToken ct = default)
        {
            Commands.Add(command);
            Started.TrySetResult(true);

            if (Failure.HasValue)
            {
                return CommandInteractionResult<StreamingProxyRoomChatAcceptedReceipt, StreamingProxyRoomChatStartError, StreamingProxyProjectionCompletionStatus>
                    .Failure(Failure.Value);
            }

            var receipt = new StreamingProxyRoomChatAcceptedReceipt(
                command.RoomId,
                "command-id",
                "correlation-id",
                command.SessionId);
            if (onAcceptedAsync != null)
                await onAcceptedAsync(receipt, ct);

            if (WaitForCancellation)
                await WaitUntilCanceledAsync(ct);

            foreach (var frame in Frames)
                await emitAsync(frame, ct);

            return CommandInteractionResult<StreamingProxyRoomChatAcceptedReceipt, StreamingProxyRoomChatStartError, StreamingProxyProjectionCompletionStatus>
                .Success(
                    receipt,
                    new CommandInteractionFinalizeResult<StreamingProxyProjectionCompletionStatus>(
                        StreamingProxyProjectionCompletionStatus.Completed,
                        true));
        }

        private static async Task WaitUntilCanceledAsync(CancellationToken ct)
        {
            var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var registration = ct.Register(static state =>
                ((TaskCompletionSource<bool>)state!).TrySetCanceled(), canceled);
            await canceled.Task;
        }
    }

    private sealed class StubRoomSubscriptionObservationPort : IStreamingProxyRoomSubscriptionObservationPort
    {
        private IEventSink<StreamingProxyRoomSessionEnvelope>? _sink;

        public List<(string RoomId, IEventSink<StreamingProxyRoomSessionEnvelope> Sink)> AttachCalls { get; } = [];
        public TaskCompletionSource<bool> Attached { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<StreamingProxyRoomSubscriptionObservationAttachment> AttachAsync(
            string roomId,
            IEventSink<StreamingProxyRoomSessionEnvelope> sink,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _sink = sink;
            AttachCalls.Add((roomId, sink));
            Attached.TrySetResult(true);
            return Task.FromResult(new StreamingProxyRoomSubscriptionObservationAttachment(
                new StubRoomSessionProjectionLease(
                    roomId,
                    $"room:{roomId}:subscription"),
                null));
        }

        public Task DetachAndDisposeAsync(
            StreamingProxyRoomSubscriptionObservationAttachment attachment,
            IEventSink<StreamingProxyRoomSessionEnvelope> sink,
            CancellationToken ct = default)
        {
            _ = attachment;
            _ = ct;
            sink.Complete();
            return sink.DisposeAsync().AsTask();
        }

        public async Task PublishAsync(EventEnvelope envelope, CancellationToken ct = default)
        {
            if (_sink == null)
                throw new InvalidOperationException("Subscription sink is not attached.");

            await _sink.PushAsync(
                new StreamingProxyRoomSessionEnvelope
                {
                    Envelope = envelope,
                },
                ct);
        }
    }

    private sealed class StubRoomSessionProjectionPort : IStreamingProxyRoomSessionProjectionPort
    {
        private IEventSink<StreamingProxyRoomSessionEnvelope>? _sink;
        private IStreamingProxyRoomSessionProjectionLease? _lease;

        public bool ProjectionEnabled => true;
        public bool ReturnNullLease { get; init; }

        public List<(string actorId, string sessionId, string projectionKind)> EnsureCalls { get; } = [];
        public List<(string actorId, string sessionId)> AttachExistingCalls { get; } = [];
        public List<StreamingProxyRoomSessionEnvelope> Messages { get; } = [];
        public List<IStreamingProxyRoomSessionProjectionLease> AttachedLeases { get; } = [];
        public int AttachCount { get; private set; }
        public int DetachCount { get; private set; }
        public int ReleaseCount { get; private set; }

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
            if (ReturnNullLease)
                return Task.FromResult<IStreamingProxyRoomSessionProjectionLease?>(null);

            _lease = new StubRoomSessionProjectionLease(actorId, sessionId);
            return Task.FromResult<IStreamingProxyRoomSessionProjectionLease?>(_lease);
        }

        public async Task<EventSinkProjectionAttachment<IStreamingProxyRoomSessionProjectionLease>?> AttachExistingChatProjectionAsync(
            string actorId,
            string sessionId,
            IEventSink<StreamingProxyRoomSessionEnvelope> sink,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            AttachExistingCalls.Add((actorId, sessionId));
            if (ReturnNullLease)
                return null;

            _lease = new StubRoomSessionProjectionLease(actorId, sessionId);
            var liveSinkLease = await AttachLiveSinkAsync(_lease, sink, ct);
            return new EventSinkProjectionAttachment<IStreamingProxyRoomSessionProjectionLease>(_lease, liveSinkLease);
        }

        public Task<IAsyncDisposable?> AttachLiveSinkAsync(
            IStreamingProxyRoomSessionProjectionLease lease,
            IEventSink<StreamingProxyRoomSessionEnvelope> sink,
            CancellationToken ct = default)
        {
            _ = ct;
            AttachCount++;
            _lease = lease;
            _sink = sink;
            AttachedLeases.Add(lease);
            Attached.TrySetResult(true);
            foreach (var message in Messages)
                sink.Push(message);
            return Task.FromResult<IAsyncDisposable?>(null);
        }

        public Task DetachLiveSinkAsync(
            IAsyncDisposable? liveSinkLease,
            CancellationToken ct = default)
        {
            _ = liveSinkLease;
            _ = ct;
            DetachCount++;
            return Task.CompletedTask;
        }

        public Task ReleaseActorProjectionAsync(
            IStreamingProxyRoomSessionProjectionLease lease,
            CancellationToken ct = default)
        {
            _ = lease;
            _ = ct;
            ReleaseCount++;
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
        public int SubscribeCalls { get; private set; }
        public string? LastScopeId { get; private set; }
        public string? LastSessionId { get; private set; }

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
            SubscribeCalls++;
            LastScopeId = scopeId;
            LastSessionId = sessionId;
            _ = handler;
            _ = ct;
            return Task.FromResult<IAsyncDisposable>(new NoopAsyncDisposable());
        }
    }

    private sealed class RecordingRoomSessionActivationService
        : IProjectionScopeActivationService<StreamingProxyRoomSessionRuntimeLease>
    {
        public List<ProjectionScopeStartRequest> Requests { get; } = [];

        public Task<StreamingProxyRoomSessionRuntimeLease> EnsureAsync(
            ProjectionScopeStartRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(new StreamingProxyRoomSessionRuntimeLease(new StreamingProxyRoomSessionProjectionContext
            {
                RootActorId = request.RootActorId,
                ProjectionKind = request.ProjectionKind,
                SessionId = request.SessionId,
            }));
        }
    }

    private sealed class RecordingRoomSessionReleaseService
        : IProjectionScopeReleaseService<StreamingProxyRoomSessionRuntimeLease>
    {
        public List<StreamingProxyRoomSessionRuntimeLease> Leases { get; } = [];

        public Task ReleaseIfIdleAsync(StreamingProxyRoomSessionRuntimeLease lease, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Leases.Add(lease);
            return Task.CompletedTask;
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
        public Exception? UnregisterException { get; init; }

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
            if (UnregisterException is not null)
                throw UnregisterException;

            RemovedActors.Add((registration.ScopeId, registration.GAgentType, registration.ActorId));
            return Task.FromResult(new GAgentActorRegistryCommandReceipt(
                registration,
                GAgentActorRegistryCommandStage.AdmissionRemoved));
        }

        public Task<ScopeResourceAdmissionResult> AuthorizeTargetAsync(
            ScopeResourceTarget target,
            CancellationToken cancellationToken = default)
            => Task.FromResult(ScopeResourceAdmissionResult.Allowed());
    }

    private sealed class StubRoomCommandService(
        StreamingProxyRoomCreateResult? result = null)
        : IStreamingProxyRoomCommandService
    {
        public List<StreamingProxyRoomCreateCommand> Commands { get; } = [];
        public List<StreamingProxyRoomPostMessageCommand> PostMessageCommands { get; } = [];
        public List<StreamingProxyRoomJoinCommand> JoinCommands { get; } = [];
        public List<StreamingProxyRoomTerminalStateCommand> TerminalCommands { get; } = [];
        public StreamingProxyRoomPostMessageResult PostMessageResult { get; init; } =
            new(StreamingProxyRoomPostMessageStatus.Accepted);
        public StreamingProxyRoomJoinResult? JoinResult { get; init; }

        public Task<StreamingProxyRoomCreateResult> CreateRoomAsync(
            StreamingProxyRoomCreateCommand command,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(command);
            return Task.FromResult(result ?? new StreamingProxyRoomCreateResult(
                StreamingProxyRoomCreateStatus.Created,
                "room-a",
                "Room A"));
        }

        public Task<StreamingProxyRoomPostMessageResult> PostMessageAsync(
            StreamingProxyRoomPostMessageCommand command,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PostMessageCommands.Add(command);
            return Task.FromResult(PostMessageResult);
        }

        public Task<StreamingProxyRoomJoinResult> JoinAsync(
            StreamingProxyRoomJoinCommand command,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            JoinCommands.Add(command);
            return Task.FromResult(JoinResult ?? new StreamingProxyRoomJoinResult(
                StreamingProxyRoomJoinStatus.Joined,
                command.AgentId?.Trim(),
                command.DisplayName?.Trim()));
        }

        public Task PublishTerminalStateAsync(
            StreamingProxyRoomTerminalStateCommand command,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TerminalCommands.Add(command);
            return Task.CompletedTask;
        }
    }

    private sealed class StubRoomParticipantsQueryPort(StreamingProxyRoomParticipantsSnapshot? snapshot)
        : IStreamingProxyRoomParticipantsQueryPort
    {
        public Task<StreamingProxyRoomParticipantsSnapshot?> GetAsync(
            string rootActorId,
            CancellationToken ct = default) =>
            Task.FromResult(snapshot);
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

    private sealed class StubLlmProvider : ILLMProvider
    {
        public string Name => "stub";
        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(LLMRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class StreamingReplyLlmProvider(string content) : ILLMProvider
    {
        public string Name => "nyxid";
        public List<LLMRequest> Requests { get; } = [];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            ct.ThrowIfCancellationRequested();
            Requests.Add(request);
            yield return new LLMStreamChunk { DeltaContent = content };
            yield return new LLMStreamChunk { IsLast = true, FinishReason = "stop" };
        }
    }

    private sealed class StubLlmProviderFactory(ILLMProvider provider, bool includeNyxId = false) : ILLMProviderFactory
    {
        public ILLMProvider GetProvider(string name) => provider;
        public ILLMProvider GetDefault() => provider;
        public IReadOnlyList<string> GetAvailableProviders() => includeNyxId ? ["nyxid"] : [];
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler? handler = null) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
    }

    private sealed class StaticHttpMessageHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body),
            });
        }
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
