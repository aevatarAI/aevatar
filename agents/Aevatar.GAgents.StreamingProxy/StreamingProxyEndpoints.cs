using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.StreamingProxy;

public static class StreamingProxyEndpoints
{
    public static IEndpointRouteBuilder MapStreamingProxyEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/scopes").WithTags("StreamingProxy");

        // Room management
        group.MapPost("/{scopeId}/streaming-proxy/rooms", HandleCreateRoomAsync);
        group.MapGet("/{scopeId}/streaming-proxy/rooms", HandleListRoomsAsync);
        group.MapDelete("/{scopeId}/streaming-proxy/rooms/{roomId}", HandleDeleteRoomAsync);

        // User triggers a discussion topic (SSE stream of all events)
        group.MapPost("/{scopeId}/streaming-proxy/rooms/{roomId}:chat", HandleChatAsync);

        // OpenClaw posts a message
        group.MapPost("/{scopeId}/streaming-proxy/rooms/{roomId}/messages", HandlePostMessageAsync);

        // OpenClaw subscribes to room message stream (SSE)
        group.MapGet("/{scopeId}/streaming-proxy/rooms/{roomId}/messages:stream", HandleMessageStreamAsync);

        // Participant management
        group.MapGet("/{scopeId}/streaming-proxy/rooms/{roomId}/participants", HandleListParticipantsAsync);
        group.MapPost("/{scopeId}/streaming-proxy/rooms/{roomId}/participants", HandleJoinAsync);

