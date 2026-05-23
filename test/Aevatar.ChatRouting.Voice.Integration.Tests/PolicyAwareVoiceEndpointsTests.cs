using System.Net;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Aevatar.ChatRouting.Abstractions;
using Aevatar.ChatRouting.Core;
using Aevatar.Foundation.VoicePresence;
using Aevatar.Foundation.VoicePresence.Hosting;
using Aevatar.Foundation.VoicePresence.Transport;
using Aevatar.Mainnet.Host.Api.Voice;
using Aevatar.GAgents.Scheduled;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;
using RoutingOwnerScope = Aevatar.ChatRouting.Core.OwnerScope;
using ScheduledOwnerScope = Aevatar.GAgents.Scheduled.OwnerScope;

namespace Aevatar.ChatRouting.Voice.Integration.Tests;

public sealed class PolicyAwareVoiceEndpointsTests
{
    [Fact]
    public async Task PolicyAwareVoice_DefaultRoute_ShouldAttachDefaultVoiceTarget()
    {
        var policyPort = StaticPolicyPort.For(new ChatRoutePolicySnapshot(
            ForwardToGAgent("voice-agent-default", "voice_presence_openai"),
            []));
        var catalog = new RecordingCatalogQueryPort(allowedActorIds: ["voice-agent-default"]);
        var stateTransitions = new List<VoicePresenceState>();
        var resolver = RecordingVoiceSessionResolver.Attached(
            CreateSessionWithStateMachine(stateTransitions),
            stateTransitions);
        using var app = CreatePolicyAwareApp(policyPort, catalog, resolver);
        var context = CreateVoiceContext(app, "/ws/voice?codec=pcm16&sample_rate_hz=24000");
        context.Features.Set<IHttpWebSocketFeature>(new FakeHttpWebSocketFeature(new FakeWebSocket(WebSocketState.CloseReceived)));

        await GetEndpoint(app, "/ws/voice").RequestDelegate!(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        policyPort.LastCallerScope!.NyxUserId.Should().Be("user-1");
        resolver.Requests.Should().ContainSingle(request =>
            request.ActorId == "voice-agent-default" &&
            request.ModuleName == "voice_presence_openai");
        catalog.Requests.Should().ContainSingle(request => request.AgentId == "voice-agent-default");
        stateTransitions.Should().Equal(
            VoicePresenceState.Idle,
            VoicePresenceState.UserSpeaking,
            VoicePresenceState.ResponseInProgress,
            VoicePresenceState.AudioDraining);
    }

    [Fact]
    public async Task PolicyAwareVoice_VoiceLarkRule_ShouldRouteToRuleTarget()
    {
        var policyPort = StaticPolicyPort.For(new ChatRoutePolicySnapshot(
            ForwardToGAgent("voice-agent-default"),
            [
                new ChatRouteRule
                {
                    RuleId = "lark-voice",
                    Priority = 10,
                    Match = new ChatRouteMatch
                    {
                        SourceKind = ChatSourceKind.Voice,
                        Channel = "lark",
                    },
                    Action = ForwardToGAgent("voice-agent-lark"),
                },
            ]));
        var catalog = new RecordingCatalogQueryPort(allowedActorIds: ["voice-agent-lark"]);
        var resolver = RecordingVoiceSessionResolver.Attached(CreateInitializedSession());
        using var app = CreatePolicyAwareApp(policyPort, catalog, resolver);
        var context = CreateVoiceContext(app, "/ws/voice?channel=lark&registration_scope_id=bot-1&sender_id=sender-1");
        context.Features.Set<IHttpWebSocketFeature>(new FakeHttpWebSocketFeature(new FakeWebSocket(WebSocketState.CloseReceived)));

        await GetEndpoint(app, "/ws/voice").RequestDelegate!(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        resolver.Requests.Should().ContainSingle(request => request.ActorId == "voice-agent-lark");
    }

    [Fact]
    public async Task BypassVoiceEndpoint_WithoutDevScope_ShouldReturnUnauthorized()
    {
        await using var app = CreateBypassAuthApp();
        await app.StartAsync();

        var response = await app.GetTestClient().GetAsync("/ws/voice/agent-1");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await app.StopAsync();
    }

    [Fact]
    public async Task PolicyAwareVoice_WhenCallerCannotAttach_ShouldRejectBeforeUpgrade()
    {
        var policyPort = StaticPolicyPort.For(new ChatRoutePolicySnapshot(ForwardToGAgent("other-agent"), []));
        var catalog = new RecordingCatalogQueryPort(allowedActorIds: []);
        var resolver = RecordingVoiceSessionResolver.Attached(CreateInitializedSession());
        var socket = new FakeWebSocket(WebSocketState.Open);
        using var app = CreatePolicyAwareApp(policyPort, catalog, resolver);
        var context = CreateVoiceContext(app, "/ws/voice");
        var wsFeature = new FakeHttpWebSocketFeature(socket);
        context.Features.Set<IHttpWebSocketFeature>(wsFeature);

        await GetEndpoint(app, "/ws/voice").RequestDelegate!(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        wsFeature.AcceptCalls.Should().Be(0);
        resolver.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task PolicyAwareVoice_WhenPolicyForwardsToModel_ShouldReturnNotImplementedBeforeUpgrade()
    {
        // Fix (review round 1, F1):
        //   Reviewer found no coverage for the v1 ForwardToModel boundary.
        //   This asserts ForwardToModel returns HTTP 501 before accepting the WebSocket.
        var policyPort = StaticPolicyPort.For(new ChatRoutePolicySnapshot(ForwardToModel("realtime-model"), []));
        var catalog = new RecordingCatalogQueryPort(allowedActorIds: ["voice-agent"]);
        var resolver = RecordingVoiceSessionResolver.Attached(CreateInitializedSession());
        var socket = new FakeWebSocket(WebSocketState.Open);
        using var app = CreatePolicyAwareApp(policyPort, catalog, resolver);
        var context = CreateVoiceContext(app, "/ws/voice");
        var wsFeature = new FakeHttpWebSocketFeature(socket);
        context.Features.Set<IHttpWebSocketFeature>(wsFeature);

        await GetEndpoint(app, "/ws/voice").RequestDelegate!(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status501NotImplemented);
        wsFeature.AcceptCalls.Should().Be(0);
        catalog.Requests.Should().BeEmpty();
        resolver.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task PolicyAwareVoice_WhenPolicyRejects_ShouldReturnForbiddenBeforeUpgrade()
    {
        // Fix (review round 1, F2):
        //   Reviewer found no coverage for route-policy Reject decisions.
        //   This asserts Reject returns HTTP 403 before attach checks or WebSocket accept.
        var policyPort = StaticPolicyPort.For(new ChatRoutePolicySnapshot(Reject("voice denied"), []));
        var catalog = new RecordingCatalogQueryPort(allowedActorIds: ["voice-agent"]);
        var resolver = RecordingVoiceSessionResolver.Attached(CreateInitializedSession());
        var socket = new FakeWebSocket(WebSocketState.Open);
        using var app = CreatePolicyAwareApp(policyPort, catalog, resolver);
        var context = CreateVoiceContext(app, "/ws/voice");
        var wsFeature = new FakeHttpWebSocketFeature(socket);
        context.Features.Set<IHttpWebSocketFeature>(wsFeature);

        await GetEndpoint(app, "/ws/voice").RequestDelegate!(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        wsFeature.AcceptCalls.Should().Be(0);
        catalog.Requests.Should().BeEmpty();
        resolver.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task PolicyAwareVoice_WhenSessionMissing_ShouldReturnNotFoundBeforeUpgrade()
    {
        var policyPort = StaticPolicyPort.For(new ChatRoutePolicySnapshot(ForwardToGAgent("voice-agent"), []));
        var catalog = new RecordingCatalogQueryPort(allowedActorIds: ["voice-agent"]);
        var resolver = RecordingVoiceSessionResolver.PreflightFailed(VoicePresencePreflightFailureKind.NotFound);
        var socket = new FakeWebSocket(WebSocketState.Open);
        using var app = CreatePolicyAwareApp(policyPort, catalog, resolver);
        var context = CreateVoiceContext(app, "/ws/voice");
        var wsFeature = new FakeHttpWebSocketFeature(socket);
        context.Features.Set<IHttpWebSocketFeature>(wsFeature);

        await GetEndpoint(app, "/ws/voice").RequestDelegate!(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        wsFeature.AcceptCalls.Should().Be(0);
    }

    [Fact]
    public async Task PolicyAwareVoice_WhenSessionIsNotInitialized_ShouldReturnRetryableServiceUnavailableBeforeUpgrade()
    {
        // Codex review (PR #709):
        //   The routed GAgent exists but its voice module is still warming up.
        //   Returning 404 made clients treat the policy target as permanently
        //   absent. Match the dev bypass at VoicePresenceEndpoints.MapVoicePresenceWebSocket
        //   and return 503 Service Unavailable so callers retry as the cold
        //   actor finishes initializing.
        var policyPort = StaticPolicyPort.For(new ChatRoutePolicySnapshot(ForwardToGAgent("voice-agent"), []));
        var catalog = new RecordingCatalogQueryPort(allowedActorIds: ["voice-agent"]);
        var resolver = RecordingVoiceSessionResolver.PreflightFailed(VoicePresencePreflightFailureKind.NotInitialized);
        var socket = new FakeWebSocket(WebSocketState.Open);
        using var app = CreatePolicyAwareApp(policyPort, catalog, resolver);
        var context = CreateVoiceContext(app, "/ws/voice");
        var wsFeature = new FakeHttpWebSocketFeature(socket);
        context.Features.Set<IHttpWebSocketFeature>(wsFeature);

        await GetEndpoint(app, "/ws/voice").RequestDelegate!(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        wsFeature.AcceptCalls.Should().Be(0);
        resolver.Requests.Should().ContainSingle(request => request.ActorId == "voice-agent");
    }

    [Fact]
    public async Task PolicyAwareVoice_WhenAttachFailsAfterUpgrade_ShouldCloseWithPolicyViolation()
    {
        var policyPort = StaticPolicyPort.For(new ChatRoutePolicySnapshot(ForwardToGAgent("voice-agent"), []));
        var catalog = new RecordingCatalogQueryPort(allowedActorIds: ["voice-agent"]);
        var resolver = RecordingVoiceSessionResolver.Attached(new VoicePresenceSession(
            isInitialized: static () => true,
            isTransportAttached: static () => false,
            attachTransportAsync: static (_, _) => throw new InvalidOperationException("boom"),
            detachTransportAsync: static (_, _) => Task.CompletedTask,
            pcmSampleRateHz: 24000));
        var socket = new FakeWebSocket(WebSocketState.Open);
        using var app = CreatePolicyAwareApp(policyPort, catalog, resolver);
        var context = CreateVoiceContext(app, "/ws/voice");
        context.Features.Set<IHttpWebSocketFeature>(new FakeHttpWebSocketFeature(socket));

        await GetEndpoint(app, "/ws/voice").RequestDelegate!(context);

        socket.CloseCalls.Should().ContainSingle(call => call.Status == WebSocketCloseStatus.PolicyViolation);
    }

    [Fact]
    public async Task PolicyAwareVoice_WhenRemoteAudioUnsupported_ShouldReturnServiceUnavailableBeforeUpgrade()
    {
        var policyPort = StaticPolicyPort.For(new ChatRoutePolicySnapshot(ForwardToGAgent("voice-agent"), []));
        var catalog = new RecordingCatalogQueryPort(allowedActorIds: ["voice-agent"]);
        var resolver = RecordingVoiceSessionResolver.Unsupported();
        var socket = new FakeWebSocket(WebSocketState.Open);
        using var app = CreatePolicyAwareApp(policyPort, catalog, resolver);
        var context = CreateVoiceContext(app, "/ws/voice");
        var wsFeature = new FakeHttpWebSocketFeature(socket);
        context.Features.Set<IHttpWebSocketFeature>(wsFeature);

        await GetEndpoint(app, "/ws/voice").RequestDelegate!(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        (await ReadBodyAsync(context)).Should().Be("remote_audio_transport_unavailable");
        wsFeature.AcceptCalls.Should().Be(0);
    }

    [Fact]
    public async Task PolicyAwareVoice_WhenTransportAlreadyAttached_ShouldReturnConflictBeforeUpgrade()
    {
        var policyPort = StaticPolicyPort.For(new ChatRoutePolicySnapshot(ForwardToGAgent("voice-agent"), []));
        var catalog = new RecordingCatalogQueryPort(allowedActorIds: ["voice-agent"]);
        var resolver = RecordingVoiceSessionResolver.PreflightFailed(VoicePresencePreflightFailureKind.TransportAlreadyAttached);
        var socket = new FakeWebSocket(WebSocketState.Open);
        using var app = CreatePolicyAwareApp(policyPort, catalog, resolver);
        var context = CreateVoiceContext(app, "/ws/voice");
        var wsFeature = new FakeHttpWebSocketFeature(socket);
        context.Features.Set<IHttpWebSocketFeature>(wsFeature);

        await GetEndpoint(app, "/ws/voice").RequestDelegate!(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        wsFeature.AcceptCalls.Should().Be(0);
    }

    [Fact]
    public async Task PolicyAwareVoice_WhenLeaseAcceptedPendingAttach_ShouldAttachAndTimeoutCloseWait()
    {
        var policyPort = StaticPolicyPort.For(new ChatRoutePolicySnapshot(ForwardToGAgent("voice-agent"), []));
        var catalog = new RecordingCatalogQueryPort(allowedActorIds: ["voice-agent"]);
        var attached = 0;
        var detached = 0;
        var session = new VoicePresenceSession(
            isInitialized: static () => true,
            isTransportAttached: static () => false,
            attachTransportAsync: (_, _) =>
            {
                attached++;
                return Task.CompletedTask;
            },
            detachTransportAsync: (_, _) =>
            {
                detached++;
                return Task.CompletedTask;
            },
            pcmSampleRateHz: 24000);
        var resolver = RecordingVoiceSessionResolver.PendingAttach(session);
        var socket = new FakeWebSocket(WebSocketState.Open);
        using var app = CreatePolicyAwareApp(
            policyPort,
            catalog,
            resolver,
            options => options.WebSocketCloseWaitTimeout = TimeSpan.FromMilliseconds(1));
        var context = CreateVoiceContext(app, "/ws/voice");
        var wsFeature = new FakeHttpWebSocketFeature(socket);
        context.Features.Set<IHttpWebSocketFeature>(wsFeature);

        await GetEndpoint(app, "/ws/voice").RequestDelegate!(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        wsFeature.AcceptCalls.Should().Be(1);
        attached.Should().Be(1);
        detached.Should().Be(1);
    }

    private static ChatRouteAction ForwardToGAgent(string actorId, string voiceModuleName = "") =>
        new()
        {
            ForwardToGagent = new ForwardToGAgent
            {
                ActorId = actorId,
                VoiceModuleName = voiceModuleName,
            },
        };

    private static ChatRouteAction ForwardToModel(string modelName) =>
        new()
        {
            ForwardToModel = new ForwardToModel { ModelName = modelName },
        };

    private static ChatRouteAction Reject(string reason) =>
        new()
        {
            Reject = new Reject { Reason = reason },
        };

    private static WebApplication CreatePolicyAwareApp(
        StaticPolicyPort policyPort,
        RecordingCatalogQueryPort catalog,
        RecordingVoiceSessionResolver resolver,
        Action<PolicyAwareVoiceEndpointOptions>? configureOptions = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        if (configureOptions != null)
            builder.Services.Configure(configureOptions);
        builder.Services.AddSingleton<IChatRoutePolicyQueryPort>(policyPort);
        builder.Services.AddSingleton(new ChatRouteResolver(new StaticFallbackProvider("fallback-model")));
        builder.Services.AddSingleton<IUserAgentCatalogQueryPort>(catalog);
        builder.Services.AddSingleton<IVoicePresenceSessionResolver>(resolver);
        var app = builder.Build();
        app.MapPolicyAwareVoiceEndpoint();
        return app;
    }

    private static WebApplication CreateBypassAuthApp()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication(TestAuthHandler.AuthenticationScheme)
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.AuthenticationScheme, _ => { });
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy("voice-dev", policy =>
            {
                policy.RequireAuthenticatedUser();
                policy.RequireAssertion(context =>
                    PolicyAwareVoiceEndpoints.IsVoiceDevBypassPrincipal(context.User));
            });
        });
        builder.Services.AddSingleton<IVoicePresenceSessionResolver>(
            RecordingVoiceSessionResolver.PreflightFailed(VoicePresencePreflightFailureKind.NotFound));

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapVoicePresenceWebSocket("/ws/voice/{actorId}")
            .RequireAuthorization("voice-dev");
        return app;
    }

    private static DefaultHttpContext CreateVoiceContext(WebApplication app, string uri)
    {
        var parsed = new Uri("http://localhost" + uri);
        var context = new DefaultHttpContext
        {
            RequestServices = app.Services,
            Response =
            {
                Body = new MemoryStream(),
            },
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("scope_id", "user-1")],
                authenticationType: "test")),
        };
        context.Request.Path = parsed.AbsolutePath;
        context.Request.QueryString = new QueryString(parsed.Query);
        return context;
    }

    private static async Task<string> ReadBodyAsync(DefaultHttpContext context)
    {
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }

    private static RouteEndpoint GetEndpoint(WebApplication app, string pattern) =>
        ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(static dataSource => dataSource.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(endpoint => endpoint.RoutePattern.RawText == pattern);

    private static VoicePresenceSession CreateInitializedSession() =>
        new(
            isInitialized: static () => true,
            isTransportAttached: static () => false,
            attachTransportAsync: static (_, _) => Task.CompletedTask,
            detachTransportAsync: static (_, _) => Task.CompletedTask,
            pcmSampleRateHz: 24000);

    private static VoicePresenceSession CreateSessionWithStateMachine(List<VoicePresenceState> transitions)
    {
        var stateMachine = new VoicePresenceStateMachine();
        transitions.Add(stateMachine.State);
        return new VoicePresenceSession(
            isInitialized: static () => true,
            isTransportAttached: static () => false,
            attachTransportAsync: (_, _) =>
            {
                stateMachine.OnSpeechStarted();
                transitions.Add(stateMachine.State);
                stateMachine.OnResponseStarted(1);
                transitions.Add(stateMachine.State);
                stateMachine.OnResponseDone(1);
                transitions.Add(stateMachine.State);
                return Task.CompletedTask;
            },
            detachTransportAsync: static (_, _) => Task.CompletedTask,
            pcmSampleRateHz: 24000,
            selfEventDispatcher: (_, _) => Task.CompletedTask,
            module: null);
    }

    private sealed class StaticFallbackProvider(string modelName) : IChatRouteFallbackProvider
    {
        public ChatRouteDecision GetFallbackDecision() =>
            new()
            {
                Action = new ChatRouteAction
                {
                    ForwardToModel = new ForwardToModel { ModelName = modelName },
                },
            };
    }

    private sealed class StaticPolicyPort(ChatRoutePolicySnapshot snapshot) : IChatRoutePolicyQueryPort
    {
        public RoutingOwnerScope? LastCallerScope { get; private set; }

        public static StaticPolicyPort For(ChatRoutePolicySnapshot snapshot) => new(snapshot);

        public Task<ChatRoutePolicySnapshot?> LookupForCallerAsync(RoutingOwnerScope callerScope, CancellationToken ct = default)
        {
            _ = ct;
            LastCallerScope = callerScope;
            return Task.FromResult<ChatRoutePolicySnapshot?>(snapshot);
        }
    }

    private sealed class RecordingCatalogQueryPort : IUserAgentCatalogQueryPort
    {
        private readonly HashSet<string> _allowedActorIds;

        public RecordingCatalogQueryPort(IEnumerable<string> allowedActorIds)
        {
            _allowedActorIds = allowedActorIds.ToHashSet(StringComparer.Ordinal);
        }

        public List<(string AgentId, ScheduledOwnerScope Caller)> Requests { get; } = [];

        public Task<UserAgentCatalogReadModelEntry?> GetForCallerAsync(
            string agentId,
            ScheduledOwnerScope caller,
            CancellationToken ct = default)
        {
            _ = ct;
            Requests.Add((agentId, caller));
            return Task.FromResult(_allowedActorIds.Contains(agentId)
                ? new UserAgentCatalogReadModelEntry { AgentId = agentId, OwnerScope = caller.Clone() }
                : null);
        }

        public Task<IReadOnlyList<UserAgentCatalogReadModelEntry>> QueryByCallerAsync(
            ScheduledOwnerScope caller,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<UserAgentCatalogReadModelEntry>>([]);

        public Task<long?> GetStateVersionForCallerAsync(
            string agentId,
            ScheduledOwnerScope caller,
            CancellationToken ct = default) =>
            Task.FromResult<long?>(null);
    }

    private sealed class RecordingVoiceSessionResolver : IVoicePresenceSessionResolver
    {
        private readonly VoicePresenceSessionResolution _resolution;

        private RecordingVoiceSessionResolver(
            VoicePresenceSessionResolution resolution,
            IReadOnlyList<VoicePresenceState>? stateTransitions = null)
        {
            _resolution = resolution;
            StateTransitions = stateTransitions ?? [];
        }

        public List<VoicePresenceSessionRequest> Requests { get; } = [];
        public IReadOnlyList<VoicePresenceState> StateTransitions { get; }

        public static RecordingVoiceSessionResolver Attached(
            VoicePresenceSession session,
            IReadOnlyList<VoicePresenceState>? stateTransitions = null) =>
            new(VoicePresenceSessionResolution.LeaseAcceptedAttached(session), stateTransitions);

        public static RecordingVoiceSessionResolver PendingAttach(VoicePresenceSession session) =>
            new(VoicePresenceSessionResolution.LeaseAcceptedPendingAttach(session));

        public static RecordingVoiceSessionResolver Unsupported() =>
            new(VoicePresenceSessionResolution.Unsupported());

        public static RecordingVoiceSessionResolver PreflightFailed(VoicePresencePreflightFailureKind failure) =>
            new(VoicePresenceSessionResolution.PreflightFailed(failure));

        public Task<VoicePresenceSessionResolution> ResolveAsync(
            VoicePresenceSessionRequest request,
            CancellationToken ct = default)
        {
            _ = ct;
            Requests.Add(request);
            return Task.FromResult(_resolution);
        }
    }

    private sealed class FakeHttpWebSocketFeature(FakeWebSocket socket) : IHttpWebSocketFeature
    {
        public int AcceptCalls { get; private set; }
        public bool IsWebSocketRequest => true;

        public Task<WebSocket> AcceptAsync(WebSocketAcceptContext context)
        {
            _ = context;
            AcceptCalls++;
            return Task.FromResult<WebSocket>(socket);
        }
    }

    private sealed class FakeWebSocket(WebSocketState state) : WebSocket
    {
        private WebSocketState _state = state;

        public List<(WebSocketCloseStatus Status, string? Description)> CloseCalls { get; } = [];
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;

        public override void Abort() => _state = WebSocketState.Aborted;

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CloseCalls.Add((closeStatus, statusDescription));
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _state = WebSocketState.CloseSent;
            return Task.CompletedTask;
        }

        public override void Dispose() => _state = WebSocketState.Closed;

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

    private sealed class TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string AuthenticationScheme = "Test";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            return Request.Headers.ContainsKey("x-test-auth")
                ? Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                    new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim("scope", Request.Headers["x-test-auth"].ToString())],
                        AuthenticationScheme)),
                    AuthenticationScheme)))
                : Task.FromResult(AuthenticateResult.NoResult());
        }
    }
}
