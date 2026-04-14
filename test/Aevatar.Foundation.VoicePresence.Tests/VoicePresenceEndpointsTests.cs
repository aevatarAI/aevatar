using System.Net.WebSockets;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Aevatar.Foundation.VoicePresence.Hosting;
using Aevatar.Foundation.VoicePresence.Modules;
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
        using var app = CreateApp(static (_, _) => Task.FromResult<VoicePresenceSession?>(null));

        var route = GetVoiceEndpoint(app);

        route.RoutePattern.RawText.ShouldBe("/voice/{actorId}");
    }

    [Fact]
    public async Task Request_should_resolve_session_from_registered_service()
    {
        var module = CreateModule(new RecordingVoiceProvider());
        await module.InitializeAsync(CancellationToken.None);

        var resolver = new RecordingSessionResolver(new VoicePresenceSession(module, static (_, _) => Task.CompletedTask));
        var socket = new FakeWebSocket(WebSocketState.Open, keepOpenUntilCancelledWhenEmpty: true);
        using var app = CreateApp(resolver);
        var context = CreateHttpContext(app);
        context.Features.Set<IHttpWebSocketFeature>(new FakeHttpWebSocketFeature(socket));
        context.Request.RouteValues["actorId"] = "agent-1";

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
        context.RequestAborted = cts.Token;

        await GetVoiceEndpoint(app).RequestDelegate!(context);

        resolver.RequestedActorIds.ShouldContain("agent-1");
        resolver.Requests.ShouldContain(static request => string.Equals(request.ModuleName, null, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Request_should_pass_module_query_to_registered_service_resolver()
    {
        var module = CreateModule(new RecordingVoiceProvider());
        await module.InitializeAsync(CancellationToken.None);

        var resolver = new RecordingSessionResolver(new VoicePresenceSession(module, static (_, _) => Task.CompletedTask));
        var socket = new FakeWebSocket(WebSocketState.Open, keepOpenUntilCancelledWhenEmpty: true);
        using var app = CreateApp(resolver);
        var context = CreateHttpContext(app);
        context.Features.Set<IHttpWebSocketFeature>(new FakeHttpWebSocketFeature(socket));
        context.Request.RouteValues["actorId"] = "agent-1";
        context.Request.QueryString = new QueryString("?module=voice_presence_openai");

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
        context.RequestAborted = cts.Token;

        await GetVoiceEndpoint(app).RequestDelegate!(context);

        resolver.RequestedActorIds.ShouldContain("agent-1");
        resolver.Requests.ShouldContain(request =>
            string.Equals(request.ActorId, "agent-1", StringComparison.Ordinal) &&
            string.Equals(request.ModuleName, "voice_presence_openai", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Request_should_reject_non_websocket_requests()
    {
        var resolverCalled = false;
        using var app = CreateApp((_, _) =>
        {
            resolverCalled = true;
            return Task.FromResult<VoicePresenceSession?>(null);
        });
        var context = CreateHttpContext(app);

        await GetVoiceEndpoint(app).RequestDelegate!(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        (await ReadBodyAsync(context)).ShouldContain("WebSocket required.");
        resolverCalled.ShouldBeFalse();
    }

    [Fact]
    public async Task Request_should_reject_missing_actor_id()
    {
        using var app = CreateApp(static (_, _) => Task.FromResult<VoicePresenceSession?>(null));
        var context = CreateHttpContext(app);
        context.Features.Set<IHttpWebSocketFeature>(new FakeHttpWebSocketFeature(new FakeWebSocket(WebSocketState.Open)));

        await GetVoiceEndpoint(app).RequestDelegate!(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        (await ReadBodyAsync(context)).ShouldContain("actorId is required.");
    }

    [Fact]
    public async Task Request_should_return_not_found_when_session_missing()
    {
        using var app = CreateApp(static (_, _) => Task.FromResult<VoicePresenceSession?>(null));
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
        var module = CreateModule(new RecordingVoiceProvider());
        var session = new VoicePresenceSession(module, static (_, _) => Task.CompletedTask);
        using var app = CreateApp((_, _) => Task.FromResult<VoicePresenceSession?>(session));
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
        var module = CreateModule(new RecordingVoiceProvider());
        await module.InitializeAsync(CancellationToken.None);

        var socket = new FakeWebSocket(WebSocketState.Open, keepOpenUntilCancelledWhenEmpty: true);
        var session = new VoicePresenceSession(module, static (_, _) => Task.CompletedTask);
        using var app = CreateApp((_, _) => Task.FromResult<VoicePresenceSession?>(session));
        var context = CreateHttpContext(app);
        context.Features.Set<IHttpWebSocketFeature>(new FakeHttpWebSocketFeature(socket));
        context.Request.RouteValues["actorId"] = "agent-1";

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
        context.RequestAborted = cts.Token;

        await GetVoiceEndpoint(app).RequestDelegate!(context);

        module.IsTransportAttached.ShouldBeFalse();
        socket.CloseCalls.ShouldBe(1);
    }

    [Fact]
    public async Task Request_should_reject_second_transport_without_detaching_existing_one()
    {
        var module = CreateModule(new RecordingVoiceProvider());
        await module.InitializeAsync(CancellationToken.None);

        var existingTransport = new StubVoiceTransport();
        module.AttachTransport(existingTransport, static (_, _) => Task.CompletedTask);

        var socket = new FakeWebSocket(WebSocketState.Open);
        var session = new VoicePresenceSession(module, static (_, _) => Task.CompletedTask);
        using var app = CreateApp((_, _) => Task.FromResult<VoicePresenceSession?>(session));
        var context = CreateHttpContext(app);
        context.Features.Set<IHttpWebSocketFeature>(new FakeHttpWebSocketFeature(socket));
        context.Request.RouteValues["actorId"] = "agent-1";

        await GetVoiceEndpoint(app).RequestDelegate!(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status409Conflict);
        (await ReadBodyAsync(context)).ShouldContain("Voice transport already attached.");
        module.IsTransportAttached.ShouldBeTrue();
        existingTransport.Disposed.ShouldBeFalse();
        socket.CloseCalls.ShouldBe(0);
    }

    private static WebApplication CreateApp(
        Func<string, HttpContext, Task<VoicePresenceSession?>> resolveSession)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        var app = builder.Build();
        app.MapVoicePresenceWebSocket("/voice/{actorId}", resolveSession);
        return app;
    }

    private static WebApplication CreateApp(IVoicePresenceSessionResolver resolver)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.Services.AddSingleton(resolver);
        var app = builder.Build();
        app.MapVoicePresenceWebSocket("/voice/{actorId}");
        return app;
    }

    private static RouteEndpoint GetVoiceEndpoint(WebApplication app) =>
        ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(x => x.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(x => string.Equals(x.RoutePattern.RawText, "/voice/{actorId}", StringComparison.Ordinal));

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
}
