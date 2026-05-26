using System.Net.WebSockets;
using Aevatar.Foundation.VoicePresence.Abstractions;
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
        using var app = CreateApp(static (_, _) => Task.FromResult<VoicePresenceSession?>(null));

        var route = GetVoiceEndpoint(app);

        route.RoutePattern.RawText.ShouldBe("/voice/{actorId}");
    }

    [Fact]
    public async Task Request_should_resolve_session_from_registered_service()
    {
        var resolver = new RecordingSessionResolver(CreateSession());
        var socket = new FakeWebSocket(WebSocketState.Open, keepOpenUntilCancelledWhenEmpty: true);
        using var app = CreateApp(resolver);
        var context = CreateHttpContext(app);
        context.Features.Set<IHttpWebSocketFeature>(new FakeHttpWebSocketFeature(socket));
        context.Request.RouteValues["actorId"] = "agent-1";

        socket.CompleteReceiveClose();
        await GetVoiceEndpoint(app).RequestDelegate!(context);

        resolver.RequestedActorIds.ShouldContain("agent-1");
        resolver.Requests.ShouldContain(static request => string.Equals(request.ModuleName, null, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Request_should_pass_module_query_to_registered_service_resolver()
    {
        var resolver = new RecordingSessionResolver(CreateSession());
        var socket = new FakeWebSocket(WebSocketState.Open, keepOpenUntilCancelledWhenEmpty: true);
        using var app = CreateApp(resolver);
        var context = CreateHttpContext(app);
        context.Features.Set<IHttpWebSocketFeature>(new FakeHttpWebSocketFeature(socket));
        context.Request.RouteValues["actorId"] = "agent-1";
        context.Request.QueryString = new QueryString("?module=voice_presence_openai");

        socket.CompleteReceiveClose();
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
        var session = CreateSession(initialized: false);
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
        var socket = new FakeWebSocket(WebSocketState.Open, keepOpenUntilCancelledWhenEmpty: true);
        var attached = false;
        var session = CreateSession(
            isTransportAttached: () => attached,
            attachTransportAsync: async (transport, ct) =>
            {
                attached = true;
                await foreach (var _ in transport.ReceiveFramesAsync(ct))
                {
                }
            },
            detachTransportAsync: (_, _) =>
            {
                attached = false;
                return Task.CompletedTask;
            });
        using var app = CreateApp((_, _) => Task.FromResult<VoicePresenceSession?>(session));
        var context = CreateHttpContext(app);
        context.Features.Set<IHttpWebSocketFeature>(new FakeHttpWebSocketFeature(socket));
        context.Request.RouteValues["actorId"] = "agent-1";

        socket.CompleteReceiveClose();
        await GetVoiceEndpoint(app).RequestDelegate!(context);

        attached.ShouldBeFalse();
        socket.State.ShouldBe(WebSocketState.CloseReceived);
    }

    [Fact]
    public async Task Request_should_reject_second_transport_without_detaching_existing_one()
    {
        var socket = new FakeWebSocket(WebSocketState.Open);
        var session = CreateSession(transportAttached: true);
        using var app = CreateApp((_, _) => Task.FromResult<VoicePresenceSession?>(session));
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
        var session = new VoicePresenceSession(
            isInitialized: static () => true,
            isTransportAttached: static () => false,
            attachTransportAsync: static (_, _) => throw new InvalidOperationException("already attached"),
            detachTransportAsync: static (_, _) => Task.CompletedTask,
            pcmSampleRateHz: 24000);
        using var app = CreateApp((_, _) => Task.FromResult<VoicePresenceSession?>(session));
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
        var session = new VoicePresenceSession(
            isInitialized: static () => true,
            isTransportAttached: static () => false,
            attachTransportAsync: static (_, _) => throw new VoiceRemoteAudioTransportUnavailableException(),
            detachTransportAsync: static (_, _) => Task.CompletedTask,
            pcmSampleRateHz: 24000);
        using var app = CreateApp((_, _) => Task.FromResult<VoicePresenceSession?>(session));
        var context = CreateHttpContext(app);
        context.Features.Set<IHttpWebSocketFeature>(new RecordingHttpWebSocketFeature(socket));
        context.Request.RouteValues["actorId"] = "agent-1";

        await GetVoiceEndpoint(app).RequestDelegate!(context);

        socket.CloseCalls.ShouldBe(1);
        socket.LastCloseStatus.ShouldBe(WebSocketCloseStatus.PolicyViolation);
        socket.LastCloseDescription.ShouldBe(VoiceRemoteAudioTransportUnavailableException.Reason);
        socket.State.ShouldBe(WebSocketState.Closed);
    }

    [Fact]
    public async Task Request_should_prefer_route_module_name_over_query()
    {
        var resolver = new RecordingSessionResolver(CreateSession());
        var socket = new FakeWebSocket(WebSocketState.Open, keepOpenUntilCancelledWhenEmpty: true);
        using var app = CreateApp("/voice/{actorId}/{moduleName}", resolver);
        var context = CreateHttpContext(app);
        context.Features.Set<IHttpWebSocketFeature>(new FakeHttpWebSocketFeature(socket));
        context.Request.RouteValues["actorId"] = "agent-1";
        context.Request.RouteValues["moduleName"] = "voice_presence_openai";
        context.Request.QueryString = new QueryString("?module=voice_presence_minicpm");

        socket.CompleteReceiveClose();
        await GetVoiceEndpoint(app, "/voice/{actorId}/{moduleName}").RequestDelegate!(context);

        resolver.Requests.ShouldContain(request =>
            string.Equals(request.ModuleName, "voice_presence_openai", StringComparison.Ordinal));
    }

    private static WebApplication CreateApp(
        Func<string, HttpContext, Task<VoicePresenceSession?>> resolveSession)
    {
        return CreateApp("/voice/{actorId}", resolveSession);
    }

    private static WebApplication CreateApp(
        string pattern,
        Func<string, HttpContext, Task<VoicePresenceSession?>> resolveSession)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        var app = builder.Build();
        app.MapVoicePresenceWebSocket(pattern, resolveSession);
        return app;
    }

    private static WebApplication CreateApp(IVoicePresenceSessionResolver resolver)
    {
        return CreateApp("/voice/{actorId}", resolver);
    }

    private static WebApplication CreateApp(string pattern, IVoicePresenceSessionResolver resolver)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.Services.AddSingleton(resolver);
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

    private static VoicePresenceSession CreateSession(
        bool initialized = true,
        bool transportAttached = false,
        Func<bool>? isTransportAttached = null,
        Func<IVoiceTransport, CancellationToken, Task>? attachTransportAsync = null,
        Func<IVoiceTransport?, CancellationToken, Task>? detachTransportAsync = null) =>
        new(
            isInitialized: () => initialized,
            isTransportAttached: isTransportAttached ?? (() => transportAttached),
            attachTransportAsync: attachTransportAsync ?? DrainTransportAsync,
            detachTransportAsync: detachTransportAsync ?? ((_, _) => Task.CompletedTask),
            pcmSampleRateHz: 24000);

    private static async Task DrainTransportAsync(IVoiceTransport transport, CancellationToken ct)
    {
        await foreach (var _ in transport.ReceiveFramesAsync(ct))
        {
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

    private sealed class RecordingSessionResolver(VoicePresenceSession? session) : IVoicePresenceSessionResolver
    {
        public List<VoicePresenceSessionRequest> Requests { get; } = [];

        public List<string> RequestedActorIds { get; } = [];

        public Task<VoicePresenceSessionResolution> ResolveAsync(
            VoicePresenceSessionRequest request,
            CancellationToken ct = default)
        {
            _ = ct;
            Requests.Add(request);
            RequestedActorIds.Add(request.ActorId);
            return Task.FromResult(session == null
                ? VoicePresenceSessionResolution.PreflightFailed(VoicePresencePreflightFailureKind.NotFound)
                : VoicePresenceSessionResolution.LeaseAcceptedAttached(session));
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