        return app;
    }

    // ─── Room CRUD ───

    private static async Task<IResult> HandleCreateRoomAsync(
        HttpContext http,
        string scopeId,
        [FromBody] CreateRoomRequest? request,
        [FromServices] StreamingProxyActorStore store,
        [FromServices] IActorRuntime actorRuntime,
        CancellationToken ct)
    {
        var roomName = request?.RoomName?.Trim();
        if (string.IsNullOrWhiteSpace(roomName))
            roomName = "Group Chat";

        var entry = await store.CreateRoomAsync(scopeId, roomName, ct);

        // Create the actor and initialize it
        var actor = await actorRuntime.CreateAsync<StreamingProxyGAgent>(entry.RoomId, ct);

        var initEvent = new GroupChatRoomInitializedEvent { RoomName = roomName };
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(initEvent),
            Route = new EnvelopeRoute { Direct = new DirectRoute { TargetActorId = actor.Id } },
        };
        await actor.HandleEventAsync(envelope, ct);

        return Results.Ok(new { roomId = entry.RoomId, roomName = entry.RoomName, createdAt = entry.CreatedAt });
    }

    private static async Task<IResult> HandleListRoomsAsync(
        HttpContext http,
        string scopeId,
        [FromServices] StreamingProxyActorStore store,
        CancellationToken ct)
    {
        var rooms = await store.ListRoomsAsync(scopeId, ct);
        return Results.Ok(rooms);
    }

    private static async Task<IResult> HandleDeleteRoomAsync(
        HttpContext http,
        string scopeId,
        string roomId,
        [FromServices] StreamingProxyActorStore store,
        CancellationToken ct)
    {
        var removed = await store.DeleteRoomAsync(scopeId, roomId, ct);
        return removed ? Results.Ok() : Results.NotFound();
    }

    // ─── User Chat (trigger topic + SSE stream) ───

    private static async Task HandleChatAsync(
        HttpContext http,
        string scopeId,
        string roomId,
        ChatTopicRequest request,
        [FromServices] IActorRuntime actorRuntime,
        [FromServices] IActorEventSubscriptionProvider subscriptionProvider,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Aevatar.GAgents.StreamingProxy.Endpoints");
        var writer = new StreamingProxySseWriter(http.Response);

        try
        {
            var prompt = request.Prompt?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(prompt))
            {
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            var actor = await actorRuntime.GetAsync(roomId);
            if (actor is null)
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            // Set up SSE response
            await writer.StartAsync(ct);

            // Subscribe to actor events — will receive all group chat events
            await using var subscription = await subscriptionProvider.SubscribeAsync<EventEnvelope>(
                actor.Id,
                async envelope => await MapAndWriteEventAsync(envelope, writer),
                ct);

            // Dispatch ChatRequestEvent to actor (which converts to GroupChatTopicEvent)
            var sessionId = request.SessionId ?? Guid.NewGuid().ToString("N");
            var chatRequest = new ChatRequestEvent
            {
                Prompt = prompt,
                SessionId = sessionId,
                ScopeId = scopeId,
            };
            var envelope = new EventEnvelope
            {
                Id = Guid.NewGuid().ToString("N"),
                Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                Payload = Any.Pack(chatRequest),
                Route = new EnvelopeRoute { Direct = new DirectRoute { TargetActorId = actor.Id } },
            };
            await actor.HandleEventAsync(envelope, ct);

            // Keep connection open until client disconnects or timeout
            // The SSE stream stays open to receive subsequent agent messages
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                // Client disconnected — normal
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "StreamingProxy chat failed for room {RoomId}", roomId);
            if (!writer.Started)
            {
                http.Response.StatusCode = StatusCodes.Status500InternalServerError;
                return;
            }
            await writer.WriteRunErrorAsync(ex.Message, CancellationToken.None);
        }
    }

    // ─── OpenClaw posts a message ───

    private static async Task<IResult> HandlePostMessageAsync(
        HttpContext http,
        string scopeId,
        string roomId,
        PostMessageRequest request,
        [FromServices] IActorRuntime actorRuntime,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.AgentId) || string.IsNullOrWhiteSpace(request.Content))
            return Results.BadRequest(new { error = "agentId and content are required" });

        var actor = await actorRuntime.GetAsync(roomId);
        if (actor is null)
            return Results.NotFound(new { error = "Room not found" });

        var messageEvent = new GroupChatMessageEvent
        {
            AgentId = request.AgentId.Trim(),
            AgentName = request.AgentName?.Trim() ?? request.AgentId.Trim(),
            Content = request.Content.Trim(),
            SessionId = request.SessionId ?? Guid.NewGuid().ToString("N"),
        };

        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(messageEvent),
            Route = new EnvelopeRoute { Direct = new DirectRoute { TargetActorId = actor.Id } },
        };
        await actor.HandleEventAsync(envelope, ct);

        return Results.Ok(new { status = "accepted" });
    }

    // ─── OpenClaw subscribes to message stream (SSE) ───

    private static async Task HandleMessageStreamAsync(
        HttpContext http,
        string scopeId,
        string roomId,
        [FromServices] IActorRuntime actorRuntime,
        [FromServices] IActorEventSubscriptionProvider subscriptionProvider,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Aevatar.GAgents.StreamingProxy.Endpoints");
        var writer = new StreamingProxySseWriter(http.Response);

        try
        {
            var actor = await actorRuntime.GetAsync(roomId);
            if (actor is null)
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            await writer.StartAsync(ct);

            // Subscribe to actor events — long-lived SSE connection
            await using var subscription = await subscriptionProvider.SubscribeAsync<EventEnvelope>(
                actor.Id,
                async envelope => await MapAndWriteEventAsync(envelope, writer),
                ct);

            // Keep connection open until client disconnects
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                // Client disconnected — normal
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "StreamingProxy message stream failed for room {RoomId}", roomId);
            if (!writer.Started)
            {
                http.Response.StatusCode = StatusCodes.Status500InternalServerError;
                return;
            }
            await writer.WriteRunErrorAsync(ex.Message, CancellationToken.None);
        }
    }

    // ─── Participant management ───

    private static Task<IResult> HandleListParticipantsAsync(
        HttpContext http,
        string scopeId,
        string roomId,
        [FromServices] StreamingProxyActorStore store,
        CancellationToken ct)
    {
        var participants = store.ListParticipants(scopeId, roomId);
        return Task.FromResult<IResult>(Results.Ok(participants));
    }

    private static async Task<IResult> HandleJoinAsync(
        HttpContext http,
        string scopeId,
        string roomId,
        JoinRoomRequest request,
        [FromServices] IActorRuntime actorRuntime,
        [FromServices] StreamingProxyActorStore store,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.AgentId))
            return Results.BadRequest(new { error = "agentId is required" });

        var actor = await actorRuntime.GetAsync(roomId);
        if (actor is null)
            return Results.NotFound(new { error = "Room not found" });

        var agentId = request.AgentId.Trim();
        var displayName = request.DisplayName?.Trim() ?? agentId;

        var joinEvent = new GroupChatParticipantJoinedEvent
        {
            AgentId = agentId,
            DisplayName = displayName,
        };

        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(joinEvent),
            Route = new EnvelopeRoute { Direct = new DirectRoute { TargetActorId = actor.Id } },
        };
        await actor.HandleEventAsync(envelope, ct);

        // Track participant in the store for query endpoints
        store.AddParticipant(scopeId, roomId, agentId, displayName);

        return Results.Ok(new { status = "joined", agentId });
    }

    // ─── Event mapping ───

    private static async ValueTask MapAndWriteEventAsync(EventEnvelope envelope, StreamingProxySseWriter writer)
    {
        var payload = envelope.Payload;
        if (payload is null)
            return;

        if (payload.Is(GroupChatTopicEvent.Descriptor))
        {
            var evt = payload.Unpack<GroupChatTopicEvent>();
            await writer.WriteTopicStartedAsync(evt.Prompt, evt.SessionId, CancellationToken.None);
        }
        else if (payload.Is(GroupChatMessageEvent.Descriptor))
        {
            var evt = payload.Unpack<GroupChatMessageEvent>();
            await writer.WriteAgentMessageAsync(evt.AgentId, evt.AgentName, evt.Content, 0, CancellationToken.None);
        }
        else if (payload.Is(GroupChatParticipantJoinedEvent.Descriptor))
        {
            var evt = payload.Unpack<GroupChatParticipantJoinedEvent>();
            await writer.WriteParticipantJoinedAsync(evt.AgentId, evt.DisplayName, CancellationToken.None);
        }
        else if (payload.Is(GroupChatParticipantLeftEvent.Descriptor))
        {
            var evt = payload.Unpack<GroupChatParticipantLeftEvent>();
            await writer.WriteParticipantLeftAsync(evt.AgentId, CancellationToken.None);
        }
    }

    // ─── Request DTOs ───

    public sealed record CreateRoomRequest(string? RoomName);
    public sealed record ChatTopicRequest(string? Prompt, string? SessionId = null);
    public sealed record PostMessageRequest(string? AgentId, string? AgentName, string? Content, string? SessionId = null);
    public sealed record JoinRoomRequest(string? AgentId, string? DisplayName);
}
