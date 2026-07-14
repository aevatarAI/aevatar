using System.Net.WebSockets;
using System.Text;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Aevatar.Foundation.VoicePresence.Abstractions.Sessions;
using Aevatar.Foundation.VoicePresence.Hosting;
using Aevatar.Foundation.VoicePresence.Transport;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
    public async Task Request_should_close_websocket_when_transport_conflicts_after_upgrade()
    {
        var socket = new RecordingCloseWebSocket(WebSocketState.Open);
        var mediaPort = new RecordingVolatileMediaStreamPort(
            attachAsync: static (_, _) => throw new VoiceTransportAlreadyAttachedException());
        using var app = CreateApp(new RecordingRealtimeSession(), mediaPort);
        var context = CreateHttpContext(app);
        context.Features.Set<IHttpWebSocketFeature>(new RecordingHttpWebSocketFeature(socket));
        context.Request.RouteValues["actorId"] = "agent-1";

        await GetVoiceEndpoint(app).RequestDelegate!(context);

        socket.CloseCalls.ShouldBe(1);
        socket.LastCloseStatus.ShouldBe(WebSocketCloseStatus.PolicyViolation);
        socket.LastCloseDescription.ShouldBe(VoiceTransportAlreadyAttachedException.Reason);
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
    public async Task Request_should_close_websocket_with_typed_reason_when_provider_credential_is_unavailable()
    {
        var socket = new RecordingCloseWebSocket(WebSocketState.Open);
        var mediaPort = new RecordingVolatileMediaStreamPort(
            attachAsync: static (_, _) => throw new RealtimeProviderCredentialException("broker response omitted secret"));
        using var app = CreateApp(new RecordingRealtimeSession(), mediaPort);
        var context = CreateHttpContext(app);
        context.Features.Set<IHttpWebSocketFeature>(new RecordingHttpWebSocketFeature(socket));
        context.Request.RouteValues["actorId"] = "agent-1";

        await GetVoiceEndpoint(app).RequestDelegate!(context);

        socket.CloseCalls.ShouldBe(1);
        socket.LastCloseStatus.ShouldBe(WebSocketCloseStatus.PolicyViolation);
        socket.LastCloseDescription.ShouldBe(VoiceWebSocketAttachExecutor.VoiceProviderCredentialUnavailableReason);
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

    [Fact]
    public async Task Request_should_forward_realtime_frames_to_websocket_control_channel()
    {
        var responseId = 37;
        var hub = new RecordingProjectionSessionEventHub();
        var receivedControls = new List<VoiceControlFrame>();
        var socket = new FakeWebSocket(WebSocketState.Open);
        socket.EnqueueReceive(
            WebSocketMessageType.Text,
            Encoding.UTF8.GetBytes(JsonFormatter.Default.Format(new VoiceControlFrame
            {
                DrainAcknowledged = new VoiceDrainAcknowledged
                {
                    ResponseId = responseId,
                    PlayoutSequence = 9,
                },
            })));
        var mediaPort = new RecordingVolatileMediaStreamPort(
            attachAsync: async (transport, ct) =>
            {
                await hub.PublishAsync(
                    "agent-1",
                    "session-1",
                    new VoiceRealtimeFrame
                    {
                        ModuleName = "voice_presence",
                        SessionId = "session-1",
                        ResponseStarted = new VoiceResponseStarted
                        {
                            ResponseId = responseId,
                            ProviderResponseId = "provider-response-37",
                        },
                    },
                    ct);

                await using (transport)
                {
                    await foreach (var frame in transport.ReceiveFramesAsync(ct))
                    {
                        if (frame.Control != null)
                            receivedControls.Add(frame.Control.Clone());
                    }
                }
            });
        using var app = CreateApp(new RecordingRealtimeSession(), mediaPort, hub);
        var context = CreateHttpContext(app);
        context.Features.Set<IHttpWebSocketFeature>(new FakeHttpWebSocketFeature(socket));
        context.Request.RouteValues["actorId"] = "agent-1";

        await GetVoiceEndpoint(app).RequestDelegate!(context);

        socket.SentTexts.Count.ShouldBe(2);
        var accepted = JsonParser.Default.Parse<VoiceControlFrame>(socket.SentTexts[0]);
        accepted.FrameCase.ShouldBe(VoiceControlFrame.FrameOneofCase.SessionAccepted);
        accepted.SessionAccepted.SessionId.ShouldBe("session-1");
        accepted.SessionAccepted.PcmSampleRateHz.ShouldBe(24000);
        accepted.SessionAccepted.WireContractVersion.ShouldBe(VoiceWireContractDefaults.CurrentWireContractVersion);
        accepted.SessionAccepted.InputImagePolicy.MaxBytes.ShouldBe(VoiceWireContractDefaults.MaxInputImageBytes);
        accepted.SessionAccepted.InputImagePolicy.AllowedMediaTypes
            .ShouldBe(VoiceWireContractDefaults.SupportedInputImageMediaTypes);
        accepted.SessionAccepted.AttachOutcome.ShouldBe(VoiceTransportAttachOutcome.NewSession);

        var realtime = JsonParser.Default.Parse<VoiceControlFrame>(socket.SentTexts[1]);
        realtime.FrameCase.ShouldBe(VoiceControlFrame.FrameOneofCase.RealtimeFrame);
        realtime.RealtimeFrame.SessionId.ShouldBe("session-1");
        realtime.RealtimeFrame.ResponseStarted.ResponseId.ShouldBe(responseId);
        realtime.RealtimeFrame.ResponseStarted.ProviderResponseId.ShouldBe("provider-response-37");

        var ack = receivedControls.ShouldHaveSingleItem();
        ack.FrameCase.ShouldBe(VoiceControlFrame.FrameOneofCase.DrainAcknowledged);
        ack.DrainAcknowledged.ResponseId.ShouldBe(realtime.RealtimeFrame.ResponseStarted.ResponseId);
        ack.DrainAcknowledged.PlayoutSequence.ShouldBe(9);
        mediaPort.DetachCalls.ShouldBe(1);
    }

    [Fact]
    public async Task Control_bridge_should_map_restart_attach_outcome_to_session_accepted()
    {
        var transport = new RecordingVoiceTransport();
        var accepted = new VoiceRealtimeSessionAccepted(
            "agent-1",
            "voice_presence",
            "session-1",
            24000,
            42,
            CreateLeaseHandle(),
            AttachOutcome: VoiceRealtimeAttachOutcome.Restarted);

        await VoiceRealtimeTransportControlBridge.SendSessionAcceptedAsync(
            transport,
            accepted,
            CancellationToken.None);

        var control = transport.SentControls.ShouldHaveSingleItem();
        control.FrameCase.ShouldBe(VoiceControlFrame.FrameOneofCase.SessionAccepted);
        control.SessionAccepted.AttachOutcome.ShouldBe(VoiceTransportAttachOutcome.Restarted);
    }

    [Fact]
    public async Task Request_should_return_retry_after_when_transport_is_already_attached()
    {
        var socket = new FakeWebSocket(WebSocketState.Open);
        using var app = CreateApp(new RecordingRealtimeSession(VoiceRealtimeSessionStartError.TransportAlreadyAttached));
        var context = CreateHttpContext(app);
        context.Features.Set<IHttpWebSocketFeature>(new FakeHttpWebSocketFeature(socket));
        context.Request.RouteValues["actorId"] = "agent-1";

        await GetVoiceEndpoint(app).RequestDelegate!(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status409Conflict);
        context.Response.Headers.RetryAfter.ToString().ShouldBe("1");
        (await ReadBodyAsync(context)).ShouldContain("Voice transport already attached.");
        socket.CloseCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Executor_should_close_websocket_when_attach_times_out()
    {
        var timeProvider = new ControllableTimeProvider(DateTimeOffset.Parse("2026-06-19T00:00:00Z"));
        var executor = CreateExecutor(
            timeProvider,
            options =>
            {
                options.AttachTimeout = TimeSpan.FromSeconds(5);
                options.CloseWaitTimeout = TimeSpan.FromSeconds(60);
            });
        var socket = new FakeWebSocket(WebSocketState.Open, keepOpenUntilCancelledWhenEmpty: true);
        var mediaPort = new TimeoutAttachMediaStreamPort(timeProvider, TimeSpan.FromSeconds(5));
        using var app = CreateApp(new RecordingRealtimeSession(), mediaPort);
        var context = CreateHttpContext(app);
        context.Features.Set<IHttpWebSocketFeature>(new FakeHttpWebSocketFeature(socket));
        var accepted = new VoiceRealtimeSessionAccepted(
            "agent-1",
            "voice_presence",
            "session-1",
            24000,
            42,
            CreateLeaseHandle());

        await executor.ExecuteAsync(context, accepted, mediaPort);

        mediaPort.AttachCanceled.ShouldBeTrue();
        socket.CloseCalls.ShouldBe(1);
        socket.LastCloseStatus.ShouldBe(WebSocketCloseStatus.PolicyViolation);
        socket.LastCloseDescription.ShouldBe("Voice transport attach timed out.");
        mediaPort.DetachCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Executor_should_detach_when_close_wait_times_out()
    {
        var timeProvider = new ControllableTimeProvider(DateTimeOffset.Parse("2026-06-19T00:00:00Z"));
        var executor = CreateExecutor(
            timeProvider,
            options =>
            {
                options.AttachTimeout = TimeSpan.FromSeconds(60);
                options.CloseWaitTimeout = TimeSpan.FromSeconds(5);
            });
        var socket = new FakeWebSocket(WebSocketState.Open, keepOpenUntilCancelledWhenEmpty: true);
        var mediaPort = new CloseWaitTimeoutMediaStreamPort();
        using var app = CreateApp(new RecordingRealtimeSession(), mediaPort);
        var context = CreateHttpContext(app);
        context.Features.Set<IHttpWebSocketFeature>(new FakeHttpWebSocketFeature(socket));
        var accepted = new VoiceRealtimeSessionAccepted(
            "agent-1",
            "voice_presence",
            "session-1",
            24000,
            42,
            CreateLeaseHandle());

        var executeTask = executor.ExecuteAsync(context, accepted, mediaPort);
        await mediaPort.WaitForAttachReturnAsync();
        timeProvider.Advance(TimeSpan.FromSeconds(5));
        await executeTask;

        mediaPort.AttachCalls.ShouldBe(1);
        mediaPort.DetachCalls.ShouldBe(1);
    }

    [Fact]
    public async Task WhipAttachExecutor_should_return_answer_and_attach_transport()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new RecordingVoiceTransport();
        var factory = new RecordingWebRtcVoiceTransportFactory(transport, "answer-sdp", completion.Task);
        var mediaPort = new NonDisposingRecordingVolatileMediaStreamPort();
        var executor = new VoiceWhipAttachExecutor(mediaPort, factory);
        await using var app = CreateApp(new RecordingRealtimeSession(), mediaPort);
        var context = CreateHttpContext(app);
        var accepted = CreateAccepted();

        var result = await executor.AttachAsync(
            context,
            accepted,
            "offer-sdp",
            "/voice/agent-1/whip/resource",
            new VoiceToolCredentialTransportBinding(
                "voice-tool:issued-1",
                "caller-jwt",
                DateTimeOffset.Parse("2026-06-30T00:00:00Z")));

        result.AnswerSdp.ShouldBe("answer-sdp");
        result.ResourceLocation.ShouldBe("/voice/agent-1/whip/resource");
        factory.RemoteOfferSdps.ShouldBe(["offer-sdp"]);
        factory.LastOptions.ShouldNotBeNull();
        factory.LastOptions!.PcmSampleRateHz.ShouldBe(24000);
        factory.LastOptions.ControlDataChannelLabel.ShouldBe("vp-control");
        mediaPort.AttachCalls.ShouldBe(1);
        mediaPort.LastToolCredentialBinding.ShouldNotBeNull();
        mediaPort.LastToolCredentialBinding!.CredentialRef.ShouldBe("voice-tool:issued-1");
        mediaPort.LastToolCredentialBinding.NyxIdAccessToken.ShouldBe("caller-jwt");

        completion.SetResult();
        await mediaPort.WaitForLifetimeCompletionAsync();
        mediaPort.LifetimeCompletionCalls.ShouldBe(1);
        transport.DisposeCalls.ShouldBe(0);
    }

    [Fact]
    public async Task WhipAttachExecutor_should_dispose_transport_when_attach_conflicts_before_attach()
    {
        var transport = new RecordingVoiceTransport();
        var factory = new RecordingWebRtcVoiceTransportFactory(
            transport,
            "answer-sdp",
            Task.CompletedTask);
        var mediaPort = new NonDisposingRecordingVolatileMediaStreamPort
        {
            AttachException = new VoiceTransportAlreadyAttachedException(),
        };
        var executor = new VoiceWhipAttachExecutor(mediaPort, factory);
        await using var app = CreateApp(new RecordingRealtimeSession(), mediaPort);
        var context = CreateHttpContext(app);

        var act = () => executor.AttachAsync(context, CreateAccepted(), "offer-sdp", "/resource");

        await act.ShouldThrowAsync<VoiceWhipTransportAttachConflictException>();
        transport.DisposeCalls.ShouldBe(1);
        mediaPort.LifetimeCompletionCalls.ShouldBe(0);
    }

    [Fact]
    public async Task WhipAttachExecutor_should_dispose_transport_when_factory_or_attach_fails()
    {
        var transport = new RecordingVoiceTransport();
        var factory = new RecordingWebRtcVoiceTransportFactory(
            transport,
            "answer-sdp",
            Task.CompletedTask);
        var mediaPort = new NonDisposingRecordingVolatileMediaStreamPort
        {
            AttachException = new VoiceVolatileMediaStreamUnavailableException(),
        };
        var executor = new VoiceWhipAttachExecutor(mediaPort, factory);
        await using var app = CreateApp(new RecordingRealtimeSession(), mediaPort);
        var context = CreateHttpContext(app);

        var act = () => executor.AttachAsync(context, CreateAccepted(), "offer-sdp", "/resource");

        await act.ShouldThrowAsync<VoiceVolatileMediaStreamUnavailableException>();
        transport.DisposeCalls.ShouldBe(1);
        mediaPort.LifetimeCompletionCalls.ShouldBe(0);
    }

    private static WebApplication CreateApp(
        RecordingRealtimeSession session,
        IVoiceVolatileMediaStreamPort? mediaPort = null,
        IProjectionSessionEventHub<VoiceRealtimeFrame>? realtimeHub = null) =>
        CreateApp("/voice/{actorId}", session, mediaPort, realtimeHub);

    private static WebApplication CreateApp(
        string pattern,
        RecordingRealtimeSession session,
        IVoiceVolatileMediaStreamPort? mediaPort = null,
        IProjectionSessionEventHub<VoiceRealtimeFrame>? realtimeHub = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.Services.AddSingleton<IRealtimeSession<VoiceRealtimeSessionRequest, VoiceRealtimeSessionAccepted, VoiceRealtimeSessionStartError, VoiceRealtimeFrame, VoiceRealtimeSessionCompletion>>(session);
        builder.Services.AddSingleton<IVoiceVolatileMediaStreamPort>(mediaPort ?? new RecordingVolatileMediaStreamPort());
        builder.Services.AddOptions<VoiceWebSocketAttachOptions>();
        builder.Services.AddSingleton<IValidateOptions<VoiceWebSocketAttachOptions>, VoiceWebSocketAttachOptionsValidator>();
        builder.Services.AddSingleton<VoiceWebSocketAttachExecutor>();
        if (realtimeHub != null)
            builder.Services.AddSingleton(realtimeHub);
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

    private static VoiceRealtimeSessionAccepted CreateAccepted() =>
        new(
            "agent-1",
            "voice_presence",
            "session-1",
            24000,
            42,
            CreateLeaseHandle());

    private static VoiceWebSocketAttachExecutor CreateExecutor(
        TimeProvider timeProvider,
        Action<VoiceWebSocketAttachOptions> configure)
    {
        var options = new VoiceWebSocketAttachOptions();
        configure(options);
        return new VoiceWebSocketAttachExecutor(
            Options.Create(options),
            LoggerFactory.Create(static _ => { }).CreateLogger<VoiceWebSocketAttachExecutor>(),
            timeProvider);
    }

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

    private class RecordingVolatileMediaStreamPort(
        Func<IVoiceTransport, CancellationToken, Task>? attachAsync = null,
        string transportLeaseId = "transport-1")
        : IVoiceVolatileMediaStreamPort
    {
        public bool SupportsRemoteAudio => true;

        public Task<bool> TryCancelResponseAsync(
            string transportLeaseId,
            CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<bool> TrySendInputImageAsync(
            string transportLeaseId,
            VoiceInputImage inputImage,
            CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<bool> TrySendToolResultAsync(
            string transportLeaseId,
            string callId,
            string resultJson,
            CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<bool> TryInjectEventAsync(
            string transportLeaseId,
            VoiceConversationEventInjection injection,
            CancellationToken ct = default) =>
            Task.FromResult(false);

        public int AttachCalls { get; private set; }

        public int DetachCalls { get; private set; }

        public int LifetimeCompletionCalls { get; private set; }

        public VoicePresenceSessionLeaseHandle? LastDetachedHandle { get; private set; }

        public VoiceToolCredentialTransportBinding? LastToolCredentialBinding { get; protected set; }

        public virtual async Task<VoiceTransportLifetimeCompleted?> AttachAsync(
            VoicePresenceSessionLeaseHandle handle,
            IVoiceTransport transport,
            CancellationToken ct = default)
            => await AttachAsync(handle, transport, null, ct);

        public virtual async Task<VoiceTransportLifetimeCompleted?> AttachAsync(
            VoicePresenceSessionLeaseHandle handle,
            IVoiceTransport transport,
            VoiceToolCredentialTransportBinding? toolCredentialBinding,
            CancellationToken ct = default)
        {
            LastToolCredentialBinding = toolCredentialBinding;
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

        public virtual Task DetachAsync(
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

        public virtual Task CompleteTransportLifetimeAsync(
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

    private sealed class NonDisposingRecordingVolatileMediaStreamPort()
        : RecordingVolatileMediaStreamPort(attachAsync: static (_, _) => Task.CompletedTask)
    {
        private readonly TaskCompletionSource _lifetimeCompleted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Exception? AttachException { get; init; }

        public override Task<VoiceTransportLifetimeCompleted?> AttachAsync(
            VoicePresenceSessionLeaseHandle handle,
            IVoiceTransport transport,
            VoiceToolCredentialTransportBinding? toolCredentialBinding,
            CancellationToken ct = default)
        {
            if (AttachException != null)
                return Task.FromException<VoiceTransportLifetimeCompleted?>(AttachException);

            return base.AttachAsync(handle, transport, toolCredentialBinding, ct);
        }

        public override async Task CompleteTransportLifetimeAsync(
            VoicePresenceSessionLeaseHandle handle,
            VoiceTransportLifetimeCompleted? completed,
            string reason,
            CancellationToken ct = default)
        {
            await base.CompleteTransportLifetimeAsync(handle, completed, reason, ct);
            _lifetimeCompleted.TrySetResult();
        }

        public Task WaitForLifetimeCompletionAsync() => _lifetimeCompleted.Task;
    }

    private sealed class TimeoutAttachMediaStreamPort(
        ControllableTimeProvider timeProvider,
        TimeSpan timeout) : IVoiceVolatileMediaStreamPort
    {
        public bool SupportsRemoteAudio => true;

        public bool AttachCanceled { get; private set; }

        public int DetachCalls { get; private set; }

        public Task<bool> TryCancelResponseAsync(
            string transportLeaseId,
            CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<bool> TrySendInputImageAsync(
            string transportLeaseId,
            VoiceInputImage inputImage,
            CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<bool> TrySendToolResultAsync(
            string transportLeaseId,
            string callId,
            string resultJson,
            CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<bool> TryInjectEventAsync(
            string transportLeaseId,
            VoiceConversationEventInjection injection,
            CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<VoiceTransportLifetimeCompleted?> AttachAsync(
            VoicePresenceSessionLeaseHandle handle,
            IVoiceTransport transport,
            CancellationToken ct = default) =>
            AttachAsync(handle, transport, null, ct);

        public async Task<VoiceTransportLifetimeCompleted?> AttachAsync(
            VoicePresenceSessionLeaseHandle handle,
            IVoiceTransport transport,
            VoiceToolCredentialTransportBinding? toolCredentialBinding,
            CancellationToken ct = default)
        {
            _ = handle;
            _ = transport;
            _ = toolCredentialBinding;
            try
            {
                var wait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var registration = ct.Register(static state =>
                {
                    ((TaskCompletionSource)state!).TrySetCanceled();
                }, wait);
                timeProvider.Advance(timeout);
                await wait.Task;
                return null;
            }
            catch (OperationCanceledException)
            {
                AttachCanceled = true;
                throw;
            }
        }

        public Task DetachAsync(
            VoicePresenceSessionLeaseHandle handle,
            IVoiceTransport? expectedTransport,
            CancellationToken ct = default)
        {
            _ = handle;
            _ = expectedTransport;
            ct.ThrowIfCancellationRequested();
            DetachCalls++;
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
            return Task.CompletedTask;
        }
    }

    private sealed class CloseWaitTimeoutMediaStreamPort() : RecordingVolatileMediaStreamPort(
            attachAsync: static (_, _) => Task.CompletedTask)
    {
        private readonly TaskCompletionSource _attachReturned =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<VoiceTransportLifetimeCompleted?> AttachAsync(
            VoicePresenceSessionLeaseHandle handle,
            IVoiceTransport transport,
            VoiceToolCredentialTransportBinding? toolCredentialBinding,
            CancellationToken ct = default)
        {
            var completed = await base.AttachAsync(handle, transport, toolCredentialBinding, ct);
            _attachReturned.TrySetResult();
            return completed;
        }

        public Task WaitForAttachReturnAsync() => _attachReturned.Task;
    }

    private sealed class ControllableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
            {
                return _utcNow;
            }
        }

        public void Advance(TimeSpan delta)
        {
            ManualTimer[] timers;
            lock (_gate)
            {
                _utcNow = _utcNow.Add(delta);
                timers = _timers.ToArray();
            }

            foreach (var timer in timers)
                timer.FireIfDue();
        }

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state, dueTime, period);
            lock (_gate)
            {
                _timers.Add(timer);
            }

            timer.FireIfDue();
            return timer;
        }

        private void Remove(ManualTimer timer)
        {
            lock (_gate)
            {
                _timers.Remove(timer);
            }
        }

        private sealed class ManualTimer(
            ControllableTimeProvider owner,
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period) : ITimer
        {
            private readonly object _gate = new();
            private TimeSpan _period = period;
            private DateTimeOffset? _dueAt = ResolveDueAt(owner.GetUtcNow(), dueTime);
            private bool _disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (_gate)
                {
                    if (_disposed)
                        return false;

                    _period = period;
                    _dueAt = ResolveDueAt(owner.GetUtcNow(), dueTime);
                }

                FireIfDue();
                return true;
            }

            public void FireIfDue()
            {
                while (true)
                {
                    lock (_gate)
                    {
                        if (_disposed || !_dueAt.HasValue || owner.GetUtcNow() < _dueAt.Value)
                            return;

                        if (_period == Timeout.InfiniteTimeSpan)
                        {
                            _dueAt = null;
                        }
                        else
                        {
                            _dueAt = owner.GetUtcNow().Add(_period);
                        }
                    }

                    callback(state);
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            public void Dispose()
            {
                lock (_gate)
                {
                    if (_disposed)
                        return;

                    _disposed = true;
                }

                owner.Remove(this);
            }

            private static DateTimeOffset? ResolveDueAt(DateTimeOffset now, TimeSpan dueTime) =>
                dueTime == Timeout.InfiniteTimeSpan ? null : now.Add(dueTime);
        }
    }

    private sealed class RecordingProjectionSessionEventHub : IProjectionSessionEventHub<VoiceRealtimeFrame>
    {
        private readonly List<Subscription> _subscriptions = [];

        public async Task PublishAsync(
            string rootActorId,
            string sessionId,
            VoiceRealtimeFrame evt,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var subscription in _subscriptions.ToArray())
            {
                if (!string.Equals(subscription.RootActorId, rootActorId, StringComparison.Ordinal) ||
                    !string.Equals(subscription.SessionId, sessionId, StringComparison.Ordinal))
                {
                    continue;
                }

                await subscription.Handler(evt.Clone());
            }
        }

        public Task<IAsyncDisposable> SubscribeAsync(
            string rootActorId,
            string sessionId,
            Func<VoiceRealtimeFrame, ValueTask> handler,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var subscription = new Subscription(rootActorId, sessionId, handler);
            _subscriptions.Add(subscription);
            return Task.FromResult<IAsyncDisposable>(new SubscriptionHandle(_subscriptions, subscription));
        }

        private sealed record Subscription(
            string RootActorId,
            string SessionId,
            Func<VoiceRealtimeFrame, ValueTask> Handler);

        private sealed class SubscriptionHandle(
            List<Subscription> subscriptions,
            Subscription subscription) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                subscriptions.Remove(subscription);
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class RecordingVoiceTransport : IVoiceTransport
    {
        public List<VoiceControlFrame> SentControls { get; } = [];

        public int DisposeCalls { get; private set; }

        public Task SendAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task SendControlAsync(VoiceControlFrame frame, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            SentControls.Add(frame.Clone());
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<VoiceTransportFrame> ReceiveFramesAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingWebRtcVoiceTransportFactory(
        IVoiceTransport transport,
        string answerSdp,
        Task completion) : IWebRtcVoiceTransportFactory
    {
        public List<string> RemoteOfferSdps { get; } = [];

        public WebRtcVoiceTransportOptions? LastOptions { get; private set; }

        public Task<WebRtcVoiceTransportSession> CreateAsync(
            string remoteOfferSdp,
            WebRtcVoiceTransportOptions options,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            RemoteOfferSdps.Add(remoteOfferSdp);
            LastOptions = options;
            return Task.FromResult(new WebRtcVoiceTransportSession(transport, answerSdp, completion));
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
