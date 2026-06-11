using System.Net.WebSockets;
using System.Text;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Aevatar.Foundation.VoicePresence.Abstractions.Sessions;
using Aevatar.Foundation.VoicePresence.Hosting;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace Aevatar.Foundation.VoicePresence.Tests;

public class VoicePresenceEndpointsTests
{
    [Fact]
    public void MapVoicePresenceWebSocket_should_register_expected_route()
    {
        using var app = CreateApp(new RecordingRealtimeSession(VoiceRealtimeSessionStartError.NotFound));

        var route = GetVoiceEndpoint(app);

        route.RoutePattern.RawText.ShouldBe("/voice/{actorId}");
    }

    [Fact]
    public async Task Request_should_resolve_session_from_registered_service()
    {
        var session = new RecordingRealtimeSession();
        var socket = new FakeWebSocket(WebSocketState.Open, keepOpenUntilCancelledWhenEmpty: true);
        using var app = CreateApp(session);
        var context = CreateHttpContext(app);
        context.Features.Set<IHttpWebSocketFeature>(new FakeHttpWebSocketFeature(socket));
        context.Request.RouteValues["actorId"] = "agent-1";

        socket.CompleteReceiveClose();
        await GetVoiceEndpoint(app).RequestDelegate!(context);

        session.RequestedActorIds.ShouldContain("agent-1");
        session.Requests.ShouldContain(static request => string.Equals(request.ModuleName, null, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Request_should_pass_module_query_to_registered_service_resolver()
    {
        var session = new RecordingRealtimeSession();
        var socket = new FakeWebSocket(WebSocketState.Open, keepOpenUntilCancelledWhenEmpty: true);
        using var app = CreateApp(session);
        var context = CreateHttpContext(app);
        context.Features.Set<IHttpWebSocketFeature>(new FakeHttpWebSocketFeature(socket));
        context.Request.RouteValues["actorId"] = "agent-1";
        context.Request.QueryString = new QueryString("?module=voice_presence_openai");

        socket.CompleteReceiveClose();
        await GetVoiceEndpoint(app).RequestDelegate!(context);

        session.Requests.ShouldContain(request =>
            string.Equals(request.ActorId, "agent-1", StringComparison.Ordinal) &&
            string.Equals(request.ModuleName, "voice_presence_openai", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Request_should_reject_non_websocket_requests()
    {
        var session = new RecordingRealtimeSession(VoiceRealtimeSessionStartError.NotFound);
        using var app = CreateApp(session);
        var context = CreateHttpContext(app);

        await GetVoiceEndpoint(app).RequestDelegate!(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        (await ReadBodyAsync(context)).ShouldContain("WebSocket required.");
        session.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Request_should_reject_missing_actor_id()
    {
        using var app = CreateApp(new RecordingRealtimeSession(VoiceRealtimeSessionStartError.NotFound));
        var context = CreateHttpContext(app);
        context.Features.Set<IHttpWebSocketFeature>(new FakeHttpWebSocketFeature(new FakeWebSocket(WebSocketState.Open)));

        await GetVoiceEndpoint(app).RequestDelegate!(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        (await ReadBodyAsync(context)).ShouldContain("actorId is required.");
    }

    [Fact]
    public async Task Request_should_return_not_found_when_session_missing()
    {
        using var app = CreateApp(new RecordingRealtimeSession(VoiceRealtimeSessionStartError.NotFound));
        var context = CreateHttpContext(app);
        context.Features.Set<IHttpWebSocketFeature>(new FakeHttpWebSocketFeature(new FakeWebSocket(WebSocketState.Open)));
        context.Request.RouteValues["actorId"] = "agent-1";

        await GetVoiceEndpoint(app).RequestDelegate!(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status404NotFound);
        (await ReadBodyAsync(context)).ShouldContain("Voice session not found");
    }

    [Fact]
    public async Task Request_should_return_service_unavailable_when_module_not_initialized()
    {
        using var app = CreateApp(new RecordingRealtimeSession(VoiceRealtimeSessionStartError.NotInitialized));
        var context = CreateHttpContext(app);
        context.Features.Set<IHttpWebSocketFeature>(new FakeHttpWebSocketFeature(new FakeWebSocket(WebSocketState.Open)));
        context.Request.RouteValues["actorId"] = "agent-1";

        await GetVoiceEndpoint(app).RequestDelegate!(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status503ServiceUnavailable);
        (await ReadBodyAsync(context)).ShouldContain("Voice module not initialized.");
    }

    [Fact]
    public async Task Request_should_attach_transport_and_cleanup_when_request_ends()
    {
        var socket = new FakeWebSocket(WebSocketState.Open, keepOpenUntilCancelledWhenEmpty: true);
        var mediaPort = new RecordingVolatileMediaStreamPort(
            attachAsync: async (transport, ct) =>
            {
                await using (transport)
                {
                    await foreach (var _ in transport.ReceiveFramesAsync(ct))
                    {
                    }
                }
            });
        using var app = CreateApp(new RecordingRealtimeSession(), mediaPort);
        var context = CreateHttpContext(app);
        context.Features.Set<IHttpWebSocketFeature>(new FakeHttpWebSocketFeature(socket));
        context.Request.RouteValues["actorId"] = "agent-1";

        socket.CompleteReceiveClose();
        await GetVoiceEndpoint(app).RequestDelegate!(context);

        mediaPort.AttachCalls.ShouldBe(1);
        mediaPort.DetachCalls.ShouldBe(1);
        socket.State.ShouldBe(WebSocketState.Closed);
    }

    [Fact]
    public async Task Request_should_accept_input_image_text_frame_at_host_boundary()
    {
        var receivedFrames = new List<VoiceTransportFrame>();
        var socket = new FakeWebSocket(WebSocketState.Open);
        socket.EnqueueReceive(
            WebSocketMessageType.Text,
            Encoding.UTF8.GetBytes(JsonFormatter.Default.Format(new VoiceControlFrame
            {
                InputImage = new VoiceInputImage
                {
                    MediaType = "image/jpeg",
                    Data = ByteString.CopyFrom([7, 8, 9]),
                },
            })));
        var mediaPort = new RecordingVolatileMediaStreamPort(
            attachAsync: async (transport, ct) =>
            {
                await using (transport)
                {
                    await foreach (var frame in transport.ReceiveFramesAsync(ct))
                        receivedFrames.Add(frame);
                }
            });
        using var app = CreateApp(new RecordingRealtimeSession(), mediaPort);
        var context = CreateHttpContext(app);
        context.Features.Set<IHttpWebSocketFeature>(new FakeHttpWebSocketFeature(socket));
        context.Request.RouteValues["actorId"] = "agent-1";

        await GetVoiceEndpoint(app).RequestDelegate!(context);

        mediaPort.AttachCalls.ShouldBe(1);
        mediaPort.DetachCalls.ShouldBe(1);
        var frame = receivedFrames.ShouldHaveSingleItem();
        frame.InputImage.ShouldNotBeNull();
        frame.InputImage!.MediaType.ShouldBe("image/jpeg");
        frame.InputImage.Data.ToByteArray().ShouldBe(new byte[] { 7, 8, 9 });
    }

    [Fact]
    public async Task Request_should_detach_using_transport_lifetime_lease_id_when_attach_returns_distinct_lease()
    {
        var socket = new FakeWebSocket(WebSocketState.Open, keepOpenUntilCancelledWhenEmpty: true);
        var mediaPort = new RecordingVolatileMediaStreamPort(transportLeaseId: "transport-attached");
        using var app = CreateApp(new RecordingRealtimeSession(), mediaPort);
        var context = CreateHttpContext(app);
        context.Features.Set<IHttpWebSocketFeature>(new FakeHttpWebSocketFeature(socket));
        context.Request.RouteValues["actorId"] = "agent-1";

        socket.CompleteReceiveClose();
        await GetVoiceEndpoint(app).RequestDelegate!(context);

        mediaPort.AttachCalls.ShouldBe(1);
        mediaPort.DetachCalls.ShouldBe(1);
        mediaPort.LastDetachedHandle.ShouldNotBeNull();
        mediaPort.LastDetachedHandle!.ActiveTransportLeaseId.ShouldBe("transport-attached");
        mediaPort.LastDetachedHandle.ActiveTransportLeaseId.ShouldNotBe(CreateLeaseHandle().ActiveTransportLeaseId);
    }

    [Fact]
    public async Task Request_should_reject_second_transport_without_detaching_existing_one()
    {
        var socket = new FakeWebSocket(WebSocketState.Open);
        using var app = CreateApp(new RecordingRealtimeSession(VoiceRealtimeSessionStartError.TransportAlreadyAttached));
        var context = CreateHttpContext(app);
        context.Features.Set<IHttpWebSocketFeature>(new FakeHttpWebSocketFeature(socket));
        context.Request.RouteValues["actorId"] = "agent-1";

        await GetVoiceEndpoint(app).RequestDelegate!(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status409Conflict);
        (await ReadBodyAsync(context)).ShouldContain("Voice transport already attached.");
        socket.CloseCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Request_should_close_websocket_when_attach_fails_after_upgrade()
    {
        var socket = new FakeWebSocket(WebSocketState.Open);
        var mediaPort = new RecordingVolatileMediaStreamPort(
            attachAsync: static (_, _) => throw new InvalidOperationException("already attached"));
        using var app = CreateApp(new RecordingRealtimeSession(), mediaPort);
        var context = CreateHttpContext(app);
        context.Features.Set<IHttpWebSocketFeature>(new FakeHttpWebSocketFeature(socket));
        context.Request.RouteValues["actorId"] = "agent-1";

        await GetVoiceEndpoint(app).RequestDelegate!(context);

        socket.CloseCalls.ShouldBe(1);
        socket.State.ShouldBe(WebSocketState.Closed);
    }

    [Fact]
    public async Task Request_should_close_websocket_with_policy_violation_when_remote_audio_is_unavailable()
    {
        var socket = new RecordingCloseWebSocket(WebSocketState.Open);
        var mediaPort = new RecordingVolatileMediaStreamPort(
            attachAsync: static (_, _) => throw new VoiceVolatileMediaStreamUnavailableException());
        using var app = CreateApp(new RecordingRealtimeSession(), mediaPort);
        var context = CreateHttpContext(app);
        context.Features.Set<IHttpWebSocketFeature>(new RecordingHttpWebSocketFeature(socket));
        context.Request.RouteValues["actorId"] = "agent-1";

        await GetVoiceEndpoint(app).RequestDelegate!(context);

        socket.CloseCalls.ShouldBe(1);
        socket.LastCloseStatus.ShouldBe(WebSocketCloseStatus.PolicyViolation);
        socket.LastCloseDescription.ShouldBe(VoiceVolatileMediaStreamUnavailableException.Reason);
        socket.State.ShouldBe(WebSocketState.Closed);
    }

    [Fact]
    public async Task Request_should_prefer_route_module_name_over_query()
    {
        var session = new RecordingRealtimeSession();
        var socket = new FakeWebSocket(WebSocketState.Open, keepOpenUntilCancelledWhenEmpty: true);
        using var app = CreateApp("/voice/{actorId}/{moduleName}", session);
        var context = CreateHttpContext(app);
        context.Features.Set<IHttpWebSocketFeature>(new FakeHttpWebSocketFeature(socket));
        context.Request.RouteValues["actorId"] = "agent-1";
        context.Request.RouteValues["moduleName"] = "voice_presence_openai";
        context.Request.QueryString = new QueryString("?module=voice_presence_minicpm");

        socket.CompleteReceiveClose();
        await GetVoiceEndpoint(app, "/voice/{actorId}/{moduleName}").RequestDelegate!(context);

        session.Requests.ShouldContain(request =>
            string.Equals(request.ModuleName, "voice_presence_openai", StringComparison.Ordinal));
    }

    private static WebApplication CreateApp(
        RecordingRealtimeSession session,
        RecordingVolatileMediaStreamPort? mediaPort = null) =>
        CreateApp("/voice/{actorId}", session, mediaPort);

    private static WebApplication CreateApp(
        string pattern,
        RecordingRealtimeSession session,
        RecordingVolatileMediaStreamPort? mediaPort = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.Services.AddSingleton<IRealtimeSession<VoiceRealtimeSessionRequest, VoiceRealtimeSessionAccepted, VoiceRealtimeSessionStartError, VoiceRealtimeFrame, VoiceRealtimeSessionCompletion>>(session);
        builder.Services.AddSingleton<IVoiceVolatileMediaStreamPort>(mediaPort ?? new RecordingVolatileMediaStreamPort());
        var app = builder.Build();
        app.MapVoicePresenceWebSocket(pattern);
        return app;
    }

    private static RouteEndpoint GetVoiceEndpoint(WebApplication app, string pattern = "/voice/{actorId}") =>
        ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(x => x.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(x => string.Equals(x.RoutePattern.RawText, pattern, StringComparison.Ordinal));

    private static DefaultHttpContext CreateHttpContext(WebApplication app)
    {
        return new DefaultHttpContext
        {
            RequestServices = app.Services,
            Response =
            {
                Body = new MemoryStream(),
            },
        };
    }

    private static async Task<string> ReadBodyAsync(DefaultHttpContext context)
    {
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }

    private static VoicePresenceSessionLeaseHandle CreateLeaseHandle(string sessionId = "session-1") =>
        new(
            "agent-1",
            "voice_presence",
            sessionId,
            "voice-presence.host",
            42,
            DateTimeOffset.UtcNow.AddMinutes(5),
            VoiceRemoteAudioSupport.Supported,
            "transport-1");

    private sealed class RecordingRealtimeSession(
        VoiceRealtimeSessionStartError? failure = null)
        : IRealtimeSession<VoiceRealtimeSessionRequest, VoiceRealtimeSessionAccepted, VoiceRealtimeSessionStartError, VoiceRealtimeFrame, VoiceRealtimeSessionCompletion>
    {
        public List<VoiceRealtimeSessionRequest> Requests { get; } = [];

        public List<string> RequestedActorIds { get; } = [];

        public Task<RealtimeSessionResult<VoiceRealtimeSessionAccepted, VoiceRealtimeSessionStartError, VoiceRealtimeSessionCompletion>> ExecuteAsync(
            VoiceRealtimeSessionRequest inbound,
            Func<VoiceRealtimeFrame, CancellationToken, ValueTask> emitAsync,
            Func<VoiceRealtimeSessionAccepted, CancellationToken, ValueTask>? onAcceptedAsync = null,
            CancellationToken ct = default)
        {
            _ = emitAsync;
            ct.ThrowIfCancellationRequested();
            Requests.Add(inbound);
            RequestedActorIds.Add(inbound.ActorId);

            if (failure.HasValue)
            {
                return Task.FromResult(
                    RealtimeSessionResult<VoiceRealtimeSessionAccepted, VoiceRealtimeSessionStartError, VoiceRealtimeSessionCompletion>
                        .Failure(failure.Value));
            }

            var accepted = new VoiceRealtimeSessionAccepted(
                inbound.ActorId,
                inbound.ModuleName ?? "voice_presence",
                "session-1",
                24000,
                42,
                CreateLeaseHandle());
            return Task.FromResult(
                RealtimeSessionResult<VoiceRealtimeSessionAccepted, VoiceRealtimeSessionStartError, VoiceRealtimeSessionCompletion>
                    .Success(accepted, VoiceRealtimeSessionCompletion.Accepted, completed: true));
        }
    }

    private sealed class RecordingVolatileMediaStreamPort(
        Func<IVoiceTransport, CancellationToken, Task>? attachAsync = null,
        string transportLeaseId = "transport-1")
        : IVoiceVolatileMediaStreamPort
    {
        public bool SupportsRemoteAudio => true;

        public int AttachCalls { get; private set; }

        public int DetachCalls { get; private set; }

        public int LifetimeCompletionCalls { get; private set; }

        public VoicePresenceSessionLeaseHandle? LastDetachedHandle { get; private set; }

        public async Task<VoiceTransportLifetimeCompleted?> AttachAsync(
            VoicePresenceSessionLeaseHandle handle,
            IVoiceTransport transport,
            CancellationToken ct = default)
        {
            AttachCalls++;
            if (attachAsync != null)
            {
                await attachAsync(transport, ct);
            }
            else
            {
                await using (transport)
                {
                    await foreach (var _ in transport.ReceiveFramesAsync(ct))
                    {
                    }
                }
            }

            return new VoiceTransportLifetimeCompleted
            {
                SessionId = handle.SessionId,
                TransportLeaseId = transportLeaseId,
                Reason = "completed",
                OwnerId = handle.OwnerId,
            };
        }

        public Task DetachAsync(
            VoicePresenceSessionLeaseHandle handle,
            IVoiceTransport? expectedTransport,
            CancellationToken ct = default)
        {
            _ = expectedTransport;
            ct.ThrowIfCancellationRequested();
            DetachCalls++;
            LastDetachedHandle = handle;
            return Task.CompletedTask;
        }

        public Task CompleteTransportLifetimeAsync(
            VoicePresenceSessionLeaseHandle handle,
            VoiceTransportLifetimeCompleted? completed,
            string reason,
            CancellationToken ct = default)
        {
            _ = handle;
            _ = completed;
            _ = reason;
            ct.ThrowIfCancellationRequested();
            LifetimeCompletionCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingCloseWebSocket : WebSocket
    {
        private WebSocketState _state;

        public RecordingCloseWebSocket(WebSocketState state)
        {
            _state = state;
        }

        public int CloseCalls { get; private set; }

        public WebSocketCloseStatus? LastCloseStatus { get; private set; }

        public string? LastCloseDescription { get; private set; }

        public override WebSocketCloseStatus? CloseStatus => LastCloseStatus;

        public override string? CloseStatusDescription => LastCloseDescription;

        public override WebSocketState State => _state;

        public override string? SubProtocol => null;

        public override void Abort()
        {
            _state = WebSocketState.Aborted;
        }

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CloseCalls++;
            LastCloseStatus = closeStatus;
            LastCloseDescription = statusDescription;
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastCloseStatus = closeStatus;
            LastCloseDescription = statusDescription;
            _state = WebSocketState.CloseSent;
            return Task.CompletedTask;
        }

        public override void Dispose()
        {
            _state = WebSocketState.Closed;
        }

        public override Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer,
            CancellationToken cancellationToken)
        {
            _ = buffer;
            cancellationToken.ThrowIfCancellationRequested();
            _state = WebSocketState.CloseReceived;
            return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
        }

        public override Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            _ = buffer;
            _ = messageType;
            _ = endOfMessage;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingHttpWebSocketFeature(RecordingCloseWebSocket socket) : IHttpWebSocketFeature
    {
        public bool IsWebSocketRequest => true;

        public Task<WebSocket> AcceptAsync(WebSocketAcceptContext context)
        {
            _ = context;
            return Task.FromResult<WebSocket>(socket);
        }
    }
}
