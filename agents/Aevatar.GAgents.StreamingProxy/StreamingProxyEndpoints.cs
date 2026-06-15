using Aevatar.AI.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.StreamingProxy.Application.Rooms;
using Aevatar.Capabilities;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Microsoft.Extensions.Logging;
using System.Threading.Channels;

namespace Aevatar.GAgents.StreamingProxy;

public static class StreamingProxyEndpoints
{
    public const string DeprecationHeaderName = "Deprecation";
    public const string SunsetHeaderName = "Sunset";
    public const string LinkHeaderName = "Link";
    public const string DeprecationHeaderValue = "true";
    public const string SunsetHeaderValue = "Wed, 25 Nov 2026 00:00:00 GMT";
    public const string SuccessorRoute = "/v1/responses";
    public const string SuccessorLinkHeaderValue =
        "</v1/responses>; rel=\"successor-version\"; title=\"Migrate direct model streaming to /v1/responses; StreamingProxy room fan-out has no one-to-one replacement\"";

    // Refactor (iter92/cluster-645):
    //   Old pattern: StreamingProxy exposed a public streaming proxy entry point for production consumers.
    //   New principle: StreamingProxy is soft-deprecated; sunset headers point direct model streaming to /v1/responses,
    //   and these routes remain compatibility-only.
    // Refactor (iter38/cluster-038-streaming-proxy-reuse-existing):
    //   Old pattern: endpoints built post-message, join, and terminal-state room envelopes directly in Host code.
    //   New principle: endpoints validate HTTP concerns and delegate typed room commands to the existing room command service.
    public static IEndpointRouteBuilder MapStreamingProxyEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/scopes")
            .WithTags("StreamingProxy")
            .AddEndpointFilter(AddDeprecationHeadersAsync);

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

    private static async ValueTask<object?> AddDeprecationHeadersAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        AddDeprecationHeaders(context.HttpContext.Response);
        return await next(context);
    }

    internal static void AddDeprecationHeaders(HttpResponse response)
    {
        response.Headers[DeprecationHeaderName] = DeprecationHeaderValue;
        response.Headers[SunsetHeaderName] = SunsetHeaderValue;
        response.Headers[LinkHeaderName] = SuccessorLinkHeaderValue;
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
                string.Equals(g.AgentKind, StreamingProxyDefaults.GAgentKind, StringComparison.Ordinal));
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
        [FromServices] IScopeResourceAdmissionPort admissionPort,
        [FromServices] IGAgentActorRegistryCommandPort registryCommandPort,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;

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
                new GAgentActorRegistration(
                    scopeId,
                    StreamingProxyDefaults.GAgentKind,
                    roomId),
                ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger("Aevatar.GAgents.StreamingProxy.Endpoints")
                .LogWarning(ex, "Failed to delete streaming proxy room {RoomId}", roomId);
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
        [FromServices] IScopeResourceAdmissionPort admissionPort,
        [FromServices] ICommandInteractionService<StreamingProxyRoomChatCommand, StreamingProxyRoomChatAcceptedReceipt, StreamingProxyRoomChatStartError, StreamingProxyRoomSessionEnvelope, StreamingProxyProjectionCompletionStatus> interactionService,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Aevatar.GAgents.StreamingProxy.Endpoints");
        var writer = new StreamingProxySseWriter(http.Response);
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

            // Set up SSE response
            await writer.StartAsync(ct);

            var accessToken = ExtractBearerToken(http);
            var preferredRoute = request.LlmRoute?.Trim();
            var defaultModel = request.LlmModel?.Trim();
            var result = await interactionService.ExecuteAsync(
                new StreamingProxyRoomChatCommand(
                    roomId,
                    scopeId,
                    prompt,
                    sessionId,
                    accessToken,
                    preferredRoute,
                    defaultModel),
                async (frame, _) =>
                {
                    await MapAndWriteRoomSessionEventAsync(frame, writer);
                },
                ct: ct);

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
        [FromServices] IStreamingProxyRoomCommandService roomCommandService,
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

        var result = await roomCommandService.PostMessageAsync(
            new StreamingProxyRoomPostMessageCommand(
                roomId,
                request.AgentId,
                request.AgentName,
                request.Content,
                request.SessionId),
            ct);
        if (result.Status == StreamingProxyRoomPostMessageStatus.RoomNotFound)
            return Results.NotFound(new { error = "Room not found" });

        // Refactor (iter56/cluster-891-endpoint-ack-honesty): old=200-shaped accepted, new=202 + Location
        //   Message dispatch only enters the room actor inbox; committed message/projection visibility arrives later.
        //   The room stream is the observation resource for clients that need applied message state.
        var streamUrl = $"/api/scopes/{Uri.EscapeDataString(scopeId)}/streaming-proxy/rooms/{Uri.EscapeDataString(roomId)}/messages:stream";
        return Results.Accepted(streamUrl, new
        {
            status = "accepted",
            statusUrl = streamUrl,
        });
    }

    // ─── OpenClaw subscribes to message stream (SSE) ───

    private static async Task HandleMessageStreamAsync(
        HttpContext http,
        string scopeId,
        string roomId,
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

            var eventChannel = new EventChannel<StreamingProxyRoomSessionEnvelope>();
            var attachment = await subscriptionObservationPort.AttachAsync(roomId, eventChannel, ct);
            if (attachment == null)
            {
                await eventChannel.DisposeAsync();
                http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }

            Task? pumpTask = null;

            try
            {
                await writer.StartAsync(ct);
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
        [FromServices] IStreamingProxyRoomParticipantsQueryPort participantsQueryPort,
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
            var snapshot = await participantsQueryPort.GetAsync(roomId, ct);
            return Results.Ok(snapshot?.Participants.Select(participant => new ParticipantResponse(
                participant.AgentId,
                participant.DisplayName,
                participant.JoinedAt?.ToDateTimeOffset() ?? DateTimeOffset.MinValue)) ?? []);
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
        [FromServices] IScopeResourceAdmissionPort admissionPort,
        [FromServices] IStreamingProxyRoomCommandService roomCommandService,
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

        var result = await roomCommandService.JoinAsync(
            new StreamingProxyRoomJoinCommand(roomId, request.AgentId, request.DisplayName),
            ct);
        if (result.Status == StreamingProxyRoomJoinStatus.RoomNotFound)
            return Results.NotFound(new { error = "Room not found" });

        var agentId = result.AgentId ?? request.AgentId.Trim();

        // Refactor (iter56/cluster-891-endpoint-ack-honesty): old=200-shaped accepted, new=202 + Location
        //   Join dispatch only enters the room actor inbox; participant projection visibility arrives later.
        //   Clients must observe/poll the participants resource instead of treating this as committed membership.
        var participantsUrl = $"/api/scopes/{Uri.EscapeDataString(scopeId)}/streaming-proxy/rooms/{Uri.EscapeDataString(roomId)}/participants";
        return Results.Accepted(participantsUrl, new
        {
            status = "accepted",
            agentId,
            statusUrl = participantsUrl,
        });
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
                StreamingProxyDefaults.GAgentKind,
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
    private sealed record ParticipantResponse(string AgentId, string DisplayName, DateTimeOffset JoinedAt);

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
