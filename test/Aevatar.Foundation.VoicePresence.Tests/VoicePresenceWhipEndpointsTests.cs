using System.Text;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Aevatar.Foundation.VoicePresence.Abstractions.Sessions;
using Aevatar.Foundation.VoicePresence.Hosting;
using Aevatar.Foundation.VoicePresence.Transport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace Aevatar.Foundation.VoicePresence.Tests;

public class VoicePresenceWhipEndpointsTests
{
    [Fact]
    public void MapVoicePresenceWhip_should_register_post_and_delete_routes()
    {
        using var app = CreateApp(new RecordingRealtimeSession(VoiceRealtimeSessionStartError.NotFound));

        GetWhipEndpoint(app, HttpMethods.Post).RoutePattern.RawText.ShouldStartWith("/voice/webrtc/{actorId}");
        GetWhipEndpoint(app, HttpMethods.Delete).RoutePattern.RawText.ShouldStartWith("/voice/webrtc/{actorId}");
    }

    [Fact]
    public async Task Post_should_resolve_session_from_registered_service()
    {
        var transport = new StubVoiceTransport();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new RecordingRealtimeSession();
        var factory = new FakeWebRtcVoiceTransportFactory(new WebRtcVoiceTransportSession(transport, "answer", completion.Task));
        using var app = CreateApp(session, transportFactory: factory);
        var context = CreateContext(app, HttpMethods.Post, "v=0\r\noffer");
        context.Request.RouteValues["actorId"] = "agent-1";

        await GetWhipEndpoint(app, HttpMethods.Post).RequestDelegate!(context);

        session.RequestedActorIds.ShouldContain("agent-1");
        session.Requests.ShouldContain(static request => string.Equals(request.ModuleName, null, StringComparison.Ordinal));
        transport.Disposed.ShouldBeFalse();
    }

    [Fact]
    public async Task Post_should_pass_module_query_to_registered_service_resolver()
    {
        var transport = new StubVoiceTransport();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new RecordingRealtimeSession();
        var factory = new FakeWebRtcVoiceTransportFactory(new WebRtcVoiceTransportSession(transport, "answer", completion.Task));
        using var app = CreateApp(session, transportFactory: factory);
        var context = CreateContext(app, HttpMethods.Post, "v=0\r\noffer");
        context.Request.RouteValues["actorId"] = "agent-1";
        context.Request.QueryString = new QueryString("?module=voice_presence_minicpm");

        await GetWhipEndpoint(app, HttpMethods.Post).RequestDelegate!(context);

        session.Requests.ShouldContain(request =>
            string.Equals(request.ActorId, "agent-1", StringComparison.Ordinal) &&
            string.Equals(request.ModuleName, "voice_presence_minicpm", StringComparison.Ordinal));
        transport.Disposed.ShouldBeFalse();
    }

