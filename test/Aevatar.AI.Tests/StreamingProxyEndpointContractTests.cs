using System.IO;
using System.Net;
using System.Reflection;
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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Hosting;
using static Aevatar.GAgents.StreamingProxy.StreamingProxyEndpoints;

namespace Aevatar.AI.Tests;

public sealed partial class StreamingProxyEndpointContractTests : StreamingProxyTestBase
{
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

    private static readonly MethodInfo HandleCreateRoomAsyncMethod = typeof(StreamingProxyEndpoints)
        .GetMethod("HandleCreateRoomAsync", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("HandleCreateRoomAsync not found.");

    private static readonly MethodInfo HandleListParticipantsAsyncMethod = typeof(StreamingProxyEndpoints)
        .GetMethod("HandleListParticipantsAsync", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("HandleListParticipantsAsync not found.");

    [Fact]
    public async Task HandleCreateRoomAsync_ShouldDelegateRoomCreationToCommandService()
    {
        var service = new RecordingRoomCommandService(
            new StreamingProxyRoomCreateResult(
                StreamingProxyRoomCreateStatus.Created,
                "room-123",
                "Summary Standup"));

        var result = await InvokeHandleCreateRoomAsync(
            CreateScopedHttpContext(),
            "scope-a",
            new StreamingProxyEndpoints.CreateRoomRequest("  Summary Standup  "),
            service,
            CancellationToken.None);

        var (statusCode, body, _) = await ExecuteResultAsync(result);

        statusCode.Should().Be(StatusCodes.Status200OK);
        service.Commands.Should().ContainSingle();
        service.Commands[0].Should().Be(new StreamingProxyRoomCreateCommand("scope-a", "  Summary Standup  "));
        body.Should().Contain("room-123");
        body.Should().Contain("Summary Standup");
    }

    [Fact]
    public async Task HandleCreateRoomAsync_ShouldMapAdmissionUnavailableToServiceUnavailable()
    {
        var service = new RecordingRoomCommandService(
            new StreamingProxyRoomCreateResult(
                StreamingProxyRoomCreateStatus.AdmissionUnavailable,
                null,
                "Incident Room"));

        var result = await InvokeHandleCreateRoomAsync(
            CreateScopedHttpContext(),
            "scope-a",
            new StreamingProxyEndpoints.CreateRoomRequest("Incident Room"),
            service,
            CancellationToken.None);

        var (statusCode, body, _) = await ExecuteResultAsync(result);

        statusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        body.Should().Contain("Failed to create room");
    }

    [Fact]
    public async Task HandleCreateRoomAsync_ShouldMapCommandFailureToServerError()
    {
        var service = new RecordingRoomCommandService(
            new StreamingProxyRoomCreateResult(
                StreamingProxyRoomCreateStatus.Failed,
                null,
                "Incident Room"));

        var result = await InvokeHandleCreateRoomAsync(
            CreateScopedHttpContext(),
            "scope-a",
            new StreamingProxyEndpoints.CreateRoomRequest("Incident Room"),
            service,
            CancellationToken.None);

        var (statusCode, body, _) = await ExecuteResultAsync(result);

        statusCode.Should().Be(StatusCodes.Status500InternalServerError);
        body.Should().Contain("Failed to create room");
    }

    [Fact]
    public async Task HandleListParticipantsAsync_ShouldReturnProjectedParticipantsFromContractQuery()
    {
        var joinedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-04-14T10:00:00+08:00"));
        var participantsQueryPort = new RecordingRoomParticipantsQueryPort
        {
            Result = new StreamingProxyRoomParticipantsSnapshot
            {
                RootActorId = "room-1",
                StateVersion = 5,
                UpdatedAt = joinedAt,
                Participants =
                {
                    new StreamingProxyRoomParticipantSnapshotEntry
                    {
                        AgentId = "agent-1",
                        DisplayName = "Bot",
                        JoinedAt = joinedAt,
                    },
                },
            },
        };
        var loggerFactory = LoggerFactory.Create(_ => { });

        var result = await InvokeHandleListParticipantsAsync(
            CreateScopedHttpContext(),
            "scope-a",
            "room-1",
            participantsQueryPort,
            loggerFactory,
            CancellationToken.None);

        var (statusCode, body, _) = await ExecuteResultAsync(result);

        statusCode.Should().Be(StatusCodes.Status200OK);
        body.Should().Contain("agent-1");
        body.Should().Contain("Bot");
        participantsQueryPort.Queries.Should().ContainSingle().Which.Should().Be("room-1");
    }

    [Fact]
    public async Task HandleListParticipantsAsync_ShouldReturnServerError_WhenParticipantsQueryThrows()
    {
        var participantsQueryPort = new RecordingRoomParticipantsQueryPort
        {
            ThrowOnGet = new InvalidOperationException("list failed"),
        };
        var loggerFactory = LoggerFactory.Create(_ => { });

        var result = await InvokeHandleListParticipantsAsync(
            CreateScopedHttpContext(),
            "scope-a",
            "room-1",
            participantsQueryPort,
            loggerFactory,
            CancellationToken.None);

        var (statusCode, body, _) = await ExecuteResultAsync(result);

        statusCode.Should().Be(StatusCodes.Status500InternalServerError);
        body.Should().Contain("Failed to list participants");
    }

    [Fact]
    public async Task HandleCreateRoomAsync_ShouldRejectMismatchedAuthenticatedScope()
    {
        var service = new RecordingRoomCommandService(
            new StreamingProxyRoomCreateResult(
                StreamingProxyRoomCreateStatus.Created,
                "room-denied",
                "Denied Room"));

        var result = await InvokeHandleCreateRoomAsync(
            CreateScopedHttpContext("scope-b"),
            "scope-a",
            new StreamingProxyEndpoints.CreateRoomRequest("Denied Room"),
            service,
            CancellationToken.None);

        var (statusCode, body, _) = await ExecuteResultAsync(result);

        statusCode.Should().Be(StatusCodes.Status403Forbidden);
        body.Should().Contain("SCOPE_ACCESS_DENIED");
        body.Should().Contain("Authenticated scope does not match requested scope.");
        service.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleListParticipantsAsync_ShouldRejectMismatchedAuthenticatedScope()
    {
        var participantsQueryPort = new RecordingRoomParticipantsQueryPort();
        var loggerFactory = LoggerFactory.Create(_ => { });

        var result = await InvokeHandleListParticipantsAsync(
            CreateScopedHttpContext("scope-b"),
            "scope-a",
            "room-1",
            participantsQueryPort,
            loggerFactory,
            CancellationToken.None);

        var (statusCode, body, _) = await ExecuteResultAsync(result);

        statusCode.Should().Be(StatusCodes.Status403Forbidden);
        body.Should().Contain("SCOPE_ACCESS_DENIED");
        body.Should().Contain("Authenticated scope does not match requested scope.");
    }

    private static async Task<IResult> InvokeHandleCreateRoomAsync(
        HttpContext context,
        string scopeId,
        StreamingProxyEndpoints.CreateRoomRequest? request,
        IStreamingProxyRoomCommandService roomCommandService,
        CancellationToken ct)
    {
        return await (Task<IResult>)HandleCreateRoomAsyncMethod.Invoke(
            null,
            [context, scopeId, request, roomCommandService, ct])!;
    }

    private static async Task<IResult> InvokeHandleListParticipantsAsync(
        HttpContext context,
        string scopeId,
        string roomId,
        IStreamingProxyRoomParticipantsQueryPort participantsQueryPort,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        return await (Task<IResult>)HandleListParticipantsAsyncMethod.Invoke(
            null,
            [context, scopeId, roomId, new RecordingGAgentActorStore([]), participantsQueryPort, loggerFactory, ct])!;
    }

    private sealed class RecordingGAgentActorStore(List<string> operations) :
        IGAgentActorRegistryCommandPort,
        IGAgentActorRegistryQueryPort,
        IScopeResourceAdmissionPort
    {
        public List<(string ScopeId, string AgentKind, string ActorId)> AddedActors { get; } = [];
        public List<(string ScopeId, string AgentKind, string ActorId)> RemovedActors { get; } = [];
        public Exception? ThrowOnRegister { get; init; }
        public Exception? ThrowOnUnregister { get; init; }
        public GAgentActorRegistryCommandStage RegisterStage { get; init; } =
            GAgentActorRegistryCommandStage.AdmissionVisible;

        public Task<GAgentActorRegistrySnapshot> ListActorsAsync(
            string scopeId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new GAgentActorRegistrySnapshot(
                scopeId,
                [],
                0,
                DateTimeOffset.MinValue,
                DateTimeOffset.UtcNow));

        public Task<GAgentActorRegistryCommandReceipt> RegisterActorAsync(
            GAgentActorRegistration registration,
            CancellationToken cancellationToken = default)
        {
            operations.Add($"store:add:{registration.ActorId}");
            AddedActors.Add((registration.ScopeId, registration.AgentKind, registration.ActorId));
            if (ThrowOnRegister is not null)
                throw ThrowOnRegister;

            return Task.FromResult(new GAgentActorRegistryCommandReceipt(
                registration,
                RegisterStage));
        }

        public Task<GAgentActorRegistryCommandReceipt> UnregisterActorAsync(
            GAgentActorRegistration registration,
            CancellationToken cancellationToken = default)
        {
            operations.Add($"store:remove:{registration.ActorId}");
            RemovedActors.Add((registration.ScopeId, registration.AgentKind, registration.ActorId));
            if (ThrowOnUnregister is not null)
                throw ThrowOnUnregister;

            return Task.FromResult(new GAgentActorRegistryCommandReceipt(
                registration,
                GAgentActorRegistryCommandStage.AdmissionRemoved));
        }

        public Task<ScopeResourceAdmissionResult> AuthorizeTargetAsync(
            ScopeResourceTarget target,
            CancellationToken cancellationToken = default)
            => Task.FromResult(ScopeResourceAdmissionResult.Allowed());
    }

    private sealed class RecordingRoomCommandService(StreamingProxyRoomCreateResult result)
        : IStreamingProxyRoomCommandService
    {
        public List<StreamingProxyRoomCreateCommand> Commands { get; } = [];

        public Task<StreamingProxyRoomCreateResult> CreateRoomAsync(
            StreamingProxyRoomCreateCommand command,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(command);
            return Task.FromResult(result);
        }

        public Task<StreamingProxyRoomPostMessageResult> PostMessageAsync(
            StreamingProxyRoomPostMessageCommand command,
            CancellationToken cancellationToken = default)
        {
            _ = command;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new StreamingProxyRoomPostMessageResult(
                StreamingProxyRoomPostMessageStatus.Accepted));
        }

        public Task<StreamingProxyRoomJoinResult> JoinAsync(
            StreamingProxyRoomJoinCommand command,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new StreamingProxyRoomJoinResult(
                StreamingProxyRoomJoinStatus.Accepted,
                command.AgentId?.Trim(),
                command.DisplayName?.Trim()));
        }

        public Task<StreamingProxyRoomLeaveResult> LeaveAsync(
            StreamingProxyRoomLeaveCommand command,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new StreamingProxyRoomLeaveResult(
                StreamingProxyRoomLeaveStatus.Accepted,
                command.AgentId?.Trim()));
        }

        public Task PublishTerminalStateAsync(
            StreamingProxyRoomTerminalStateCommand command,
            CancellationToken cancellationToken = default)
        {
            _ = command;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task SubmitParticipantsResolvedAsync(
            StreamingProxyRoomParticipantsResolvedCommand command,
            CancellationToken cancellationToken = default)
        {
            _ = command;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task SubmitParticipantReplyObservedAsync(
            StreamingProxyRoomParticipantReplyObservedCommand command,
            CancellationToken cancellationToken = default)
        {
            _ = command;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task SubmitParticipantReplyFailedAsync(
            StreamingProxyRoomParticipantReplyFailedCommand command,
            CancellationToken cancellationToken = default)
        {
            _ = command;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingRoomParticipantsQueryPort : IStreamingProxyRoomParticipantsQueryPort
    {
        public List<string> Queries { get; } = [];
        public Exception? ThrowOnGet { get; init; }
        public StreamingProxyRoomParticipantsSnapshot? Result { get; init; }

        public Task<StreamingProxyRoomParticipantsSnapshot?> GetAsync(
            string rootActorId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ThrowOnGet is not null)
                throw ThrowOnGet;

            Queries.Add(rootActorId);
            return Task.FromResult(Result);
        }
    }

}
