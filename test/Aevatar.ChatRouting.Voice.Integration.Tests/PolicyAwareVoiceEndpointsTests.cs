using System.Net;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Aevatar.ChatRouting.Abstractions;
using Aevatar.ChatRouting.Core;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Aevatar.Foundation.VoicePresence.Hosting;
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
using RoutingOwnerScope = Aevatar.Foundation.Abstractions.OwnerScope;
using ScheduledOwnerScope = Aevatar.Foundation.Abstractions.OwnerScope;

namespace Aevatar.ChatRouting.Voice.Integration.Tests;

public sealed class PolicyAwareVoiceEndpointsTests
{
    [Fact]
    public async Task PolicyAwareVoice_WhenForwardToModelHasGAgentToolHint_ShouldReturnNotImplementedBeforeUpgrade()
    {
        var policyPort = StaticPolicyPort.For(new ChatRoutePolicySnapshot(
            GAgentToolHint("voice-agent-default"),
            []));
        var catalog = new RecordingCatalogQueryPort(allowedActorIds: ["voice-agent-default"]);
        var resolver = RecordingVoiceSessionResolver.Attached(CreateInitializedSession());
        var socket = new FakeWebSocket(WebSocketState.Open);
        using var app = CreatePolicyAwareApp(policyPort, catalog, resolver);
        var context = CreateVoiceContext(app, "/ws/voice?codec=pcm16&sample_rate_hz=24000");
        var wsFeature = new FakeHttpWebSocketFeature(socket);
        context.Features.Set<IHttpWebSocketFeature>(wsFeature);

        await GetEndpoint(app, "/ws/voice").RequestDelegate!(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status501NotImplemented);
        (await ReadBodyAsync(context)).Should().Be("Voice ForwardToModel is not supported in v1.");
        policyPort.LastCallerScope!.NyxUserId.Should().Be("user-1");
        wsFeature.AcceptCalls.Should().Be(0);
        catalog.Requests.Should().BeEmpty();
        resolver.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task PolicyAwareVoice_WhenVoiceRuleForwardToModelHasTypedAttachTarget_ShouldAttach()
    {
        var policyPort = StaticPolicyPort.For(new ChatRoutePolicySnapshot(
            ForwardToModel("fallback-model"),
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
                    Action = VoiceAttachTarget("voice-agent-lark", "voice_presence_openai"),
                },
            ]));
        var catalog = new RecordingCatalogQueryPort(allowedActorIds: ["voice-agent-lark"]);
        var resolver = RecordingVoiceSessionResolver.Attached(CreateSessionCompletingOnAttach());
        var socket = new FakeWebSocket(WebSocketState.Open);
        using var app = CreatePolicyAwareApp(policyPort, catalog, resolver);
        var context = CreateVoiceContext(app, "/ws/voice?channel=lark&registration_scope_id=bot-1&sender_id=sender-1");
        var wsFeature = new FakeHttpWebSocketFeature(socket);
        context.Features.Set<IHttpWebSocketFeature>(wsFeature);

        await GetEndpoint(app, "/ws/voice").RequestDelegate!(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        wsFeature.AcceptCalls.Should().Be(1);
        catalog.Requests.Should().BeEmpty();
        resolver.Requests.Should().ContainSingle()
            .Which.Should().Be(new VoicePresenceSessionRequest(
                "voice-agent-lark",
                "voice_presence_openai",
                VoicePresenceSessionRequestPurpose.Attach));
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
    public async Task PolicyAwareVoice_WhenPolicyForwardsToModel_ShouldReturnNotImplementedBeforeUpgrade()
    {
        // Refactor (issue1321-first): all ForwardToModel voice decisions fail closed before WebSocket accept.
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

    private static ChatRouteAction GAgentToolHint(string actorId) =>
        new()
        {
            ForwardToModel = new ForwardToModel
            {
                ToolSetRef = new ChatRouteToolSetRef { Name = "workspace.default" },
                ToolChoiceHint = new ChatRouteToolChoiceHint
                {
                    ToolName = "aevatar_invoke_gagent",
                },
            },
        };

    private static ChatRouteAction VoiceAttachTarget(string actorId, string voiceModuleName) =>
        ChatRouteActionTargets.ForwardToVoiceAttachTarget(actorId, voiceModuleName);

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

    private static VoicePresenceSession CreateSessionCompletingOnAttach() =>
        new(
            isInitialized: static () => true,
            isTransportAttached: static () => false,
            attachTransportAsync: static (transport, _) =>
            {
                if (transport is IAsyncDisposable disposable)
                    return disposable.DisposeAsync().AsTask();

                return Task.CompletedTask;
            },
            detachTransportAsync: static (_, _) => Task.CompletedTask,
            pcmSampleRateHz: 24000);

    private static VoicePresenceSession CreateSessionWithRuntimeState(List<VoicePresenceRuntimeStatus> transitions)
    {
        var runtimeState = new VoicePresenceRuntimeState
        {
            Status = VoicePresenceRuntimeStatus.Idle,
            NextResponseId = 1,
            LastDrainAckResponseId = -1,
            LastDrainAckPlayoutSequence = -1,
        };
        transitions.Add(runtimeState.Status);
        return new VoicePresenceSession(
            isInitialized: static () => true,
            isTransportAttached: static () => false,
            attachTransportAsync: (_, _) =>
            {
                runtimeState.Status = VoicePresenceRuntimeStatus.UserSpeaking;
                transitions.Add(runtimeState.Status);
                runtimeState.CurrentResponseId = 1;
                runtimeState.NextResponseId = 2;
                runtimeState.Status = VoicePresenceRuntimeStatus.ResponseInProgress;
                transitions.Add(runtimeState.Status);
                runtimeState.Status = VoicePresenceRuntimeStatus.AudioDraining;
                transitions.Add(runtimeState.Status);
                return Task.CompletedTask;
            },
            detachTransportAsync: static (_, _) => Task.CompletedTask,
            pcmSampleRateHz: 24000);
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
            IReadOnlyList<VoicePresenceRuntimeStatus>? statusTransitions = null)
        {
            _resolution = resolution;
            StatusTransitions = statusTransitions ?? [];
        }

        public List<VoicePresenceSessionRequest> Requests { get; } = [];
        public IReadOnlyList<VoicePresenceRuntimeStatus> StatusTransitions { get; }

        public static RecordingVoiceSessionResolver Attached(
            VoicePresenceSession session,
            IReadOnlyList<VoicePresenceRuntimeStatus>? statusTransitions = null) =>
            new(VoicePresenceSessionResolution.LeaseAcceptedAttached(session), statusTransitions);

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