    [Fact]
    public async Task Post_should_reject_missing_actor_id()
    {
        using var app = CreateApp(new RecordingRealtimeSession(VoiceRealtimeSessionStartError.NotFound));
        var context = CreateContext(app, HttpMethods.Post, string.Empty);

        await GetWhipEndpoint(app, HttpMethods.Post).RequestDelegate!(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        (await ReadBodyAsync(context)).ShouldContain("actorId is required.");
    }

    [Fact]
    public async Task Post_should_reject_empty_sdp_offer()
    {
        using var app = CreateApp(new RecordingRealtimeSession(VoiceRealtimeSessionStartError.NotFound));
        var context = CreateContext(app, HttpMethods.Post, "  ");
        context.Request.RouteValues["actorId"] = "agent-1";

        await GetWhipEndpoint(app, HttpMethods.Post).RequestDelegate!(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        (await ReadBodyAsync(context)).ShouldContain("SDP offer is required.");
    }

    [Fact]
    public async Task Post_should_return_not_found_when_session_missing()
    {
        using var app = CreateApp(new RecordingRealtimeSession(VoiceRealtimeSessionStartError.NotFound));
        var context = CreateContext(app, HttpMethods.Post, "v=0\r\noffer");
        context.Request.RouteValues["actorId"] = "agent-1";

        await GetWhipEndpoint(app, HttpMethods.Post).RequestDelegate!(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status404NotFound);
        (await ReadBodyAsync(context)).ShouldContain("Voice session not found");
    }

    [Fact]
    public async Task Post_should_return_service_unavailable_when_module_not_initialized()
    {
        using var app = CreateApp(new RecordingRealtimeSession(VoiceRealtimeSessionStartError.NotInitialized));
        var context = CreateContext(app, HttpMethods.Post, "v=0\r\noffer");
        context.Request.RouteValues["actorId"] = "agent-1";

        await GetWhipEndpoint(app, HttpMethods.Post).RequestDelegate!(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status503ServiceUnavailable);
        (await ReadBodyAsync(context)).ShouldContain("Voice module not initialized.");
    }

    [Fact]
    public async Task Post_should_return_conflict_when_transport_already_attached()
    {
        using var app = CreateApp(new RecordingRealtimeSession(VoiceRealtimeSessionStartError.TransportAlreadyAttached));
        var context = CreateContext(app, HttpMethods.Post, "v=0\r\noffer");
        context.Request.RouteValues["actorId"] = "agent-1";

        await GetWhipEndpoint(app, HttpMethods.Post).RequestDelegate!(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status409Conflict);
        (await ReadBodyAsync(context)).ShouldContain("Voice transport already attached.");
    }

    [Fact]
    public async Task Post_should_attach_transport_and_return_answer_sdp()
    {
        var transport = new StubVoiceTransport();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new FakeWebRtcVoiceTransportFactory(new WebRtcVoiceTransportSession(transport, "v=0\r\nanswer", completion.Task));
        var lifetimeCompletedCalls = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mediaPort = new RecordingVolatileMediaStreamPort(
            completeTransportLifetimeAsync: (_, _, _) =>
            {
                lifetimeCompletedCalls.TrySetResult();
                return Task.CompletedTask;
            });
        using var app = CreateApp(new RecordingRealtimeSession(pcmSampleRateHz: 16000), mediaPort, factory);
        var context = CreateContext(app, HttpMethods.Post, "v=0\r\noffer");
        context.Request.RouteValues["actorId"] = "agent-1";

        await GetWhipEndpoint(app, HttpMethods.Post).RequestDelegate!(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status201Created);
        context.Response.ContentType.ShouldBe("application/sdp");
        context.Response.Headers.Location.ToString().ShouldBe("/voice/webrtc/agent-1");
        (await ReadBodyAsync(context)).ShouldBe("v=0\r\nanswer");
        mediaPort.AttachCalls.ShouldBe(1);
        factory.Calls.Count.ShouldBe(1);
        factory.Calls[0].RemoteOfferSdp.ShouldBe("v=0\r\noffer");
        factory.Calls[0].Options.PcmSampleRateHz.ShouldBe(16000);
        transport.Disposed.ShouldBeFalse();

        completion.SetResult();
        await lifetimeCompletedCalls.Task.WaitAsync(TimeSpan.FromSeconds(3));
        transport.Disposed.ShouldBeFalse();
    }

    [Fact]
    public async Task Delete_should_detach_current_transport()
    {
        var mediaPort = new RecordingVolatileMediaStreamPort();
        using var app = CreateApp(new RecordingRealtimeSession(), mediaPort);
        var context = CreateContext(app, HttpMethods.Delete, string.Empty);
        context.Request.RouteValues["actorId"] = "agent-1";

        await GetWhipEndpoint(app, HttpMethods.Delete).RequestDelegate!(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status204NoContent);
        mediaPort.DetachCalls.ShouldBe(1);
        mediaPort.LastDetachedTransport.ShouldBeNull();
    }

    [Fact]
    public async Task Delete_should_request_detach_purpose()
    {
        var session = new RecordingRealtimeSession();
        var mediaPort = new RecordingVolatileMediaStreamPort();
        using var app = CreateApp(session, mediaPort);
        var context = CreateContext(app, HttpMethods.Delete, string.Empty);
        context.Request.RouteValues["actorId"] = "agent-1";

        await GetWhipEndpoint(app, HttpMethods.Delete).RequestDelegate!(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status204NoContent);
        session.Requests.ShouldHaveSingleItem().Purpose.ShouldBe(VoiceRealtimeSessionPurpose.Detach);
        mediaPort.DetachCalls.ShouldBe(1);
    }

    [Fact]
    public async Task Post_should_dispose_transport_when_attach_fails()
    {
        var transport = new StubVoiceTransport();
        var factory = new FakeWebRtcVoiceTransportFactory(new WebRtcVoiceTransportSession(transport, "answer", Task.CompletedTask));
        var mediaPort = new RecordingVolatileMediaStreamPort(
            attachAsync: static (_, _) => throw new InvalidOperationException("attach failed"));
        using var app = CreateApp(new RecordingRealtimeSession(), mediaPort, factory);
        var context = CreateContext(app, HttpMethods.Post, "v=0\r\noffer");
        context.Request.RouteValues["actorId"] = "agent-1";

        await Should.ThrowAsync<InvalidOperationException>(() =>
            GetWhipEndpoint(app, HttpMethods.Post).RequestDelegate!(context));

        transport.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task Post_should_return_service_unavailable_and_dispose_transport_when_remote_audio_is_unavailable()
    {
        var transport = new StubVoiceTransport();
        var factory = new FakeWebRtcVoiceTransportFactory(new WebRtcVoiceTransportSession(transport, "answer", Task.CompletedTask));
        var mediaPort = new RecordingVolatileMediaStreamPort(
            attachAsync: static (_, _) => throw new VoiceVolatileMediaStreamUnavailableException());
        using var app = CreateApp(new RecordingRealtimeSession(), mediaPort, factory);
        var context = CreateContext(app, HttpMethods.Post, "v=0\r\noffer");
        context.Request.RouteValues["actorId"] = "agent-1";

        await GetWhipEndpoint(app, HttpMethods.Post).RequestDelegate!(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status503ServiceUnavailable);
        (await ReadBodyAsync(context)).ShouldBe(VoiceVolatileMediaStreamUnavailableException.Reason);
        transport.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task Delete_should_reject_missing_actor_id()
    {
        using var app = CreateApp(new RecordingRealtimeSession(VoiceRealtimeSessionStartError.NotFound));
        var context = CreateContext(app, HttpMethods.Delete, string.Empty);

        await GetWhipEndpoint(app, HttpMethods.Delete).RequestDelegate!(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        (await ReadBodyAsync(context)).ShouldContain("actorId is required.");
    }

    [Fact]
    public async Task Delete_should_return_not_found_when_session_missing()
    {
        using var app = CreateApp(new RecordingRealtimeSession(VoiceRealtimeSessionStartError.NotFound));
        var context = CreateContext(app, HttpMethods.Delete, string.Empty);
        context.Request.RouteValues["actorId"] = "agent-1";

        await GetWhipEndpoint(app, HttpMethods.Delete).RequestDelegate!(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status404NotFound);
        (await ReadBodyAsync(context)).ShouldContain("Voice session not found");
    }

    private static WebApplication CreateApp(
        RecordingRealtimeSession session,
        RecordingVolatileMediaStreamPort? mediaPort = null,
        IWebRtcVoiceTransportFactory? transportFactory = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.Services.AddSingleton<IRealtimeSession<VoiceRealtimeSessionRequest, VoiceRealtimeSessionAccepted, VoiceRealtimeSessionStartError, VoiceRealtimeFrame, VoiceRealtimeSessionCompletion>>(session);
        builder.Services.AddSingleton<IVoiceVolatileMediaStreamPort>(mediaPort ?? new RecordingVolatileMediaStreamPort());
        var app = builder.Build();
        app.MapVoicePresenceWhip("/voice/webrtc/{actorId}", transportFactory);
        return app;
    }

    private static RouteEndpoint GetWhipEndpoint(WebApplication app, string method) =>
        ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(x => x.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(x =>
                x.RoutePattern.RawText?.StartsWith("/voice/webrtc/{actorId}", StringComparison.Ordinal) == true &&
                x.Metadata.OfType<HttpMethodMetadata>().Single().HttpMethods.Contains(method, StringComparer.OrdinalIgnoreCase));

    private static DefaultHttpContext CreateContext(WebApplication app, string method, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        return new DefaultHttpContext
        {
            RequestServices = app.Services,
            Request =
            {
                Method = method,
                Path = "/voice/webrtc/agent-1",
                Body = new MemoryStream(bytes),
                ContentLength = bytes.Length,
            },
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

    private static VoicePresenceSessionLeaseHandle CreateLeaseHandle(
        string sessionId,
        int sampleRateHz) =>
        new(
            "agent-1",
            "voice_presence",
            sessionId,
            "voice-presence.host",
            sampleRateHz,
            DateTimeOffset.UtcNow.AddMinutes(5),
            VoiceRemoteAudioSupport.Supported,
            "transport-1");

    private sealed class RecordingRealtimeSession(
        VoiceRealtimeSessionStartError? failure = null,
        int pcmSampleRateHz = 24000)
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
                pcmSampleRateHz,
                42,
                CreateLeaseHandle("session-1", pcmSampleRateHz));
            return Task.FromResult(
                RealtimeSessionResult<VoiceRealtimeSessionAccepted, VoiceRealtimeSessionStartError, VoiceRealtimeSessionCompletion>
                    .Success(accepted, VoiceRealtimeSessionCompletion.Accepted, completed: true));
        }
    }

    private sealed class RecordingVolatileMediaStreamPort(
        Func<IVoiceTransport, CancellationToken, Task>? attachAsync = null,
        Func<VoicePresenceSessionLeaseHandle, VoiceTransportLifetimeCompleted?, string, Task>? completeTransportLifetimeAsync = null)
        : IVoiceVolatileMediaStreamPort
    {
        public bool SupportsRemoteAudio => true;

        public Task<bool> TrySendToolResultAsync(
            string transportLeaseId,
            string callId,
            string resultJson,
            CancellationToken ct = default) =>
            Task.FromResult(false);

        public int AttachCalls { get; private set; }

        public int DetachCalls { get; private set; }

        public int LifetimeCompletionCalls { get; private set; }

        public IVoiceTransport? LastDetachedTransport { get; private set; }

        public async Task<VoiceTransportLifetimeCompleted?> AttachAsync(
            VoicePresenceSessionLeaseHandle handle,
            IVoiceTransport transport,
            CancellationToken ct = default)
        {
            _ = handle;
            AttachCalls++;
            if (attachAsync != null)
                await attachAsync(transport, ct);
            return new VoiceTransportLifetimeCompleted
            {
                SessionId = handle.SessionId,
                TransportLeaseId = "transport-1",
                Reason = "completed",
                OwnerId = handle.OwnerId,
            };
        }

        public Task DetachAsync(
            VoicePresenceSessionLeaseHandle handle,
            IVoiceTransport? expectedTransport,
            CancellationToken ct = default)
        {
            _ = handle;
            ct.ThrowIfCancellationRequested();
            DetachCalls++;
            LastDetachedTransport = expectedTransport;
            return Task.CompletedTask;
        }

        public async Task CompleteTransportLifetimeAsync(
            VoicePresenceSessionLeaseHandle handle,
            VoiceTransportLifetimeCompleted? completed,
            string reason,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            LifetimeCompletionCalls++;
            if (completeTransportLifetimeAsync != null)
                await completeTransportLifetimeAsync(handle, completed, reason);
        }
    }

    private sealed class StubVoiceTransport : IVoiceTransport
    {
        public bool Disposed { get; private set; }

        public Task SendAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken ct)
        {
            _ = pcm16;
            _ = ct;
            return Task.CompletedTask;
        }

        public Task SendControlAsync(VoiceControlFrame frame, CancellationToken ct)
        {
            _ = frame;
            _ = ct;
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<VoiceTransportFrame> ReceiveFramesAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            _ = ct;
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeWebRtcVoiceTransportFactory(WebRtcVoiceTransportSession session) : IWebRtcVoiceTransportFactory
    {
        public List<(string RemoteOfferSdp, WebRtcVoiceTransportOptions Options)> Calls { get; } = [];

        public Task<WebRtcVoiceTransportSession> CreateAsync(
            string remoteOfferSdp,
            WebRtcVoiceTransportOptions options,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add((remoteOfferSdp, options));
            return Task.FromResult(session);
        }
    }
}
