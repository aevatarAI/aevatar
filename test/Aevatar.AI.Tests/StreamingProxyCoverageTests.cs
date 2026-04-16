using System.IO;
using System.Reflection;
using Aevatar.Foundation.Abstractions;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Studio.Application.Studio.Abstractions;
using StreamingProxyParticipant = Aevatar.Studio.Application.Studio.Abstractions.StreamingProxyParticipant;
using Google.Protobuf;
using Any = Google.Protobuf.WellKnownTypes.Any;
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

        var descriptor = services.FirstOrDefault(d =>
            d.ServiceType == typeof(StreamingProxyNyxParticipantCoordinator));

        descriptor.Should().NotBeNull();
        descriptor!.Lifetime.Should().Be(ServiceLifetime.Singleton);
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
            new DefaultHttpContext(),
            "scope-a",
            request,
            actorStore,
            runtime,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status200OK);
        response.Body.Should().Contain("roomName");
        actorStore.AddedActors.Should().ContainSingle(x => x.gagentType == StreamingProxyDefaults.GAgentTypeName);
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
            new DefaultHttpContext(),
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
            new DefaultHttpContext(),
            "scope-a",
            "room-1",
            actorStore,
            participantStore,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status200OK);
        actorStore.RemovedActors.Should().ContainSingle(x =>
            x.gagentType == StreamingProxyDefaults.GAgentTypeName && x.actorId == "room-1");
        participantStore.RemovedRooms.Should().ContainSingle(x => x == "room-1");
    }

    [Fact]
    public async Task HandleChatAsync_ShouldRejectEmptyPrompt()
    {
        var context = new DefaultHttpContext();
        var runtime = new StubActorRuntime();
        var subscriptions = new StubSubscriptionProvider();
        var participantStore = new StubParticipantStore();
        var coordinator = CreateNyxParticipantCoordinator();

        var method = typeof(StreamingProxyEndpoints).GetMethod(
            "HandleChatAsync",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var task = method.Invoke(null, [context, "scope-a", "room-a", new ChatTopicRequest(null), runtime, subscriptions, participantStore, coordinator, NullLoggerFactory.Instance, CancellationToken.None]);
        await InvokeTaskAsync(task);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task HandleMessageStreamAsync_ShouldRejectMissingRoom()
    {
        var context = new DefaultHttpContext();
        var runtime = new StubActorRuntime();
        var method = typeof(StreamingProxyEndpoints).GetMethod(
            "HandleMessageStreamAsync",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        var task = method.Invoke(
            null,
            [context, "scope-a", "missing", runtime, new StubSubscriptionProvider(), NullLoggerFactory.Instance, CancellationToken.None]);
        await InvokeTaskAsync(task);

        context.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task HandlePostMessageAsync_ShouldRejectMissingFieldsAndReturnAccepted()
    {
        var result = await InvokeResultAsync(
            "HandlePostMessageAsync",
            new DefaultHttpContext(),
            "scope-a",
            "room-a",
            new PostMessageRequest(null, "name", "content"),
            new StubActorRuntime(),
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);

        result = await InvokeResultAsync(
            "HandlePostMessageAsync",
            new DefaultHttpContext(),
            "scope-a",
            "missing-room",
            new PostMessageRequest("agent", null, "content"),
            new StubActorRuntime(),
            CancellationToken.None);

        response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status404NotFound);

        var runtime = new StubActorRuntime(new List<IActor> { new StubActor("room-a") });
        result = await InvokeResultAsync(
            "HandlePostMessageAsync",
            new DefaultHttpContext(),
            "scope-a",
            "room-a",
            new PostMessageRequest("agent", null, "content"),
            runtime,
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

        var result = await InvokeResultAsync(
            "HandleJoinAsync",
            new DefaultHttpContext(),
            "scope-a",
            "room-a",
            new JoinRoomRequest(null, null),
            runtime,
            participantStore,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);

        var joinRequest = new JoinRoomRequest("agent-1", "Alice");
        result = await InvokeResultAsync(
            "HandleJoinAsync",
            new DefaultHttpContext(),
            "scope-a",
            "room-a",
            joinRequest,
            runtime,
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
            new EventEnvelope { Payload = Any.Pack(new GroupChatTopicEvent { Prompt = "topic", SessionId = "s1" }) },
            new EventEnvelope { Payload = Any.Pack(new GroupChatMessageEvent { AgentId = "a1", AgentName = "A1", Content = "hi", SessionId = "s1" }) },
            new EventEnvelope { Payload = Any.Pack(new GroupChatParticipantJoinedEvent { AgentId = "a1", DisplayName = "A1" }) },
            new EventEnvelope { Payload = Any.Pack(new GroupChatParticipantLeftEvent { AgentId = "a1" }) },
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
    public async Task HandleListParticipantsAsync_ShouldReturnStoreParticipants()
    {
        var participantStore = new StubParticipantStore();
        participantStore.Participants["room-a"] =
        [
            new StreamingProxyParticipant("agent-1", "Alice", DateTimeOffset.UtcNow),
        ];

        var result = await InvokeResultAsync(
            "HandleListParticipantsAsync",
            new DefaultHttpContext(),
            "scope-a",
            "room-a",
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

        public string Id { get; }

        public IAgent Agent => new StubAgent();

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
        {
            _ = envelope;
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

    private sealed class StubSubscriptionProvider : IActorEventSubscriptionProvider
    {
        public Task<IAsyncDisposable> SubscribeAsync<TMessage>(
            string actorId,
            Func<TMessage, Task> handler,
            CancellationToken ct = default)
            where TMessage : class, IMessage, new()
        {
            _ = actorId;
            _ = handler;
            _ = ct;
            return Task.FromResult<IAsyncDisposable>(new NoopDisposable());
        }
    }

    private sealed class NoopDisposable : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubGAgentActorStore : IGAgentActorStore
    {
        public List<GAgentActorGroup> Groups { get; } = [];
        public List<(string gagentType, string actorId)> AddedActors { get; } = [];
        public List<(string gagentType, string actorId)> RemovedActors { get; } = [];

        public Task<IReadOnlyList<GAgentActorGroup>> GetAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<GAgentActorGroup>>(Groups.AsReadOnly());

        public Task AddActorAsync(string gagentType, string actorId, CancellationToken cancellationToken = default)
        {
            AddedActors.Add((gagentType, actorId));
            return Task.CompletedTask;
        }

        public Task RemoveActorAsync(string gagentType, string actorId, CancellationToken cancellationToken = default)
        {
            RemovedActors.Add((gagentType, actorId));
            return Task.CompletedTask;
        }
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
}
