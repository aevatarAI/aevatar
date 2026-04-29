using Aevatar.AI.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.GAgents.StreamingProxy.Application.Rooms;
using Aevatar.Hosting;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Microsoft.Extensions.Logging;
using System.Threading.Channels;

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
        [FromServices] IStreamingProxyRoomCommandService roomCommandService,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;

        var result = await roomCommandService.CreateRoomAsync(
            new StreamingProxyRoomCreateCommand(scopeId, request?.RoomName),
            ct);

        return result.Status switch
        {
            StreamingProxyRoomCreateStatus.Created => Results.Ok(new
            {
                roomId = result.RoomId,
                roomName = result.RoomName,
            }),
            StreamingProxyRoomCreateStatus.AdmissionUnavailable => Results.Json(
                new { error = "Failed to create room" },
                statusCode: StatusCodes.Status503ServiceUnavailable),
            _ => Results.Json(
                new { error = "Failed to create room" },
                statusCode: StatusCodes.Status500InternalServerError),
        };
    }

    private static async Task<IResult> HandleListRoomsAsync(
        HttpContext http,
        string scopeId,
        [FromServices] IGAgentActorRegistryQueryPort registryQueryPort,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;

        var logger = loggerFactory.CreateLogger("Aevatar.GAgents.StreamingProxy.Endpoints");
        try
        {
            var snapshot = await registryQueryPort.ListActorsAsync(scopeId, ct);
            var group = snapshot.Groups.FirstOrDefault(g =>
                string.Equals(g.GAgentType, StreamingProxyDefaults.GAgentTypeName, StringComparison.Ordinal));
            var roomIds = group?.ActorIds ?? [];
            return Results.Ok(new
            {
                snapshot.ScopeId,
                snapshot.StateVersion,
                snapshot.UpdatedAt,
                snapshot.ObservedAt,
                Rooms = roomIds.Select(id => new { roomId = id }),
            });
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to list rooms from registry read model");
            return Results.Ok(new
            {
                ScopeId = scopeId,
                StateVersion = 0L,
                UpdatedAt = DateTimeOffset.MinValue,
                ObservedAt = DateTimeOffset.UtcNow,
                Rooms = Array.Empty<object>(),
            });
        }
    }

    private static async Task<IResult> HandleDeleteRoomAsync(
        HttpContext http,
        string scopeId,
        string roomId,
        [FromServices] IGAgentActorRegistryCommandPort registryCommandPort,
        [FromServices] IScopeResourceAdmissionPort admissionPort,
        [FromServices] IStreamingProxyParticipantStore participantStore,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;

        var logger = loggerFactory.CreateLogger("Aevatar.GAgents.StreamingProxy.Endpoints");
        var admissionError = await AuthorizeRoomAsync(
            admissionPort,
            scopeId,
            roomId,
            ScopeResourceOperation.Delete,
            ct);
        if (admissionError != null)
            return admissionError;

        try
        {
            await registryCommandPort.UnregisterActorAsync(
                new GAgentActorRegistration(scopeId, StreamingProxyDefaults.GAgentTypeName, roomId),
                ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to unregister room {RoomId} from registry", roomId);
            return Results.Json(
                new { error = "Failed to delete room" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        try
        {
            await participantStore.RemoveRoomAsync(roomId, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to remove participants for room {RoomId}", roomId);
        }
        return Results.Ok();
    }

    // ─── User Chat (trigger topic + SSE stream) ───

    private static async Task HandleChatAsync(
        HttpContext http,
        string scopeId,
        string roomId,
        ChatTopicRequest request,
        [FromServices] IActorRuntime actorRuntime,
        [FromServices] IScopeResourceAdmissionPort admissionPort,
        [FromServices] IStreamingProxyRoomSessionProjectionPort roomSessionProjectionPort,
        [FromServices] StreamingProxyChatDurableCompletionResolver durableCompletionResolver,
        [FromServices] IStreamingProxyParticipantStore participantStore,
        [FromServices] StreamingProxyNyxParticipantCoordinator participantCoordinator,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Aevatar.GAgents.StreamingProxy.Endpoints");
        var writer = new StreamingProxySseWriter(http.Response);
        IActor? actor = null;
        string? sessionId = null;

        try
        {
            if (await AevatarScopeAccessGuard.TryWriteScopeAccessDeniedAsync(http, scopeId, ct))
                return;

            if (!await TryAuthorizeRoomAsync(
                    http,
                    admissionPort,
                    scopeId,
                    roomId,
                    ScopeResourceOperation.Chat,
                    ct))
                return;

            var prompt = request.Prompt?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(prompt))
            {
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            actor = await actorRuntime.GetAsync(roomId);
            if (actor is null)
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            // Set up SSE response
            await writer.StartAsync(ct);

            var activityChannel = Channel.CreateUnbounded<StreamingProxyStreamSignal>();
            sessionId = request.SessionId ?? Guid.NewGuid().ToString("N");
            var eventChannel = new EventChannel<StreamingProxyRoomSessionEnvelope>();
            var projectionLease = await roomSessionProjectionPort.EnsureAndAttachAsync(
                token => roomSessionProjectionPort.EnsureChatProjectionAsync(actor.Id, sessionId, token),
                eventChannel,
                ct);
            if (projectionLease == null)
                throw new InvalidOperationException("StreamingProxy room session projection pipeline is unavailable.");

            Task? pumpTask = null;

            try
            {
                pumpTask = PumpRoomSessionEventsAsync(
                    eventChannel,
                    writer,
                    activityChannel.Writer);

                var accessToken = ExtractBearerToken(http);
                var preferredRoute = request.LlmRoute?.Trim();
                var defaultModel = request.LlmModel?.Trim();

                // Emit the room topic immediately so the client sees visible progress
                // even when Nyx participant discovery is slow.
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

                IReadOnlyList<StreamingProxyNyxParticipantDefinition> participants = string.IsNullOrWhiteSpace(accessToken)
                    ? Array.Empty<StreamingProxyNyxParticipantDefinition>()
                    : await participantCoordinator.EnsureParticipantsJoinedAsync(
                        scopeId,
                        roomId,
                        actor,
                        participantStore,
                        accessToken,
                        ct,
                        preferredRoute,
                        defaultModel);

                if (participants.Count > 0 && !string.IsNullOrWhiteSpace(accessToken))
                {
                    var successfulReplies = await participantCoordinator.GenerateRepliesAsync(
                        participants,
                        actor,
                        prompt,
                        sessionId,
                        accessToken,
                        ct,
                        participantStore,
                        roomId);
                    var participantTerminalState = DetermineParticipantTerminalState(successfulReplies);
                    await PublishTerminalStateAsync(
                        actor,
                        sessionId,
                        participantTerminalState.Status,
                        participantTerminalState.ErrorMessage,
                        ct);
                    await FinalizeFromLiveOrDurableCompletionAsync(
                        actor.Id,
                        sessionId,
                        activityChannel.Reader,
                        durableCompletionResolver,
                        writer,
                        null,
                        ct);
                    return;
                }

                var sawActivity = false;
                var sawAgentMessage = false;
                while (!ct.IsCancellationRequested)
                {
                    using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    idleCts.CancelAfter(sawAgentMessage
                        ? StreamingProxyDefaults.IdleCompletionTimeoutMs
                        : sawActivity
                            ? StreamingProxyDefaults.PostTopicTimeoutMs
                            : StreamingProxyDefaults.InitialResponseTimeoutMs);

                    try
                    {
                        if (!await activityChannel.Reader.WaitToReadAsync(idleCts.Token))
                            break;
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        break;
                    }

                    while (activityChannel.Reader.TryRead(out var signal))
                    {
                        sawActivity = true;
                        if (signal == StreamingProxyStreamSignal.AgentMessage)
                            sawAgentMessage = true;
                        if (signal is StreamingProxyStreamSignal.RunFinished or StreamingProxyStreamSignal.RunFailed)
                            return;
                    }
                }

                var idleTerminalState = DetermineIdleTerminalState(sawAgentMessage);
                await PublishTerminalStateAsync(
                    actor,
                    sessionId,
                    idleTerminalState.Status,
                    idleTerminalState.ErrorMessage,
                    ct);
                await FinalizeFromLiveOrDurableCompletionAsync(
                    actor.Id,
                    sessionId,
                    activityChannel.Reader,
                    durableCompletionResolver,
                    writer,
                    null,
                    ct);
            }
            finally
            {
                await roomSessionProjectionPort.DetachReleaseAndDisposeAsync(
                    projectionLease,
                    eventChannel,
                    () =>
                    {
                        activityChannel.Writer.TryComplete();
                        return Task.CompletedTask;
                    },
                    CancellationToken.None);

                if (pumpTask != null)
                {
                    try
                    {
                        await pumpTask;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        // Client disconnected.
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            await TryPublishCanceledTerminalStateAsync(actor, sessionId, durableCompletionResolver, logger);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "StreamingProxy chat failed for room {RoomId}", roomId);
            await TryPublishFailedTerminalStateAsync(
                actor,
                sessionId,
                "StreamingProxy chat failed before completion.",
                durableCompletionResolver,
                logger);
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
        [FromServices] IScopeResourceAdmissionPort admissionPort,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;

        if (string.IsNullOrWhiteSpace(request.AgentId) || string.IsNullOrWhiteSpace(request.Content))
            return Results.BadRequest(new { error = "agentId and content are required" });

        var admissionError = await AuthorizeRoomAsync(
            admissionPort,
            scopeId,
            roomId,
            ScopeResourceOperation.Use,
            ct);
        if (admissionError != null)
            return admissionError;

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
        [FromServices] IScopeResourceAdmissionPort admissionPort,
        [FromServices] IStreamingProxyRoomSessionProjectionPort roomSessionProjectionPort,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Aevatar.GAgents.StreamingProxy.Endpoints");
        var writer = new StreamingProxySseWriter(http.Response);

        try
        {
            if (await AevatarScopeAccessGuard.TryWriteScopeAccessDeniedAsync(http, scopeId, ct))
                return;

            if (!await TryAuthorizeRoomAsync(
                    http,
                    admissionPort,
                    scopeId,
                    roomId,
                    ScopeResourceOperation.Stream,
                    ct))
                return;

            var actor = await actorRuntime.GetAsync(roomId);
            if (actor is null)
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            await writer.StartAsync(ct);
            var sessionId = Guid.NewGuid().ToString("N");
            var eventChannel = new EventChannel<StreamingProxyRoomSessionEnvelope>();
            var projectionLease = await roomSessionProjectionPort.EnsureAndAttachAsync(
                token => roomSessionProjectionPort.EnsureSubscriptionProjectionAsync(actor.Id, sessionId, token),
                eventChannel,
                ct);
            if (projectionLease == null)
                throw new InvalidOperationException("StreamingProxy room session projection pipeline is unavailable.");

            Task? pumpTask = null;

            try
            {
                pumpTask = PumpRoomSessionEventsAsync(eventChannel, writer);
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Client disconnected — normal
            }
            finally
            {
                await roomSessionProjectionPort.DetachReleaseAndDisposeAsync(
                    projectionLease,
                    eventChannel,
                    null,
                    CancellationToken.None);

                if (pumpTask != null)
                {
                    try
                    {
                        await pumpTask;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        // Client disconnected.
                    }
                }
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

    private static async Task<IResult> HandleListParticipantsAsync(
        HttpContext http,
        string scopeId,
        string roomId,
        [FromServices] IScopeResourceAdmissionPort admissionPort,
        [FromServices] IStreamingProxyParticipantStore participantStore,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;

        var admissionError = await AuthorizeRoomAsync(
            admissionPort,
            scopeId,
            roomId,
            ScopeResourceOperation.ListParticipants,
            ct);
        if (admissionError != null)
            return admissionError;

        var logger = loggerFactory.CreateLogger("Aevatar.GAgents.StreamingProxy.Endpoints");
        try
        {
            var participants = await participantStore.ListAsync(roomId, ct);
            return Results.Ok(participants);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to list participants for room {RoomId}", roomId);
            return Results.Json(
                new { error = "Failed to list participants" },
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> HandleJoinAsync(
        HttpContext http,
        string scopeId,
        string roomId,
        JoinRoomRequest request,
        [FromServices] IActorRuntime actorRuntime,
        [FromServices] IScopeResourceAdmissionPort admissionPort,
        [FromServices] IStreamingProxyParticipantStore participantStore,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;

        if (string.IsNullOrWhiteSpace(request.AgentId))
            return Results.BadRequest(new { error = "agentId is required" });

        var admissionError = await AuthorizeRoomAsync(
            admissionPort,
            scopeId,
            roomId,
            ScopeResourceOperation.Join,
            ct);
        if (admissionError != null)
            return admissionError;

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

        var logger = loggerFactory.CreateLogger("Aevatar.GAgents.StreamingProxy.Endpoints");
        try
        {
            await participantStore.AddAsync(roomId, agentId, displayName, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to persist participant {AgentId} in room {RoomId}", agentId, roomId);
        }

        return Results.Ok(new { status = "joined", agentId });
    }

    // ─── Event mapping ───

    private static async ValueTask<StreamingProxyStreamSignal?> MapAndWriteEventAsync(EventEnvelope envelope, StreamingProxySseWriter writer)
    {
        if (TryGetObservedTerminalEvent(envelope, out var terminalEvent))
        {
            if (terminalEvent.Status == StreamingProxyChatSessionTerminalStatus.Failed)
            {
                await writer.WriteRunErrorAsync(
                    string.IsNullOrWhiteSpace(terminalEvent.ErrorMessage)
                        ? "StreamingProxy chat failed."
                        : terminalEvent.ErrorMessage,
                    CancellationToken.None);
                return StreamingProxyStreamSignal.RunFailed;
            }

            await writer.WriteRunFinishedAsync(CancellationToken.None);
            return StreamingProxyStreamSignal.RunFinished;
        }

        var payload = envelope.Payload;
        if (CommittedStateEventEnvelope.TryGetObservedPayload(envelope, out var observedPayload, out _, out _) &&
            observedPayload != null)
        {
            payload = observedPayload;
        }

        if (payload is null || !ShouldWriteToSse(envelope))
            return null;

        if (payload.Is(GroupChatTopicEvent.Descriptor))
        {
            var evt = payload.Unpack<GroupChatTopicEvent>();
            await writer.WriteTopicStartedAsync(evt.Prompt, evt.SessionId, CancellationToken.None);
            return StreamingProxyStreamSignal.TopicStarted;
        }
        else if (payload.Is(GroupChatMessageEvent.Descriptor))
        {
            var evt = payload.Unpack<GroupChatMessageEvent>();
            await writer.WriteAgentMessageAsync(evt.AgentId, evt.AgentName, evt.Content, 0, CancellationToken.None);
            return StreamingProxyStreamSignal.AgentMessage;
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

        return null;
    }

    private static async Task PumpRoomSessionEventsAsync(
        IEventSink<StreamingProxyRoomSessionEnvelope> eventSink,
        StreamingProxySseWriter writer,
        ChannelWriter<StreamingProxyStreamSignal>? signalWriter = null)
    {
        ArgumentNullException.ThrowIfNull(eventSink);
        ArgumentNullException.ThrowIfNull(writer);

        try
        {
            await foreach (var sessionEnvelope in eventSink.ReadAllAsync(CancellationToken.None))
            {
                if (sessionEnvelope.Envelope == null)
                    continue;

                var signal = await MapAndWriteEventAsync(sessionEnvelope.Envelope, writer);
                if (signal.HasValue)
                    signalWriter?.TryWrite(signal.Value);
            }
        }
        finally
        {
            signalWriter?.TryComplete();
        }
    }

    private static bool ShouldWriteToSse(EventEnvelope envelope) =>
        envelope.Route?.IsTopologyPublication() == true ||
        CommittedStateEventEnvelope.TryUnpack(envelope, out _) ||
        TryGetObservedTerminalEvent(envelope, out _);

    private static bool TryGetObservedTerminalEvent(
        EventEnvelope envelope,
        out StreamingProxyChatSessionTerminalStateChanged terminalEvent)
    {
        terminalEvent = new StreamingProxyChatSessionTerminalStateChanged();
        if (!CommittedStateEventEnvelope.TryGetObservedPayload(envelope, out var payload, out _, out _) ||
            payload?.Is(StreamingProxyChatSessionTerminalStateChanged.Descriptor) != true)
        {
            return false;
        }

        terminalEvent = payload.Unpack<StreamingProxyChatSessionTerminalStateChanged>();
        return !string.IsNullOrWhiteSpace(terminalEvent.SessionId);
    }

    private static async Task PublishTerminalStateAsync(
        IActor actor,
        string sessionId,
        StreamingProxyChatSessionTerminalStatus status,
        string? errorMessage,
        CancellationToken ct)
    {
        var terminalEvent = new StreamingProxyChatSessionTerminalStateChanged
        {
            SessionId = sessionId,
            Status = status,
            TerminalAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            ErrorMessage = errorMessage ?? string.Empty,
        };
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(terminalEvent),
            Route = new EnvelopeRoute
            {
                Direct = new DirectRoute
                {
                    TargetActorId = actor.Id,
                },
            },
        };
        await actor.HandleEventAsync(envelope, ct);
    }

    private static (StreamingProxyChatSessionTerminalStatus Status, string? ErrorMessage) DetermineParticipantTerminalState(
        int successfulReplies) =>
        successfulReplies > 0
            ? (StreamingProxyChatSessionTerminalStatus.Completed, null)
            : (StreamingProxyChatSessionTerminalStatus.Failed, "StreamingProxy chat completed without any participant replies.");

    private static (StreamingProxyChatSessionTerminalStatus Status, string? ErrorMessage) DetermineIdleTerminalState(
        bool sawAgentMessage) =>
        sawAgentMessage
            ? (StreamingProxyChatSessionTerminalStatus.Completed, null)
            : (StreamingProxyChatSessionTerminalStatus.Failed, "StreamingProxy chat timed out without any agent replies.");

    private static async Task FinalizeFromLiveOrDurableCompletionAsync(
        string actorId,
        string sessionId,
        ChannelReader<StreamingProxyStreamSignal> signalReader,
        StreamingProxyChatDurableCompletionResolver durableCompletionResolver,
        StreamingProxySseWriter writer,
        TimeSpan? terminalCompletionTimeout,
        CancellationToken ct)
    {
        var timeout = terminalCompletionTimeout ?? TimeSpan.FromMilliseconds(StreamingProxyDefaults.TerminalCompletionTimeoutMs);
        if (await WaitForTerminalSignalAsync(signalReader, timeout, ct))
            return;

        var durableCompletion = await durableCompletionResolver.ResolveAsync(actorId, sessionId, ct);
        switch (durableCompletion)
        {
            case StreamingProxyProjectionCompletionStatus.Failed:
                await writer.WriteRunErrorAsync("StreamingProxy chat failed.", CancellationToken.None);
                return;
            case StreamingProxyProjectionCompletionStatus.Completed:
                await writer.WriteRunFinishedAsync(CancellationToken.None);
                return;
            case StreamingProxyProjectionCompletionStatus.Unknown:
            default:
                await writer.WriteRunErrorAsync("StreamingProxy completion timed out.", CancellationToken.None);
                return;
        }
    }

    private static async Task TryPublishCanceledTerminalStateAsync(
        IActor? actor,
        string? sessionId,
        StreamingProxyChatDurableCompletionResolver durableCompletionResolver,
        ILogger logger)
    {
        if (actor is null || string.IsNullOrWhiteSpace(sessionId))
            return;

        try
        {
            var durableCompletion = await durableCompletionResolver.ResolveAsync(actor.Id, sessionId, CancellationToken.None);
            if (durableCompletion is StreamingProxyProjectionCompletionStatus.Completed or StreamingProxyProjectionCompletionStatus.Failed)
                return;

            await PublishTerminalStateAsync(
                actor,
                sessionId,
                StreamingProxyChatSessionTerminalStatus.Failed,
                "StreamingProxy chat was cancelled before completion.",
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to publish terminal cancellation state for room {RoomId}, session {SessionId}",
                actor.Id,
                sessionId);
        }
    }

    private static async Task TryPublishFailedTerminalStateAsync(
        IActor? actor,
        string? sessionId,
        string errorMessage,
        StreamingProxyChatDurableCompletionResolver durableCompletionResolver,
        ILogger logger)
    {
        if (actor is null || string.IsNullOrWhiteSpace(sessionId))
            return;

        try
        {
            var durableCompletion = await durableCompletionResolver.ResolveAsync(actor.Id, sessionId, CancellationToken.None);
            if (durableCompletion is StreamingProxyProjectionCompletionStatus.Completed or StreamingProxyProjectionCompletionStatus.Failed)
                return;

            await PublishTerminalStateAsync(
                actor,
                sessionId,
                StreamingProxyChatSessionTerminalStatus.Failed,
                errorMessage,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to publish terminal failure state for room {RoomId}, session {SessionId}",
                actor.Id,
                sessionId);
        }
    }

    private static async Task<bool> WaitForTerminalSignalAsync(
        ChannelReader<StreamingProxyStreamSignal> signalReader,
        TimeSpan timeout,
        CancellationToken ct)
    {
        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        waitCts.CancelAfter(timeout);

        try
        {
            while (await signalReader.WaitToReadAsync(waitCts.Token))
            {
                while (signalReader.TryRead(out var signal))
                {
                    if (signal is StreamingProxyStreamSignal.RunFinished or StreamingProxyStreamSignal.RunFailed)
                        return true;
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }

        return false;
    }

    private static async Task<IResult?> AuthorizeRoomAsync(
        IScopeResourceAdmissionPort admissionPort,
        string scopeId,
        string roomId,
        ScopeResourceOperation operation,
        CancellationToken ct)
    {
        var admission = await admissionPort.AuthorizeTargetAsync(
            new ScopeResourceTarget(
                scopeId,
                ScopeResourceKind.GAgentActor,
                StreamingProxyDefaults.GAgentTypeName,
                roomId,
                operation),
            ct);
        return MapAdmissionError(admission);
    }

    private static async Task<bool> TryAuthorizeRoomAsync(
        HttpContext http,
        IScopeResourceAdmissionPort admissionPort,
        string scopeId,
        string roomId,
        ScopeResourceOperation operation,
        CancellationToken ct)
    {
        var admissionError = await AuthorizeRoomAsync(admissionPort, scopeId, roomId, operation, ct);
        if (admissionError == null)
            return true;

        switch (admissionError)
        {
            case IStatusCodeHttpResult { StatusCode: { } statusCode }:
                http.Response.StatusCode = statusCode;
                break;
            default:
                http.Response.StatusCode = StatusCodes.Status500InternalServerError;
                break;
        }

        return false;
    }

    private static IResult? MapAdmissionError(ScopeResourceAdmissionResult admission) =>
        admission.Status switch
        {
            ScopeResourceAdmissionStatus.Allowed => null,
            ScopeResourceAdmissionStatus.NotFound => Results.NotFound(new { error = "Room not found" }),
            ScopeResourceAdmissionStatus.Denied or ScopeResourceAdmissionStatus.ScopeMismatch =>
                Results.Json(new { error = "Room access denied" }, statusCode: StatusCodes.Status403Forbidden),
            ScopeResourceAdmissionStatus.Unavailable =>
                Results.Json(new { error = "Room admission unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable),
            _ => Results.Json(new { error = "Room admission failed" }, statusCode: StatusCodes.Status503ServiceUnavailable),
        };

    // ─── Request DTOs ───

    public sealed record CreateRoomRequest(string? RoomName);
    public sealed record ChatTopicRequest(
        string? Prompt,
        string? SessionId = null,
        string? LlmRoute = null,
        string? LlmModel = null);
    public sealed record PostMessageRequest(string? AgentId, string? AgentName, string? Content, string? SessionId = null);
    public sealed record JoinRoomRequest(string? AgentId, string? DisplayName);

    private static string? ExtractBearerToken(HttpContext http)
    {
        var header = http.Request.Headers.Authorization.ToString().Trim();
        if (string.IsNullOrWhiteSpace(header))
            return null;

        const string prefix = "Bearer ";
        return header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? header[prefix.Length..].Trim()
            : null;
    }

    internal enum StreamingProxyStreamSignal
    {
        TopicStarted,
        AgentMessage,
        RunFinished,
        RunFailed,
    }
}
