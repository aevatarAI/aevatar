using System.Text;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Aevatar.Foundation.VoicePresence.Hosting;
using Aevatar.Foundation.VoicePresence.Modules;
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
        var module = CreateModule(new RecordingVoiceProvider());
        await module.InitializeAsync(CancellationToken.None);

        var transport = new StubVoiceTransport();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolver = new RecordingSessionResolver(new VoicePresenceSession(module, static (_, _) => Task.CompletedTask));
        var factory = new FakeWebRtcVoiceTransportFactory(new WebRtcVoiceTransportSession(transport, "answer", completion.Task));
        using var app = CreateApp(resolver, factory);
        var context = CreateContext(app, HttpMethods.Post, "v=0\r\noffer");
        context.Request.RouteValues["actorId"] = "agent-1";

        await GetWhipEndpoint(app, HttpMethods.Post).RequestDelegate!(context);

        resolver.RequestedActorIds.ShouldContain("agent-1");
        resolver.Requests.ShouldContain(static request => string.Equals(request.ModuleName, null, StringComparison.Ordinal));

        completion.SetResult();
        await transport.DisposedTask.Task;
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
    public async Task Post_should_pass_module_query_to_registered_service_resolver()
    {
        var module = CreateModule(new RecordingVoiceProvider());
        await module.InitializeAsync(CancellationToken.None);

        var transport = new StubVoiceTransport();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolver = new RecordingSessionResolver(new VoicePresenceSession(module, static (_, _) => Task.CompletedTask));
        var factory = new FakeWebRtcVoiceTransportFactory(new WebRtcVoiceTransportSession(transport, "answer", completion.Task));
        using var app = CreateApp(resolver, factory);
        var context = CreateContext(app, HttpMethods.Post, "v=0\r\noffer");
        context.Request.RouteValues["actorId"] = "agent-1";
        context.Request.QueryString = new QueryString("?module=voice_presence_minicpm");

        await GetWhipEndpoint(app, HttpMethods.Post).RequestDelegate!(context);

        resolver.Requests.ShouldContain(request =>
            string.Equals(request.ActorId, "agent-1", StringComparison.Ordinal) &&
            string.Equals(request.ModuleName, "voice_presence_minicpm", StringComparison.Ordinal));

        completion.SetResult();
        await transport.DisposedTask.Task;
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
        var module = CreateModule(new RecordingVoiceProvider());
        var session = new VoicePresenceSession(module, static (_, _) => Task.CompletedTask);
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
        var module = CreateModule(new RecordingVoiceProvider());
        await module.InitializeAsync(CancellationToken.None);
        module.AttachTransport(new StubVoiceTransport(), static (_, _) => Task.CompletedTask);

        var session = new VoicePresenceSession(module, static (_, _) => Task.CompletedTask);
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
        var module = CreateModule(new RecordingVoiceProvider());
        await module.InitializeAsync(CancellationToken.None);

        var transport = new StubVoiceTransport();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new FakeWebRtcVoiceTransportFactory(new WebRtcVoiceTransportSession(transport, "v=0\r\nanswer", completion.Task));
        var detachCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = CreateTrackingSession(
            module,
            detachCompletedByTransport: new Dictionary<IVoiceTransport, TaskCompletionSource>
            {
                [transport] = detachCompleted,
            },
            pcmSampleRateHz: 16000);
        using var app = CreateApp((_, _) => Task.FromResult<VoicePresenceSession?>(session), factory);
        var context = CreateContext(app, HttpMethods.Post, "v=0\r\noffer");
        context.Request.RouteValues["actorId"] = "agent-1";

        await GetWhipEndpoint(app, HttpMethods.Post).RequestDelegate!(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status201Created);
        context.Response.ContentType.ShouldBe("application/sdp");
        context.Response.Headers.Location.ToString().ShouldBe("/voice/webrtc/agent-1");
        (await ReadBodyAsync(context)).ShouldBe("v=0\r\nanswer");
        module.IsTransportAttached.ShouldBeTrue();
        factory.Calls.Count.ShouldBe(1);
        factory.Calls[0].RemoteOfferSdp.ShouldBe("v=0\r\noffer");
        factory.Calls[0].Options.PcmSampleRateHz.ShouldBe(16000);
        transport.Disposed.ShouldBeFalse();

        completion.SetResult();
        await detachCompleted.Task;
        module.IsTransportAttached.ShouldBeFalse();
        transport.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task Delete_should_detach_current_transport()
    {
        var module = CreateModule(new RecordingVoiceProvider());
        await module.InitializeAsync(CancellationToken.None);
        var transport = new StubVoiceTransport();
        module.AttachTransport(transport, static (_, _) => Task.CompletedTask);

        var session = new VoicePresenceSession(module, static (_, _) => Task.CompletedTask);
        using var app = CreateApp((_, _) => Task.FromResult<VoicePresenceSession?>(session));
        var context = CreateContext(app, HttpMethods.Delete, string.Empty);
        context.Request.RouteValues["actorId"] = "agent-1";

        await GetWhipEndpoint(app, HttpMethods.Delete).RequestDelegate!(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status204NoContent);
        module.IsTransportAttached.ShouldBeFalse();
        transport.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task Post_should_dispose_transport_when_attach_fails()
    {
        var transport = new StubVoiceTransport();
        var factory = new FakeWebRtcVoiceTransportFactory(new WebRtcVoiceTransportSession(transport, "answer", Task.CompletedTask));
        var session = new VoicePresenceSession(
            isInitialized: static () => true,
            isTransportAttached: static () => false,
            attachTransportAsync: static (_, _) => throw new InvalidOperationException("attach failed"),
            detachTransportAsync: static (_, _) => Task.CompletedTask,
            pcmSampleRateHz: 24000);
        using var app = CreateApp((_, _) => Task.FromResult<VoicePresenceSession?>(session), factory);
        var context = CreateContext(app, HttpMethods.Post, "v=0\r\noffer");
        context.Request.RouteValues["actorId"] = "agent-1";

        await Should.ThrowAsync<InvalidOperationException>(() =>
            GetWhipEndpoint(app, HttpMethods.Post).RequestDelegate!(context));

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

    [Fact]
    public async Task Stale_completion_should_not_detach_new_transport()
    {
        var module = CreateModule(new RecordingVoiceProvider());
        await module.InitializeAsync(CancellationToken.None);

        var transport1 = new StubVoiceTransport();
        var completion1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport2 = new StubVoiceTransport();
        var completion2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new SequencedWebRtcVoiceTransportFactory(
            new WebRtcVoiceTransportSession(transport1, "answer-1", completion1.Task),
            new WebRtcVoiceTransportSession(transport2, "answer-2", completion2.Task));
        var transport1DetachCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport2DetachCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = CreateTrackingSession(
            module,
            detachCompletedByTransport: new Dictionary<IVoiceTransport, TaskCompletionSource>
            {
                [transport1] = transport1DetachCompleted,
                [transport2] = transport2DetachCompleted,
            });
        using var app = CreateApp((_, _) => Task.FromResult<VoicePresenceSession?>(session), factory);

        var post1 = CreateContext(app, HttpMethods.Post, "offer-1");
        post1.Request.RouteValues["actorId"] = "agent-1";
        await GetWhipEndpoint(app, HttpMethods.Post).RequestDelegate!(post1);

        var delete = CreateContext(app, HttpMethods.Delete, string.Empty);
        delete.Request.RouteValues["actorId"] = "agent-1";
        await GetWhipEndpoint(app, HttpMethods.Delete).RequestDelegate!(delete);
        transport1.Disposed.ShouldBeTrue();

        var post2 = CreateContext(app, HttpMethods.Post, "offer-2");
        post2.Request.RouteValues["actorId"] = "agent-1";
        await GetWhipEndpoint(app, HttpMethods.Post).RequestDelegate!(post2);
        module.IsTransportAttached.ShouldBeTrue();
        transport2.Disposed.ShouldBeFalse();

        completion1.SetResult();
        await transport1DetachCompleted.Task;

        module.IsTransportAttached.ShouldBeTrue();
        transport2.Disposed.ShouldBeFalse();

        completion2.SetResult();
        await transport2DetachCompleted.Task;
        module.IsTransportAttached.ShouldBeFalse();
    }

    private static VoicePresenceSession CreateTrackingSession(
        VoicePresenceModule module,
        IReadOnlyDictionary<IVoiceTransport, TaskCompletionSource> detachCompletedByTransport,
        int pcmSampleRateHz = 24000) =>
        new(
            isInitialized: () => module.IsInitialized,
            isTransportAttached: () => module.IsTransportAttached,
            attachTransportAsync: (transport, _) =>
            {
                module.AttachTransport(transport, static (_, _) => Task.CompletedTask);
                return Task.CompletedTask;
            },
            detachTransportAsync: async (expectedTransport, _) =>
            {
                await module.DetachTransportAsync(expectedTransport);
                if (expectedTransport != null &&
                    detachCompletedByTransport.TryGetValue(expectedTransport, out var completion))
                {
                    completion.TrySetResult();
                }
            },
            pcmSampleRateHz,
            module,
            static (_, _) => Task.CompletedTask);

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

    private static VoicePresenceModule CreateModule(RecordingVoiceProvider provider) =>
        new(
            provider,
            new VoiceProviderConfig
            {
                ProviderName = "openai",
                ApiKey = "sk-test",
                Model = "gpt-realtime",
            },
            new VoiceSessionConfig
            {
                Voice = "alloy",
                SampleRateHz = 24000,
            });

    private sealed class RecordingVoiceProvider : IRealtimeVoiceProvider
    {
        public Func<VoiceProviderEvent, CancellationToken, Task>? OnEvent { private get; set; }

        public Task ConnectAsync(VoiceProviderConfig config, CancellationToken ct)
        {
            _ = config;
            _ = ct;
            return Task.CompletedTask;
        }

        public Task SendAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken ct)
        {
            _ = pcm16;
            _ = ct;
            return Task.CompletedTask;
        }

        public Task SendToolResultAsync(string callId, string resultJson, CancellationToken ct)
        {
            _ = callId;
            _ = resultJson;
            _ = ct;
            return Task.CompletedTask;
        }

        public Task InjectEventAsync(VoiceConversationEventInjection injection, CancellationToken ct)
        {
            _ = injection;
            _ = ct;
            return Task.CompletedTask;
        }

        public Task CancelResponseAsync(CancellationToken ct)
        {
            _ = ct;
            return Task.CompletedTask;
        }

        public Task UpdateSessionAsync(VoiceSessionConfig session, CancellationToken ct)
        {
            _ = session;
            _ = ct;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingSessionResolver(VoicePresenceSession? session) : IVoicePresenceSessionResolver
    {
        public List<VoicePresenceSessionRequest> Requests { get; } = [];

        public List<string> RequestedActorIds { get; } = [];

        public Task<VoicePresenceSession?> ResolveAsync(VoicePresenceSessionRequest request, CancellationToken ct = default)
        {
            _ = ct;
            Requests.Add(request);
            RequestedActorIds.Add(request.ActorId);
            return Task.FromResult(session);
        }
    }

    private sealed class StubVoiceTransport : IVoiceTransport
    {
        public bool Disposed { get; private set; }

        public TaskCompletionSource DisposedTask { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

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
            DisposedTask.TrySetResult();
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

    private sealed class SequencedWebRtcVoiceTransportFactory(params WebRtcVoiceTransportSession[] sessions)
        : IWebRtcVoiceTransportFactory
    {
        private readonly Queue<WebRtcVoiceTransportSession> _sessions = new(sessions);

        public Task<WebRtcVoiceTransportSession> CreateAsync(
            string remoteOfferSdp,
            WebRtcVoiceTransportOptions options,
            CancellationToken ct)
        {
            _ = remoteOfferSdp;
            _ = options;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(_sessions.Dequeue());
        }
    }
}
