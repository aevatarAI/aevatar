using Aevatar.AI.Abstractions;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
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
        return Results.Ok();
    }

    // ─── User Chat (trigger topic + SSE stream) ───

    private static async Task HandleChatAsync(
        HttpContext http,
        string scopeId,
        string roomId,
        ChatTopicRequest request,
        [FromServices] IActorRuntime actorRuntime,
        [FromServices] IActorDispatchPort actorDispatchPort,
        [FromServices] IScopeResourceAdmissionPort admissionPort,
        [FromServices] ICommandInteractionService<StreamingProxyRoomChatCommand, StreamingProxyRoomChatAcceptedReceipt, StreamingProxyRoomChatStartError, StreamingProxyRoomSessionEnvelope, StreamingProxyProjectionCompletionStatus> interactionService,
        [FromServices] StreamingProxyChatDurableCompletionResolver durableCompletionResolver,
        [FromServices] IStreamingProxyParticipantQueryPort participantQueryPort,
        [FromServices] StreamingProxyNyxParticipantCoordinator participantCoordinator,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Aevatar.GAgents.StreamingProxy.Endpoints");
        var writer = new StreamingProxySseWriter(http.Response);
        IActor? actor = null;
        var sessionId = request.SessionId ?? Guid.NewGuid().ToString("N");

        try
        {
            // Refactor (iter21/cluster-002-request-path-projection-session-priming):
            //   Old pattern: request handlers synchronously ensure projection/session leases and wait on live sinks.
            //   New principle: commands use accepted receipts; observation is owned by binders or attach-only sessions.
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

            var accessToken = ExtractBearerToken(http);
            var preferredRoute = request.LlmRoute?.Trim();
            var defaultModel = request.LlmModel?.Trim();
            var result = await interactionService.ExecuteAsync(
                new StreamingProxyRoomChatCommand(roomId, scopeId, prompt, sessionId),
                async (frame, token) =>
                {
                    await MapAndWriteRoomSessionEventAsync(frame, writer);
                },
                async (_, token) =>
                {
                    IReadOnlyList<StreamingProxyNyxParticipantDefinition> participants = string.IsNullOrWhiteSpace(accessToken)
                        ? Array.Empty<StreamingProxyNyxParticipantDefinition>()
                        : await participantCoordinator.EnsureParticipantsJoinedAsync(
                            scopeId,
                            roomId,
                            actor,
                            participantQueryPort,
                            accessToken,
                            token,
                            preferredRoute,
                            defaultModel);

                    if (participants.Count == 0 || string.IsNullOrWhiteSpace(accessToken))
                        return;

                    var terminalState = DetermineParticipantTerminalState(await participantCoordinator.GenerateRepliesAsync(
                        participants,
                        actor,
                        prompt,
                        sessionId,
                        accessToken,
                        token));
                    await PublishTerminalStateAsync(
                        actorDispatchPort,
                        actor.Id,
                        sessionId,
                        terminalState.Status,
                        terminalState.ErrorMessage,
                        token);
                },
                ct);

            if (!result.Succeeded)
            {
                switch (result.Error)
                {
                    case StreamingProxyRoomChatStartError.RoomNotFound:
                        http.Response.StatusCode = StatusCodes.Status404NotFound;
                        return;
                    case StreamingProxyRoomChatStartError.ProjectionUnavailable:
                        await writer.WriteRunErrorAsync(
                            "StreamingProxy room session projection pipeline is unavailable.",
                            CancellationToken.None);
                        return;
                    default:
                        await writer.WriteRunErrorAsync(
                            "StreamingProxy chat failed before completion.",
                            CancellationToken.None);
                        return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            await TryPublishCanceledTerminalStateAsync(actorDispatchPort, actor, sessionId, durableCompletionResolver, logger);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "StreamingProxy chat failed for room {RoomId}", roomId);
            await TryPublishFailedTerminalStateAsync(
                actorDispatchPort,
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
        [FromServices] IActorDispatchPort actorDispatchPort,
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
        await DispatchRoomEnvelopeAsync(actorDispatchPort, actor.Id, envelope, ct);

        return Results.Ok(new { status = "accepted" });
    }

    // ─── OpenClaw subscribes to message stream (SSE) ───

    private static async Task HandleMessageStreamAsync(
        HttpContext http,
        string scopeId,
        string roomId,
        [FromServices] IActorRuntime actorRuntime,
        [FromServices] IScopeResourceAdmissionPort admissionPort,
        [FromServices] IStreamingProxyRoomSubscriptionObservationPort subscriptionObservationPort,
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
            var eventChannel = new EventChannel<StreamingProxyRoomSessionEnvelope>();
            var attachment = await subscriptionObservationPort.AttachAsync(actor.Id, eventChannel, ct);

            Task? pumpTask = null;

            try
            {
                pumpTask = PumpRoomSessionEventsAsync(eventChannel, writer);
                await WaitForClientDisconnectAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Client disconnected — normal
            }
            finally
            {
                await subscriptionObservationPort.DetachAndDisposeAsync(
                    attachment,
                    eventChannel,
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
        [FromServices] IStreamingProxyParticipantQueryPort participantQueryPort,
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
            var participants = await participantQueryPort.ListAsync(roomId, ct);
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
        [FromServices] IActorDispatchPort actorDispatchPort,
        [FromServices] IScopeResourceAdmissionPort admissionPort,
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
        await DispatchRoomEnvelopeAsync(actorDispatchPort, actor.Id, envelope, ct);

        return Results.Ok(new { status = "joined", agentId });
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

                var signal = await MapAndWriteRoomSessionEventAsync(sessionEnvelope, writer);
                if (signal.HasValue)
                    signalWriter?.TryWrite(signal.Value);
            }
        }
        finally
        {
            signalWriter?.TryComplete();
        }
    }

    private static async ValueTask<StreamingProxyStreamSignal?> MapAndWriteRoomSessionEventAsync(
        StreamingProxyRoomSessionEnvelope sessionEnvelope,
        StreamingProxySseWriter writer)
    {
        // Refactor (iter1/cluster-004):
        //   Old pattern: StreamingProxy endpoint code mapped raw actor EventEnvelope subscriptions.
        //   New principle: endpoint observes typed Projection Pipeline session events and writes SSE frames.
        ArgumentNullException.ThrowIfNull(sessionEnvelope);
        ArgumentNullException.ThrowIfNull(writer);

        var envelope = sessionEnvelope.Envelope;
        if (envelope == null)
            return null;

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

        if (payload.Is(GroupChatMessageEvent.Descriptor))
        {
            var evt = payload.Unpack<GroupChatMessageEvent>();
            await writer.WriteAgentMessageAsync(evt.AgentId, evt.AgentName, evt.Content, 0, CancellationToken.None);
            return StreamingProxyStreamSignal.AgentMessage;
        }

        if (payload.Is(GroupChatParticipantJoinedEvent.Descriptor))
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

    private static bool ShouldWriteToSse(EventEnvelope envelope) =>
        envelope.Route?.IsTopologyPublication() == true ||
        CommittedStateEventEnvelope.TryUnpack(envelope, out _) ||
        TryGetObservedTerminalEvent(envelope, out _);

    private static bool TryGetObservedTerminalEvent(
        EventEnvelope envelope,
        out StreamingProxyChatSessionTerminalStateChanged terminalEvent)
        => StreamingProxyRoomInteractionHelpers.TryGetTerminalEvent(envelope, out terminalEvent);

    private static async Task PublishTerminalStateAsync(
        IActorDispatchPort actorDispatchPort,
        string actorId,
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
                    TargetActorId = actorId,
                },
            },
        };
        await DispatchRoomEnvelopeAsync(actorDispatchPort, actorId, envelope, ct);
    }

    private static Task DispatchRoomEnvelopeAsync(
        IActorDispatchPort actorDispatchPort,
        string actorId,
        EventEnvelope envelope,
        CancellationToken ct)
    {
        // Refactor (iter1/cluster-004):
        //   Old pattern: StreamingProxy endpoints invoked actors inline.
        //   New principle: endpoints publish commands through IActorDispatchPort with runtime-neutral delivery.
        return actorDispatchPort.DispatchAsync(actorId, envelope, ct);
    }

    private static (StreamingProxyChatSessionTerminalStatus Status, string? ErrorMessage) DetermineParticipantTerminalState(
        int successfulReplies) =>
        successfulReplies > 0
            ? (StreamingProxyChatSessionTerminalStatus.Completed, null)
            : (StreamingProxyChatSessionTerminalStatus.Failed, "StreamingProxy chat completed without any participant replies.");

    private static async Task TryPublishCanceledTerminalStateAsync(
        IActorDispatchPort actorDispatchPort,
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
                actorDispatchPort,
                actor.Id,
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
        IActorDispatchPort actorDispatchPort,
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
                actorDispatchPort,
                actor.Id,
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

    private static async Task WaitForClientDisconnectAsync(CancellationToken ct)
    {
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var registration = ct.Register(
            static state => ((TaskCompletionSource)state!).TrySetCanceled(),
            disconnected);
        await disconnected.Task;
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
