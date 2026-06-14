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

public class StreamingProxyCoverageTests
{
    [Fact]
    public void AddStreamingProxy_ShouldRegisterSingletonCoordinator()
    {
        var services = new ServiceCollection();
        services.AddStreamingProxy();

        var coordinatorDescriptor = services.FirstOrDefault(d =>
            d.ServiceType == typeof(StreamingProxyNyxParticipantCoordinator));
        var runnerDescriptor = services.FirstOrDefault(d =>
            d.ServiceType == typeof(IHostedService) &&
            d.ImplementationType == typeof(StreamingProxyChatLifecycleContinuationRunner));
        var projectionDescriptor = services.FirstOrDefault(d =>
            d.ServiceType == typeof(IStreamingProxyRoomSessionProjectionPort));
        var terminalQueryDescriptor = services.FirstOrDefault(d =>
            d.ServiceType == typeof(IStreamingProxyChatSessionTerminalQueryPort));
        var participantsQueryDescriptor = services.FirstOrDefault(d =>
            d.ServiceType == typeof(IStreamingProxyRoomParticipantsQueryPort));
        var roomCommandDescriptor = services.FirstOrDefault(d =>
            d.ServiceType == typeof(IStreamingProxyRoomCommandService));

        coordinatorDescriptor.Should().NotBeNull();
        coordinatorDescriptor!.Lifetime.Should().Be(ServiceLifetime.Singleton);
        runnerDescriptor.Should().NotBeNull();
        runnerDescriptor!.Lifetime.Should().Be(ServiceLifetime.Singleton);
        projectionDescriptor.Should().NotBeNull();
        projectionDescriptor!.Lifetime.Should().Be(ServiceLifetime.Singleton);
        terminalQueryDescriptor.Should().NotBeNull();
        terminalQueryDescriptor!.Lifetime.Should().Be(ServiceLifetime.Singleton);
        participantsQueryDescriptor.Should().NotBeNull();
        participantsQueryDescriptor!.Lifetime.Should().Be(ServiceLifetime.Singleton);
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
    public void StreamingProxyEndpointSource_ShouldApplyDeprecationFilter()
    {
        var root = GetRepositoryRoot();
        var endpoints = File.ReadAllText(Path.Combine(
            root,
            "agents/Aevatar.GAgents.StreamingProxy/StreamingProxyEndpoints.cs"));

        endpoints.Should().Contain(".AddEndpointFilter(AddDeprecationHeadersAsync)");
    }

    [Fact]
    public void AddDeprecationHeaders_ShouldAdvertiseSunsetAndSuccessor()
    {
        var context = new DefaultHttpContext();

        StreamingProxyEndpoints.AddDeprecationHeaders(context.Response);

        context.Response.Headers[StreamingProxyEndpoints.DeprecationHeaderName].ToString()
            .Should().Be(StreamingProxyEndpoints.DeprecationHeaderValue);
        context.Response.Headers[StreamingProxyEndpoints.SunsetHeaderName].ToString()
            .Should().Be(StreamingProxyEndpoints.SunsetHeaderValue);
        context.Response.Headers[StreamingProxyEndpoints.LinkHeaderName].ToString()
            .Should().Be(StreamingProxyEndpoints.SuccessorLinkHeaderValue);
        StreamingProxyEndpoints.SuccessorRoute.Should().Be("/v1/responses");
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
        endpoints.Should().Contain("StreamingProxyRoomPostMessageCommand");
        endpoints.Should().Contain("ICommandInteractionService<StreamingProxyRoomChatCommand");
        endpoints.Should().NotContain("[FromServices] StreamingProxyChatLifecycleFacade");
    }

    [Fact]
    public void StreamingProxyProductionSource_ShouldDeleteSingletonParticipantAuthority()
    {
        var root = GetRepositoryRoot();
        var productionSources = Directory
            .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path =>
                !path.Contains($"{Path.DirectorySeparatorChar}test{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(File.ReadAllText);

        productionSources.Should().OnlyContain(source =>
            !source.Contains("IStreamingProxy" + "ParticipantStore", StringComparison.Ordinal) &&
            !source.Contains("ActorBackedStreamingProxy" + "ParticipantStore", StringComparison.Ordinal) &&
            !source.Contains("StreamingProxy" + "ParticipantGAgentState", StringComparison.Ordinal) &&
            !source.Contains("StreamingProxy" + "ParticipantCurrentStateDocument", StringComparison.Ordinal) &&
            !source.Contains("streaming-proxy-" + "participants", StringComparison.Ordinal));
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
        nyxCoordinator.Should().NotContain("IActorDispatchPort", "Nyx participant coordination must stay adapter-only.");
        nyxCoordinator.Should().NotContain("GroupChatParticipantJoinedEvent", "Nyx adapter must forward join commands only.");
        nyxCoordinator.Should().NotContain("GroupChatMessageEvent", "Nyx adapter must forward message commands only.");
        nyxCoordinator.Should().NotContain("GroupChatParticipantLeftEvent", "Nyx adapter must forward leave commands only.");
        nyxCoordinator.Should().NotContain("StreamingProxyChatSessionTerminalStateChanged", "Nyx adapter must not mint terminal facts.");
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
            StreamingProxyDefaults.GAgentKind,
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
    public async Task HandleDeleteRoomAsync_ShouldReturnOk_AndOnlyRemoveRoomRegistry()
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
            x.gagentType == StreamingProxyDefaults.GAgentKind && x.actorId == "room-1");
    }

    [Fact]
    public async Task HandleDeleteRoomAsync_ShouldReturnServiceUnavailable_WhenRegistryUnavailable()
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
        var roomCommandService = new StubRoomCommandService();
        var interactionService = new StubStreamingProxyRoomChatInteractionService();
        var durableCompletionResolver = new StreamingProxyChatDurableCompletionResolver(new StubTerminalQueryPort());
        var actorStore = new StubGAgentActorStore();

        await InvokeTaskAsync(
            "HandleChatAsync",
            context,
            "scope-a",
            "room-a",
            new ChatTopicRequest(null),
            roomCommandService,
            actorStore,
            interactionService,
            durableCompletionResolver,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        interactionService.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleChatAsync_ShouldRejectMismatchedAuthenticatedScope()
    {
        var context = CreateScopedHttpContext("scope-b");
        context.Response.Body = new MemoryStream();
        var roomCommandService = new StubRoomCommandService();
        var interactionService = new StubStreamingProxyRoomChatInteractionService();
        var durableCompletionResolver = new StreamingProxyChatDurableCompletionResolver(new StubTerminalQueryPort());
        var actorStore = new StubGAgentActorStore();

        await InvokeTaskAsync(
            "HandleChatAsync",
            context,
            "scope-a",
            "room-a",
            new ChatTopicRequest("hello"),
            roomCommandService,
            actorStore,
            interactionService,
            durableCompletionResolver,
            NullLoggerFactory.Instance,
            CancellationToken.None);

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
        var actorStore = new StubGAgentActorStore
        {
            AdmissionResult = ScopeResourceAdmissionResult.NotFound(),
        };
        var observationPort = new StubRoomSubscriptionObservationPort();
        await InvokeTaskAsync(
            "HandleMessageStreamAsync",
            context,
            "scope-a",
            "missing",
            actorStore,
            observationPort,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        context.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        observationPort.AttachCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleMessageStreamAsync_ShouldAttachProjectionSession_AndWriteRoomEvents()
    {
        var context = CreateScopedHttpContext();
        context.Response.Body = new MemoryStream();
        var actorStore = new StubGAgentActorStore();
        var observationPort = new StubRoomSubscriptionObservationPort();
        using var cts = new CancellationTokenSource();

        var task = InvokeTaskAsync(
            "HandleMessageStreamAsync",
            context,
            "scope-a",
            "room-a",
            actorStore,
            observationPort,
            NullLoggerFactory.Instance,
            cts.Token);

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
        response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        response.Location.Should().Be("/api/scopes/scope-a/streaming-proxy/rooms/room-a/messages:stream");
        response.Body.Should().Contain("\"status\":\"accepted\"");
        response.Body.Should().Contain("\"statusUrl\":\"/api/scopes/scope-a/streaming-proxy/rooms/room-a/messages:stream\"");
        roomCommandService.PostMessageCommands.Should().ContainSingle(x => x.RoomId == "room-a");
    }

    [Fact]
    public async Task HandleJoinAsync_ShouldRejectMissingAgentIdAndDispatchRoomJoinOnly()
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
        roomCommandService.JoinCommands.Should().ContainSingle(x => x.RoomId == "missing-room");

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
        response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        response.Location.Should().Be("/api/scopes/scope-a/streaming-proxy/rooms/room-a/participants");
        response.Body.Should().Contain("\"status\":\"accepted\"");
        response.Body.Should().Contain("\"agentId\":\"agent-1\"");
        response.Body.Should().Contain("\"statusUrl\":\"/api/scopes/scope-a/streaming-proxy/rooms/room-a/participants\"");
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
    public async Task HandleListParticipantsAsync_ShouldReturnRoomProjectionParticipants()
    {
        var joinedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
        var participantsQueryPort = new StubRoomParticipantsQueryPort(new StreamingProxyRoomParticipantsSnapshot
        {
            RootActorId = "room-a",
            StateVersion = 7,
            Participants =
            {
                new StreamingProxyRoomParticipantSnapshotEntry
                {
                    AgentId = "agent-1",
                    DisplayName = "Alice",
                    JoinedAt = joinedAt,
                },
            },
        });

        var result = await InvokeResultAsync(
            "HandleListParticipantsAsync",
            CreateScopedHttpContext(),
            "scope-a",
            "room-a",
            new StubGAgentActorStore(),
            participantsQueryPort,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status200OK);
        response.Body.Should().Contain("Alice");
        participantsQueryPort.Queries.Should().ContainSingle().Which.Should().Be("room-a");
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
                    NyxIdAccessToken = " typed-access ",
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

        // iter50 cluster-050: actor-owned idempotent join — duplicate same-id joins no longer publish
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
                    NyxIdAccessToken = " access-token ",
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
                    NyxIdAccessToken = "access-token",
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
                    NyxIdAccessToken = "access-token",
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
                    NyxIdAccessToken = "access-token",
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

    private static async Task SeedTwoParticipantLifecycleAsync(StreamingProxyGAgent agent)
    {
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
                    NyxIdAccessToken = "access-token",
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
                    RoutePreference = "route-a",
                    Model = "model-a",
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

    private static StreamingProxyNyxParticipantCoordinator CreateNyxCoordinator(
        IStreamingProxyRoomCommandService roomCommandService,
        Func<LLMRequest, LLMResponse>? responseFactory = null,
        string? servicesJson = null)
    {
        var httpClient = new HttpClient(new StreamingProxyTestHttpHandler(servicesJson));
        responseFactory ??= request => new LLMResponse
        {
            Content = $"reply from {request.RequestId}",
        };
        var provider = new StubNyxIdChatProviderFactory((request, _) => Task.FromResult(responseFactory(request)));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cli:App:NyxId:Authority"] = "https://nyx.example.com",
            })
            .Build();

        return new StreamingProxyNyxParticipantCoordinator(
            provider,
            configuration,
            new StubHttpClientFactory(httpClient),
            NullLogger<StreamingProxyNyxParticipantCoordinator>.Instance);
    }

    private sealed class StubNyxIdChatProviderFactory(
        Func<LLMRequest, CancellationToken, Task<LLMResponse>> buildResponseAsync)
        : ILLMProviderFactory, ILLMProvider
    {
        public string Name => "nyxid";

        public ILLMProvider GetProvider(string name)
        {
            _ = name;
            return this;
        }

        public ILLMProvider GetDefault() => this;

        public IReadOnlyList<string> GetAvailableProviders() => [Name];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            var response = await buildResponseAsync(request, ct);
            if (!string.IsNullOrEmpty(response.Content))
                yield return new LLMStreamChunk { DeltaContent = response.Content };

            yield return new LLMStreamChunk
            {
                IsLast = true,
                Usage = response.Usage,
                FinishReason = response.FinishReason,
            };
        }
    }

    private static async Task<(int StatusCode, string Body, string? Location)> ExecuteResultAsync(IResult result)
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
        return (
            context.Response.StatusCode,
            await new StreamReader(context.Response.Body).ReadToEndAsync(),
            context.Response.Headers.Location.ToString());
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

    private static async Task InvokeTaskAsync(string methodName, params object[] args)
    {
        var method = typeof(StreamingProxyEndpoints).GetMethod(
            methodName,
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = method.Invoke(null, NormalizeEndpointArgs(method, args));
        await InvokeTaskAsync(result);
    }

    private static object[] NormalizeEndpointArgs(MethodInfo method, object[] args)
    {
        var parameters = method.GetParameters();
        if (parameters.Length == args.Length &&
            ParametersMatchArgs(parameters, args))
        {
            return args;
        }

        return RebuildEndpointArgs(parameters, args.ToList());
    }

    private static bool ParametersMatchArgs(ParameterInfo[] parameters, object[] args)
    {
        for (var i = 0; i < parameters.Length; i++)
        {
            if (!parameters[i].ParameterType.IsInstanceOfType(args[i]))
                return false;
        }

        return true;
    }

    private static object[] RebuildEndpointArgs(
        ParameterInfo[] parameters,
        List<object> args)
    {
        var used = new bool[args.Count];
        var rebuilt = new List<object>(parameters.Length);
        foreach (var parameter in parameters)
        {
            var index = -1;
            for (var i = 0; i < args.Count; i++)
            {
                if (!used[i] && parameter.ParameterType.IsInstanceOfType(args[i]))
                {
                    index = i;
                    break;
                }
            }

            if (index >= 0)
            {
                used[index] = true;
                rebuilt.Add(args[index]);
                continue;
            }

            if (parameter.ParameterType == typeof(IGAgentActorRegistryCommandPort) ||
                parameter.ParameterType == typeof(IGAgentActorRegistryQueryPort) ||
                parameter.ParameterType == typeof(IScopeResourceAdmissionPort))
            {
                var store = args.OfType<StubGAgentActorStore>().FirstOrDefault() ?? new StubGAgentActorStore();
                rebuilt.Add(store);
                continue;
            }

            if (parameter.ParameterType == typeof(IStreamingProxyRoomCommandService))
            {
                rebuilt.Add(args.OfType<IStreamingProxyRoomCommandService>().FirstOrDefault() ?? new StubRoomCommandService());
                continue;
            }

            if (parameter.ParameterType == typeof(IStreamingProxyRoomSubscriptionObservationPort))
            {
                rebuilt.Add(args.OfType<IStreamingProxyRoomSubscriptionObservationPort>().FirstOrDefault() ?? new StubRoomSubscriptionObservationPort());
                continue;
            }

            if (parameter.ParameterType == typeof(ICommandInteractionService<StreamingProxyRoomChatCommand, StreamingProxyRoomChatAcceptedReceipt, StreamingProxyRoomChatStartError, StreamingProxyRoomSessionEnvelope, StreamingProxyProjectionCompletionStatus>))
            {
                rebuilt.Add(args
                    .OfType<ICommandInteractionService<StreamingProxyRoomChatCommand, StreamingProxyRoomChatAcceptedReceipt, StreamingProxyRoomChatStartError, StreamingProxyRoomSessionEnvelope, StreamingProxyProjectionCompletionStatus>>()
                    .FirstOrDefault() ?? new StubStreamingProxyRoomChatInteractionService());
                continue;
            }

            if (parameter.ParameterType == typeof(ILoggerFactory))
            {
                rebuilt.Add(args.OfType<ILoggerFactory>().FirstOrDefault() ?? NullLoggerFactory.Instance);
                continue;
            }

            if (parameter.ParameterType == typeof(CancellationToken))
            {
                rebuilt.Add(args.OfType<CancellationToken>().FirstOrDefault());
                continue;
            }

            throw new InvalidOperationException($"Unable to normalize endpoint argument {parameter.Name}:{parameter.ParameterType.FullName}.");
        }

        return rebuilt.ToArray();
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

        public async Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            Dispatches.Add((actorId, envelope));
            var actor = await runtime.GetAsync(actorId);
            if (actor is not null)
                await actor.HandleEventAsync(envelope, ct);
            return DispatchAdmissionFactory.Create(actorId, envelope);
        }
    }

    private sealed class ThrowingActorDispatchPort(Exception exception) : IActorDispatchPort
    {
        public Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            _ = actorId;
            _ = envelope;
            _ = ct;
            return Task.FromException<DispatchAdmission>(exception);
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
        public Exception? ThrowOnExecute { get; init; }
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
            if (ThrowOnExecute is not null)
                throw ThrowOnExecute;

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

        async Task<RealtimeSessionResult<StreamingProxyRoomChatAcceptedReceipt, StreamingProxyRoomChatStartError, StreamingProxyProjectionCompletionStatus>>
            IRealtimeSession<StreamingProxyRoomChatCommand, StreamingProxyRoomChatAcceptedReceipt, StreamingProxyRoomChatStartError, StreamingProxyRoomSessionEnvelope, StreamingProxyProjectionCompletionStatus>.ExecuteAsync(
                StreamingProxyRoomChatCommand inbound,
                Func<StreamingProxyRoomSessionEnvelope, CancellationToken, ValueTask> emitAsync,
                Func<StreamingProxyRoomChatAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync,
                CancellationToken ct)
        {
            return await ExecuteAsync(inbound, emitAsync, onAcceptedAsync, ct);
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

        public Task<StreamingProxyRoomSubscriptionObservationAttachment?> AttachAsync(
            string roomId,
            IEventSink<StreamingProxyRoomSessionEnvelope> sink,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _sink = sink;
            AttachCalls.Add((roomId, sink));
            Attached.TrySetResult(true);
            return Task.FromResult<StreamingProxyRoomSubscriptionObservationAttachment?>(new StreamingProxyRoomSubscriptionObservationAttachment(
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

        public List<(string actorId, string sessionId)> AttachExistingCalls { get; } = [];
        public List<(string actorId, string subscriptionId)> AttachExistingSubscriptionCalls { get; } = [];
        public List<StreamingProxyRoomSessionEnvelope> Messages { get; } = [];
        public List<IStreamingProxyRoomSessionProjectionLease> AttachedLeases { get; } = [];
        public int AttachCount { get; private set; }
        public int DetachCount { get; private set; }
        public int ReleaseCount { get; private set; }

        public TaskCompletionSource<bool> Attached { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

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

        public async Task<EventSinkProjectionAttachment<IStreamingProxyRoomSessionProjectionLease>?> AttachExistingSubscriptionProjectionAsync(
            string actorId,
            string subscriptionId,
            IEventSink<StreamingProxyRoomSessionEnvelope> sink,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            AttachExistingSubscriptionCalls.Add((actorId, subscriptionId));
            if (ReturnNullLease)
                return null;

            _lease = new StubRoomSessionProjectionLease(actorId, subscriptionId);
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
        public List<(string RootActorId, string SessionId, StreamingProxyRoomSessionEnvelope Event)> Published { get; } = [];
        public int SubscribeCalls { get; private set; }
        public string? LastRootActorId { get; private set; }
        public string? LastSessionId { get; private set; }

        public Task PublishAsync(
            string rootActorId,
            string sessionId,
            StreamingProxyRoomSessionEnvelope evt,
            CancellationToken ct = default)
        {
            _ = ct;
            Published.Add((rootActorId, sessionId, evt));
            return Task.CompletedTask;
        }

        public Task<IAsyncDisposable> SubscribeAsync(
            string rootActorId,
            string sessionId,
            Func<StreamingProxyRoomSessionEnvelope, ValueTask> handler,
            CancellationToken ct = default)
        {
            SubscribeCalls++;
            LastRootActorId = rootActorId;
            LastSessionId = sessionId;
            _ = handler;
            _ = ct;
            return Task.FromResult<IAsyncDisposable>(new NoopAsyncDisposable());
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

    private static IProjectionScopeAttachExistingLeaseLookup<StreamingProxyRoomSessionRuntimeLease> CreateRoomSessionAttachExistingLookup(
        IActorRuntime runtime) =>
        new ProjectionScopeAttachExistingLeaseLookup<StreamingProxyRoomSessionRuntimeLease, StreamingProxyRoomSessionProjectionContext>(
            runtime,
            request => new StreamingProxyRoomSessionProjectionContext
            {
                RootActorId = request.RootActorId,
                ProjectionKind = request.ProjectionKind,
                SessionId = request.SessionId,
            },
            (_, context) => new StreamingProxyRoomSessionRuntimeLease(context));

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
        public ScopeResourceAdmissionResult AdmissionResult { get; init; } = ScopeResourceAdmissionResult.Allowed();

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
            AddedActors.Add((registration.ScopeId, registration.AgentKind, registration.ActorId));
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

            RemovedActors.Add((registration.ScopeId, registration.AgentKind, registration.ActorId));
            return Task.FromResult(new GAgentActorRegistryCommandReceipt(
                registration,
                GAgentActorRegistryCommandStage.AdmissionRemoved));
        }

        public Task<ScopeResourceAdmissionResult> AuthorizeTargetAsync(
            ScopeResourceTarget target,
            CancellationToken cancellationToken = default)
            => Task.FromResult(AdmissionResult);
    }

    private sealed class StubRoomCommandService(
        StreamingProxyRoomCreateResult? result = null)
        : IStreamingProxyRoomCommandService
    {
        public List<StreamingProxyRoomCreateCommand> Commands { get; } = [];
        public List<StreamingProxyRoomPostMessageCommand> PostMessageCommands { get; } = [];
        public List<StreamingProxyRoomJoinCommand> JoinCommands { get; } = [];
        public List<StreamingProxyRoomLeaveCommand> LeaveCommands { get; } = [];
        public List<StreamingProxyRoomTerminalStateCommand> TerminalCommands { get; } = [];
        public List<StreamingProxyRoomParticipantsResolvedCommand> ParticipantsResolvedCommands { get; } = [];
        public List<StreamingProxyRoomParticipantReplyObservedCommand> ReplyObservedCommands { get; } = [];
        public List<StreamingProxyRoomParticipantReplyFailedCommand> ReplyFailedCommands { get; } = [];
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
                StreamingProxyRoomJoinStatus.Accepted,
                command.AgentId?.Trim(),
                command.DisplayName?.Trim()));
        }

        public Task<StreamingProxyRoomLeaveResult> LeaveAsync(
            StreamingProxyRoomLeaveCommand command,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LeaveCommands.Add(command);
            return Task.FromResult(new StreamingProxyRoomLeaveResult(
                StreamingProxyRoomLeaveStatus.Accepted,
                command.AgentId?.Trim()));
        }

        public Task PublishTerminalStateAsync(
            StreamingProxyRoomTerminalStateCommand command,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TerminalCommands.Add(command);
            return Task.CompletedTask;
        }

        public Task SubmitParticipantsResolvedAsync(
            StreamingProxyRoomParticipantsResolvedCommand command,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ParticipantsResolvedCommands.Add(command);
            return Task.CompletedTask;
        }

        public Task SubmitParticipantReplyObservedAsync(
            StreamingProxyRoomParticipantReplyObservedCommand command,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReplyObservedCommands.Add(command);
            return Task.CompletedTask;
        }

        public Task SubmitParticipantReplyFailedAsync(
            StreamingProxyRoomParticipantReplyFailedCommand command,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReplyFailedCommands.Add(command);
            return Task.CompletedTask;
        }
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StreamingProxyTestHttpHandler(string? servicesJson = null) : HttpMessageHandler
    {
        private const string DefaultServicesJson = """
            {
              "services": [
                {
                  "user_service_id": "svc-node-a",
                  "service_slug": "openclaw",
                  "display_name": "OpenClaw Node A",
                  "status": "ready",
                  "route_value": "/api/v1/proxy/s/openclaw/node-a",
                  "node_id": "node-a",
                  "allowed": true,
                  "models": ["model-a"]
                },
                {
                  "user_service_id": "svc-node-b",
                  "service_slug": "openclaw",
                  "display_name": "OpenClaw Node B",
                  "status": "ready",
                  "route_value": "/api/v1/proxy/s/openclaw/node-b",
                  "node_id": "node-b",
                  "allowed": true,
                  "models": ["model-b"]
                },
                {
                  "user_service_id": "svc-node-c",
                  "service_slug": "openclaw",
                  "display_name": "OpenClaw Node C",
                  "status": "ready",
                  "route_value": "/api/v1/proxy/s/openclaw/node-c",
                  "node_id": "node-c",
                  "allowed": true,
                  "models": ["model-c"]
                }
              ]
            }
            """;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(servicesJson ?? DefaultServicesJson),
            });
        }
    }

    private sealed class StubStreamProvider : IStreamProvider
    {
        private readonly Dictionary<string, StubStream> _streams = [];

        public IStream GetStream(string actorId) => GetTypedStream(actorId);

        public StubStream GetTypedStream(string actorId)
        {
            if (!_streams.TryGetValue(actorId, out var stream))
            {
                stream = new StubStream(actorId);
                _streams[actorId] = stream;
            }

            return stream;
        }
    }

    private sealed class StubStream(string streamId) : IStream
    {
        private Func<EventEnvelope, Task>? _envelopeHandler;
        private readonly Dictionary<System.Type, Func<IMessage, Task>> _typedHandlers = [];

        public string StreamId { get; } = streamId;

        public async Task ProduceAsync<T>(T message, CancellationToken ct = default)
            where T : IMessage
        {
            ct.ThrowIfCancellationRequested();
            if (message is EventEnvelope envelope && _envelopeHandler is not null)
            {
                await _envelopeHandler(envelope);
                return;
            }

            if (_typedHandlers.TryGetValue(typeof(T), out var handler))
                await handler(message);
        }

        public Task<IAsyncDisposable> SubscribeAsync<T>(
            Func<T, Task> handler,
            CancellationToken ct = default)
            where T : IMessage, new()
        {
            ct.ThrowIfCancellationRequested();
            if (typeof(T) == typeof(EventEnvelope))
                _envelopeHandler = envelope => ((Func<EventEnvelope, Task>)(object)handler)(envelope);
            else
                _typedHandlers[typeof(T)] = message => handler((T)message);

            return Task.FromResult<IAsyncDisposable>(new NoopAsyncDisposable());
        }

        public Task UpsertRelayAsync(StreamForwardingBinding binding, CancellationToken ct = default) => Task.CompletedTask;
        public Task RemoveRelayAsync(string targetStreamId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<StreamForwardingBinding>> ListRelaysAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<StreamForwardingBinding>>([]);
    }

    private sealed class StubActorEventSubscriptionProvider(StubStreamProvider streams) : IActorEventSubscriptionProvider
    {
        public Task<IAsyncDisposable> SubscribeAsync<TMessage>(
            string actorId,
            Func<TMessage, Task> handler,
            CancellationToken ct = default)
            where TMessage : class, IMessage, new() =>
            streams.GetTypedStream(actorId).SubscribeAsync(handler, ct);
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

    private sealed class StubRoomParticipantsQueryPort : IStreamingProxyRoomParticipantsQueryPort
    {
        private readonly StreamingProxyRoomParticipantsSnapshot? _snapshot;
        public List<string> Queries { get; } = [];

        public StubRoomParticipantsQueryPort(StreamingProxyRoomParticipantsSnapshot? snapshot = null)
        {
            _snapshot = snapshot;
        }

        public Task<StreamingProxyRoomParticipantsSnapshot?> GetAsync(
            string rootActorId,
            CancellationToken ct = default)
        {
            Queries.Add(rootActorId);
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(_snapshot);
        }
    }

    private sealed class RecordingProjectionWriteDispatcher<TReadModel>
        : IProjectionWriteDispatcher<TReadModel>
        where TReadModel : class, IProjectionReadModel
    {
        public List<TReadModel> Upserts { get; } = [];
        public List<string> Deletes { get; } = [];

        public Task<ProjectionWriteResult> UpsertAsync(
            TReadModel readModel,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Upserts.Add(readModel);
            return Task.FromResult(ProjectionWriteResult.Applied());
        }

        public Task<ProjectionWriteResult> DeleteAsync(
            string id,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Deletes.Add(id);
            return Task.FromResult(ProjectionWriteResult.Applied());
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
