using System.Text;
using Aevatar.Foundation.VoicePresence.Abstractions;
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
        using var app = CreateApp(static (_, _) => Task.FromResult<VoicePresenceSession?>(null));

        GetWhipEndpoint(app, HttpMethods.Post).RoutePattern.RawText.ShouldStartWith("/voice/webrtc/{actorId}");
        GetWhipEndpoint(app, HttpMethods.Delete).RoutePattern.RawText.ShouldStartWith("/voice/webrtc/{actorId}");
    }

    [Fact]
    public async Task Post_should_resolve_session_from_registered_service()
    {
        var transport = new StubVoiceTransport();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolver = new RecordingSessionResolver(CreateSession());
        var factory = new FakeWebRtcVoiceTransportFactory(new WebRtcVoiceTransportSession(transport, "answer", completion.Task));
        using var app = CreateApp(resolver, factory);
        var context = CreateContext(app, HttpMethods.Post, "v=0\r\noffer");
        context.Request.RouteValues["actorId"] = "agent-1";

        await GetWhipEndpoint(app, HttpMethods.Post).RequestDelegate!(context);

        resolver.RequestedActorIds.ShouldContain("agent-1");
        resolver.Requests.ShouldContain(static request => string.Equals(request.ModuleName, null, StringComparison.Ordinal));
        transport.Disposed.ShouldBeFalse();
    }

    [Fact]
    public async Task Post_should_pass_module_query_to_registered_service_resolver()
    {
        var transport = new StubVoiceTransport();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolver = new RecordingSessionResolver(CreateSession());
        var factory = new FakeWebRtcVoiceTransportFactory(new WebRtcVoiceTransportSession(transport, "answer", completion.Task));
        using var app = CreateApp(resolver, factory);
        var context = CreateContext(app, HttpMethods.Post, "v=0\r\noffer");
        context.Request.RouteValues["actorId"] = "agent-1";
        context.Request.QueryString = new QueryString("?module=voice_presence_minicpm");

        await GetWhipEndpoint(app, HttpMethods.Post).RequestDelegate!(context);

        resolver.Requests.ShouldContain(request =>
            string.Equals(request.ActorId, "agent-1", StringComparison.Ordinal) &&
            string.Equals(request.ModuleName, "voice_presence_minicpm", StringComparison.Ordinal));
        transport.Disposed.ShouldBeFalse();
    }

    [Fact]
    public async Task Post_should_reject_missing_actor_id()
    {
        using var app = CreateApp(static (_, _) => Task.FromResult<VoicePresenceSession?>(null));
        var context = CreateContext(app, HttpMethods.Post, string.Empty);

        await GetWhipEndpoint(app, HttpMethods.Post).RequestDelegate!(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        (await ReadBodyAsync(context)).ShouldContain("actorId is required.");
    }

    [Fact]
    public async Task Post_should_reject_empty_sdp_offer()
    {
        using var app = CreateApp(static (_, _) => Task.FromResult<VoicePresenceSession?>(null));
        var context = CreateContext(app, HttpMethods.Post, "  ");
        context.Request.RouteValues["actorId"] = "agent-1";

        await GetWhipEndpoint(app, HttpMethods.Post).RequestDelegate!(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        (await ReadBodyAsync(context)).ShouldContain("SDP offer is required.");
    }

    [Fact]
    public async Task Post_should_return_not_found_when_session_missing()
    {
        using var app = CreateApp(static (_, _) => Task.FromResult<VoicePresenceSession?>(null));
        var context = CreateContext(app, HttpMethods.Post, "v=0\r\noffer");
        context.Request.RouteValues["actorId"] = "agent-1";

        await GetWhipEndpoint(app, HttpMethods.Post).RequestDelegate!(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status404NotFound);
        (await ReadBodyAsync(context)).ShouldContain("Voice session not found");
    }

    [Fact]
    public async Task Post_should_return_service_unavailable_when_module_not_initialized()
    {
        var session = CreateSession(initialized: false);
        using var app = CreateApp((_, _) => Task.FromResult<VoicePresenceSession?>(session));
        var context = CreateContext(app, HttpMethods.Post, "v=0\r\noffer");
        context.Request.RouteValues["actorId"] = "agent-1";

        await GetWhipEndpoint(app, HttpMethods.Post).RequestDelegate!(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status503ServiceUnavailable);
        (await ReadBodyAsync(context)).ShouldContain("Voice module not initialized.");
    }

    [Fact]
    public async Task Post_should_return_conflict_when_transport_already_attached()
    {
        var session = CreateSession(transportAttached: true);
        using var app = CreateApp((_, _) => Task.FromResult<VoicePresenceSession?>(session));
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
        var attachCalls = 0;
        var lifetimeCompletedCalls = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = CreateSession(
            pcmSampleRateHz: 16000,
            attachTransportAsync: (_, _) =>
            {
                attachCalls++;
                return Task.CompletedTask;
            },
            completeTransportLifetimeAsync: (_, _) =>
            {
                lifetimeCompletedCalls.TrySetResult();
                return Task.CompletedTask;
            });
        using var app = CreateApp((_, _) => Task.FromResult<VoicePresenceSession?>(session), factory);
        var context = CreateContext(app, HttpMethods.Post, "v=0\r\noffer");
        context.Request.RouteValues["actorId"] = "agent-1";

        await GetWhipEndpoint(app, HttpMethods.Post).RequestDelegate!(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status201Created);
        context.Response.ContentType.ShouldBe("application/sdp");
        context.Response.Headers.Location.ToString().ShouldBe("/voice/webrtc/agent-1");
        (await ReadBodyAsync(context)).ShouldBe("v=0\r\nanswer");
        attachCalls.ShouldBe(1);
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
        var transport = new StubVoiceTransport();
        var session = CreateSession(
            transportAttached: true,
            detachTransportAsync: async (expectedTransport, _) =>
            {
                await (expectedTransport ?? transport).DisposeAsync();
            });
        using var app = CreateApp((_, _) => Task.FromResult<VoicePresenceSession?>(session));
        var context = CreateContext(app, HttpMethods.Delete, string.Empty);
        context.Request.RouteValues["actorId"] = "agent-1";

        await GetWhipEndpoint(app, HttpMethods.Delete).RequestDelegate!(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status204NoContent);
        transport.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task Delete_should_detach_actor_owned_attached_session()
    {
        var detachCalls = 0;
        var releaseCalls = 0;
        var session = CreateSession(
            transportAttached: true,
            attachTransportAsync: static (_, _) => throw new InvalidOperationException("attach should not run"),
            detachTransportAsync: (_, _) =>
            {
                detachCalls++;
                releaseCalls++;
                return Task.CompletedTask;
            });
        var resolver = new RecordingSessionResolver(VoicePresenceSessionResolution.LeaseAcceptedAttached(session));
        using var app = CreateApp(resolver);
        var context = CreateContext(app, HttpMethods.Delete, string.Empty);
        context.Request.RouteValues["actorId"] = "agent-1";

        await GetWhipEndpoint(app, HttpMethods.Delete).RequestDelegate!(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status204NoContent);
        resolver.Requests.ShouldHaveSingleItem().Purpose.ShouldBe(VoicePresenceSessionRequestPurpose.Detach);
        detachCalls.ShouldBe(1);
        releaseCalls.ShouldBe(1);
    }

    [Fact]
    public async Task Delete_should_detach_transport_already_attached_typed_preflight_session()
    {
        var attachCalls = 0;
        var detachCalls = 0;
        var session = CreateSession(
            transportAttached: true,
            attachTransportAsync: (_, _) =>
            {
                attachCalls++;
                return Task.CompletedTask;
            },
            detachTransportAsync: (_, _) =>
            {
                detachCalls++;
                return Task.CompletedTask;
            });
        var resolver = new RecordingSessionResolver(new VoicePresenceSessionResolution(
            VoicePresenceSessionResolutionKind.PreflightFailed,
            session,
            VoicePresencePreflightFailureKind.TransportAlreadyAttached));
        using var app = CreateApp(resolver);
        var context = CreateContext(app, HttpMethods.Delete, string.Empty);
        context.Request.RouteValues["actorId"] = "agent-1";

        await GetWhipEndpoint(app, HttpMethods.Delete).RequestDelegate!(context)
            .WaitAsync(TimeSpan.FromSeconds(5));

        context.Response.StatusCode.ShouldBe(StatusCodes.Status204NoContent);
        resolver.Requests.ShouldHaveSingleItem().Purpose.ShouldBe(VoicePresenceSessionRequestPurpose.Detach);
        attachCalls.ShouldBe(0);
        detachCalls.ShouldBe(1);
    }

    [Fact]
    public async Task Post_should_dispose_transport_when_attach_fails()
    {
        var transport = new StubVoiceTransport();
        var factory = new FakeWebRtcVoiceTransportFactory(new WebRtcVoiceTransportSession(transport, "answer", Task.CompletedTask));
        var session = CreateSession(
            attachTransportAsync: static (_, _) => throw new InvalidOperationException("attach failed"));
        using var app = CreateApp((_, _) => Task.FromResult<VoicePresenceSession?>(session), factory);
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
        var session = CreateSession(
            attachTransportAsync: static (_, _) => throw new VoiceRemoteAudioTransportUnavailableException());
        using var app = CreateApp((_, _) => Task.FromResult<VoicePresenceSession?>(session), factory);
        var context = CreateContext(app, HttpMethods.Post, "v=0\r\noffer");
        context.Request.RouteValues["actorId"] = "agent-1";

        await GetWhipEndpoint(app, HttpMethods.Post).RequestDelegate!(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status503ServiceUnavailable);
        (await ReadBodyAsync(context)).ShouldBe(VoiceRemoteAudioTransportUnavailableException.Reason);
        transport.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task Delete_should_reject_missing_actor_id()
    {
        using var app = CreateApp(static (_, _) => Task.FromResult<VoicePresenceSession?>(null));
        var context = CreateContext(app, HttpMethods.Delete, string.Empty);

        await GetWhipEndpoint(app, HttpMethods.Delete).RequestDelegate!(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        (await ReadBodyAsync(context)).ShouldContain("actorId is required.");
    }

    [Fact]
    public async Task Delete_should_return_not_found_when_session_missing()
    {
        using var app = CreateApp(static (_, _) => Task.FromResult<VoicePresenceSession?>(null));
        var context = CreateContext(app, HttpMethods.Delete, string.Empty);
        context.Request.RouteValues["actorId"] = "agent-1";

        await GetWhipEndpoint(app, HttpMethods.Delete).RequestDelegate!(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status404NotFound);
        (await ReadBodyAsync(context)).ShouldContain("Voice session not found");
    }

    private static VoicePresenceSession CreateSession(
        bool initialized = true,
        bool transportAttached = false,
        int pcmSampleRateHz = 24000,
        Func<IVoiceTransport, CancellationToken, Task>? attachTransportAsync = null,
        Func<IVoiceTransport?, CancellationToken, Task>? detachTransportAsync = null,
        Func<VoiceTransportLifetimeCompleted?, CancellationToken, Task>? completeTransportLifetimeAsync = null) =>
        new(
            isInitialized: () => initialized,
            isTransportAttached: () => transportAttached,
            attachTransportAsync: attachTransportAsync ?? ((_, _) => Task.CompletedTask),
            detachTransportAsync: detachTransportAsync ?? ((_, _) => Task.CompletedTask),
            pcmSampleRateHz,
            completeTransportLifetimeAsync);

    private static WebApplication CreateApp(
        Func<string, HttpContext, Task<VoicePresenceSession?>> resolveSession,
        IWebRtcVoiceTransportFactory? transportFactory = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        var app = builder.Build();
        app.MapVoicePresenceWhip("/voice/webrtc/{actorId}", resolveSession, transportFactory);
        return app;
    }

    private static WebApplication CreateApp(
        IVoicePresenceSessionResolver resolver,
        IWebRtcVoiceTransportFactory? transportFactory = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.Services.AddSingleton(resolver);
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

    private sealed class RecordingSessionResolver : IVoicePresenceSessionResolver
    {
        private readonly VoicePresenceSessionResolution _resolution;

        public RecordingSessionResolver(VoicePresenceSession? session)
            : this(session == null
                ? VoicePresenceSessionResolution.PreflightFailed(VoicePresencePreflightFailureKind.NotFound)
                : VoicePresenceSessionResolution.LeaseAcceptedAttached(session))
        {
        }

        public RecordingSessionResolver(VoicePresenceSessionResolution resolution)
        {
            _resolution = resolution;
        }

        public List<VoicePresenceSessionRequest> Requests { get; } = [];

        public List<string> RequestedActorIds { get; } = [];

        public Task<VoicePresenceSessionResolution> ResolveAsync(
            VoicePresenceSessionRequest request,
            CancellationToken ct = default)
        {
            _ = ct;
            Requests.Add(request);
            RequestedActorIds.Add(request.ActorId);
            return Task.FromResult(_resolution);
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
