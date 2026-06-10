using System.Net;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Aevatar.ChatRouting.Abstractions;
using Aevatar.ChatRouting.Core;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Aevatar.Foundation.VoicePresence.Abstractions.Sessions;
using Aevatar.Foundation.VoicePresence.Hosting;
using Aevatar.Mainnet.Host.Api.Voice;
using Aevatar.GAgents.Scheduled;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
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
        var session = new RecordingVoiceRealtimeSession();
        var socket = new FakeWebSocket(WebSocketState.Open);
        using var app = CreatePolicyAwareApp(policyPort, catalog, session);
        var context = CreateVoiceContext(app, "/ws/voice?codec=pcm16&sample_rate_hz=24000");
        var wsFeature = new FakeHttpWebSocketFeature(socket);
        context.Features.Set<IHttpWebSocketFeature>(wsFeature);

        await GetEndpoint(app, "/ws/voice").RequestDelegate!(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status501NotImplemented);
        (await ReadBodyAsync(context)).Should().Be("Voice ForwardToModel is not supported in v1.");
        policyPort.LastCallerScope!.NyxUserId.Should().Be("user-1");
        wsFeature.AcceptCalls.Should().Be(0);
        catalog.Requests.Should().BeEmpty();
        session.Requests.Should().BeEmpty();
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
                    Action = VoiceAttachTarget(
                        "voice-agent-lark",
                        "voice_presence_openai",
                        new VoiceSessionOverrides
                        {
                            Voice = "verse",
                            SampleRateHz = 16000,
                            TurnDetectionMode = VoiceTurnDetectionMode.Disabled,
                        }),
                },
            ]));
        var catalog = new RecordingCatalogQueryPort(allowedActorIds: ["voice-agent-lark"]);
        var attachedTransports = new List<IVoiceTransport>();
        var detachedTransports = new List<IVoiceTransport?>();
        var session = new RecordingVoiceRealtimeSession();
        var mediaPort = new RecordingVolatileMediaStreamPort(
            attachAsync: transport =>
            {
                attachedTransports.Add(transport);
                return transport.DisposeAsync().AsTask();
            },
            detachAsync: transport =>
            {
                detachedTransports.Add(transport);
                return Task.CompletedTask;
            });
        var socket = new FakeWebSocket(WebSocketState.Open);
        using var app = CreatePolicyAwareApp(policyPort, catalog, session, mediaPort);
        var context = CreateVoiceContext(app, "/ws/voice?channel=lark&registration_scope_id=bot-1&sender_id=sender-1");
        var wsFeature = new FakeHttpWebSocketFeature(socket);
        context.Features.Set<IHttpWebSocketFeature>(wsFeature);

        await GetEndpoint(app, "/ws/voice").RequestDelegate!(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        wsFeature.AcceptCalls.Should().Be(1);
        catalog.Requests.Should().BeEmpty();
        var request = session.Requests.Should().ContainSingle().Which;
        request.ActorId.Should().Be("voice-agent-lark");
        request.ModuleName.Should().Be("voice_presence_openai");
        request.Purpose.Should().Be(VoiceRealtimeSessionPurpose.Attach);
        request.SessionOverrides.Should().NotBeNull();
        request.SessionOverrides!.Voice.Should().Be("verse");
        request.SessionOverrides.SampleRateHz.Should().Be(16000);
        request.SessionOverrides.TurnDetectionMode.Should().Be(VoiceTurnDetectionMode.Disabled);
        attachedTransports.Should().ContainSingle();
        detachedTransports.Should().ContainSingle()
            .Which.Should().BeSameAs(attachedTransports.Single());
    }

    [Theory]
    [InlineData("unsupported", StatusCodes.Status503ServiceUnavailable, "remote_audio_transport_unavailable")]
    [InlineData("not-found", StatusCodes.Status404NotFound, "Voice session not found for this agent.")]
    [InlineData("not-initialized", StatusCodes.Status503ServiceUnavailable, "Voice module not initialized.")]
    [InlineData("transport-attached", StatusCodes.Status409Conflict, "Voice transport already attached.")]
    public async Task PolicyAwareVoice_WhenTypedAttachResolutionIsNotAccepted_ShouldReturnMappedFailureBeforeUpgrade(
        string resolutionCase,
        int expectedStatusCode,
        string expectedBody)
    {
        var policyPort = StaticPolicyPort.For(new ChatRoutePolicySnapshot(
            VoiceAttachTarget(" voice-agent-lark ", " voice_presence_openai "),
            []));
        var session = resolutionCase switch
        {
            "unsupported" => new RecordingVoiceRealtimeSession(VoiceRealtimeSessionStartError.Unsupported),
            "not-found" => new RecordingVoiceRealtimeSession(VoiceRealtimeSessionStartError.NotFound),
            "not-initialized" => new RecordingVoiceRealtimeSession(VoiceRealtimeSessionStartError.NotInitialized),
            "transport-attached" => new RecordingVoiceRealtimeSession(VoiceRealtimeSessionStartError.TransportAlreadyAttached),
            _ => throw new ArgumentOutOfRangeException(nameof(resolutionCase), resolutionCase, null),
        };
        var socket = new FakeWebSocket(WebSocketState.Open);
        using var app = CreatePolicyAwareApp(
            policyPort,
            new RecordingCatalogQueryPort(allowedActorIds: ["voice-agent-lark"]),
            session);
        var context = CreateVoiceContext(app, "/ws/voice?channel=lark");
        var wsFeature = new FakeHttpWebSocketFeature(socket);
        context.Features.Set<IHttpWebSocketFeature>(wsFeature);

        await GetEndpoint(app, "/ws/voice").RequestDelegate!(context);

        context.Response.StatusCode.Should().Be(expectedStatusCode);
        (await ReadBodyAsync(context)).Should().Be(expectedBody);
        wsFeature.AcceptCalls.Should().Be(0);
        session.Requests.Should().ContainSingle()
            .Which.Should().Be(new VoiceRealtimeSessionRequest(
                "voice-agent-lark",
                "voice_presence_openai",
                VoiceRealtimeSessionPurpose.Attach));
    }

    [Theory]
    [InlineData("remote-audio-unavailable", "remote_audio_transport_unavailable")]
    [InlineData("already-attached", "Voice transport already attached.")]
    public async Task PolicyAwareVoice_WhenAttachFailsAfterUpgrade_ShouldCloseWebSocketWithPolicyReason(
        string failureCase,
        string expectedCloseReason)
    {
        var policyPort = StaticPolicyPort.For(new ChatRoutePolicySnapshot(
            VoiceAttachTarget("voice-agent-lark", "voice_presence_openai"),
            []));
        var mediaPort = failureCase switch
        {
            "remote-audio-unavailable" => new RecordingVolatileMediaStreamPort(
                attachAsync: static _ => throw new VoiceVolatileMediaStreamUnavailableException()),
            "already-attached" => new RecordingVolatileMediaStreamPort(
                attachAsync: static _ => throw new InvalidOperationException("already attached")),
            _ => throw new ArgumentOutOfRangeException(nameof(failureCase), failureCase, null),
        };
        var socket = new FakeWebSocket(WebSocketState.Open);
        using var app = CreatePolicyAwareApp(
            policyPort,
            new RecordingCatalogQueryPort(allowedActorIds: ["voice-agent-lark"]),
            new RecordingVoiceRealtimeSession(),
            mediaPort);
        var context = CreateVoiceContext(app, "/ws/voice?channel=lark");
        var wsFeature = new FakeHttpWebSocketFeature(socket);
        context.Features.Set<IHttpWebSocketFeature>(wsFeature);

        await GetEndpoint(app, "/ws/voice").RequestDelegate!(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        wsFeature.AcceptCalls.Should().Be(1);
        socket.CloseCalls.Should().ContainSingle()
            .Which.Should().Be((WebSocketCloseStatus.PolicyViolation, expectedCloseReason));
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
        var session = new RecordingVoiceRealtimeSession();
        var socket = new FakeWebSocket(WebSocketState.Open);
        using var app = CreatePolicyAwareApp(policyPort, catalog, session);
        var context = CreateVoiceContext(app, "/ws/voice");
        var wsFeature = new FakeHttpWebSocketFeature(socket);
        context.Features.Set<IHttpWebSocketFeature>(wsFeature);

        await GetEndpoint(app, "/ws/voice").RequestDelegate!(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status501NotImplemented);
        wsFeature.AcceptCalls.Should().Be(0);
        catalog.Requests.Should().BeEmpty();
        session.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task PolicyAwareVoice_WhenForwardToModelHasOnlyPrefilledVoiceTarget_ShouldReturn501BeforeUpgrade()
    {
        var policyPort = StaticPolicyPort.For(new ChatRoutePolicySnapshot(ForwardToModelWithPrefilledVoiceTarget(), []));
        var catalog = new RecordingCatalogQueryPort(allowedActorIds: ["voice-agent-lark"]);
        var session = new RecordingVoiceRealtimeSession();
        var socket = new FakeWebSocket(WebSocketState.Open);
        using var app = CreatePolicyAwareApp(policyPort, catalog, session);
        var context = CreateVoiceContext(app, "/ws/voice");
        var wsFeature = new FakeHttpWebSocketFeature(socket);
        context.Features.Set<IHttpWebSocketFeature>(wsFeature);

        await GetEndpoint(app, "/ws/voice").RequestDelegate!(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status501NotImplemented);
        (await ReadBodyAsync(context)).Should().Be("Voice ForwardToModel is not supported in v1.");
        wsFeature.AcceptCalls.Should().Be(0);
        catalog.Requests.Should().BeEmpty();
        session.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task PolicyAwareVoice_WhenPolicyRejects_ShouldReturnForbiddenBeforeUpgrade()
    {
        var policyPort = StaticPolicyPort.For(new ChatRoutePolicySnapshot(Reject("voice denied"), []));
        var catalog = new RecordingCatalogQueryPort(allowedActorIds: ["voice-agent"]);
        var session = new RecordingVoiceRealtimeSession();
        var socket = new FakeWebSocket(WebSocketState.Open);
        using var app = CreatePolicyAwareApp(policyPort, catalog, session);
        var context = CreateVoiceContext(app, "/ws/voice");
        var wsFeature = new FakeHttpWebSocketFeature(socket);
        context.Features.Set<IHttpWebSocketFeature>(wsFeature);

        await GetEndpoint(app, "/ws/voice").RequestDelegate!(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        wsFeature.AcceptCalls.Should().Be(0);
        catalog.Requests.Should().BeEmpty();
        session.Requests.Should().BeEmpty();
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

    private static ChatRouteAction VoiceAttachTarget(
        string actorId,
        string voiceModuleName,
        VoiceSessionOverrides? overrides = null) =>
        ChatRouteActionTargets.ForwardToVoiceAttachTarget(
            actorId,
            voiceModuleName,
            sessionOverrides: overrides);

    private static ChatRouteAction ForwardToModel(string modelName) =>
        new()
        {
            ForwardToModel = new ForwardToModel { ModelName = modelName },
        };

    private static ChatRouteAction ForwardToModelWithPrefilledVoiceTarget() =>
        new()
        {
            ForwardToModel = new ForwardToModel
            {
                ToolChoiceHint = new ChatRouteToolChoiceHint
                {
                    ToolName = "aevatar_invoke_gagent",
                    PrefilledArguments = new Struct
                    {
                        Fields =
                        {
                            ["actor_id"] = Google.Protobuf.WellKnownTypes.Value.ForString("voice-agent-lark"),
                            ["voice_module_name"] = Google.Protobuf.WellKnownTypes.Value.ForString("voice_presence_openai"),
                        },
                    },
                },
            },
        };

    private static ChatRouteAction Reject(string reason) =>
        new()
        {
            Reject = new Reject { Reason = reason },
        };

    private static WebApplication CreatePolicyAwareApp(
        StaticPolicyPort policyPort,
        RecordingCatalogQueryPort catalog,
        RecordingVoiceRealtimeSession session,
        RecordingVolatileMediaStreamPort? mediaPort = null,
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
        builder.Services.AddSingleton<IRealtimeSession<VoiceRealtimeSessionRequest, VoiceRealtimeSessionAccepted, VoiceRealtimeSessionStartError, VoiceRealtimeFrame, VoiceRealtimeSessionCompletion>>(session);
        builder.Services.AddSingleton<IVoiceVolatileMediaStreamPort>(mediaPort ?? new RecordingVolatileMediaStreamPort());
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
        builder.Services.AddSingleton<IRealtimeSession<VoiceRealtimeSessionRequest, VoiceRealtimeSessionAccepted, VoiceRealtimeSessionStartError, VoiceRealtimeFrame, VoiceRealtimeSessionCompletion>>(
            new RecordingVoiceRealtimeSession(VoiceRealtimeSessionStartError.NotFound));
        builder.Services.AddSingleton<IVoiceVolatileMediaStreamPort>(new RecordingVolatileMediaStreamPort());

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

    private static VoicePresenceSessionLeaseHandle CreateLeaseHandle(string actorId, string? moduleName) =>
        new(
            actorId,
            moduleName ?? "voice_presence",
            "session-1",
            "voice-presence.host",
            42,
            DateTimeOffset.UtcNow.AddMinutes(5),
            VoiceRemoteAudioSupport.Supported,
            "transport-1");

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

    private sealed class RecordingVoiceRealtimeSession(
        VoiceRealtimeSessionStartError? failure = null)
        : IRealtimeSession<VoiceRealtimeSessionRequest, VoiceRealtimeSessionAccepted, VoiceRealtimeSessionStartError, VoiceRealtimeFrame, VoiceRealtimeSessionCompletion>
    {
        public List<VoiceRealtimeSessionRequest> Requests { get; } = [];

        public Task<RealtimeSessionResult<VoiceRealtimeSessionAccepted, VoiceRealtimeSessionStartError, VoiceRealtimeSessionCompletion>> ExecuteAsync(
            VoiceRealtimeSessionRequest inbound,
            Func<VoiceRealtimeFrame, CancellationToken, ValueTask> emitAsync,
            Func<VoiceRealtimeSessionAccepted, CancellationToken, ValueTask>? onAcceptedAsync = null,
            CancellationToken ct = default)
        {
            _ = emitAsync;
            ct.ThrowIfCancellationRequested();
            Requests.Add(inbound);
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
                CreateLeaseHandle(inbound.ActorId, inbound.ModuleName));
            return Task.FromResult(
                RealtimeSessionResult<VoiceRealtimeSessionAccepted, VoiceRealtimeSessionStartError, VoiceRealtimeSessionCompletion>
                    .Success(accepted, VoiceRealtimeSessionCompletion.Accepted, completed: true));
        }
    }

    private sealed class RecordingVolatileMediaStreamPort(
        Func<IVoiceTransport, Task>? attachAsync = null,
        Func<IVoiceTransport?, Task>? detachAsync = null)
        : IVoiceVolatileMediaStreamPort
    {
        public bool SupportsRemoteAudio => true;

        public Task<VoiceTransportLifetimeCompleted?> AttachAsync(
            VoicePresenceSessionLeaseHandle handle,
            IVoiceTransport transport,
            CancellationToken ct = default)
        {
            _ = handle;
            ct.ThrowIfCancellationRequested();
            return AttachCoreAsync(transport);
        }

        public Task DetachAsync(
            VoicePresenceSessionLeaseHandle handle,
            IVoiceTransport? expectedTransport,
            CancellationToken ct = default)
        {
            _ = handle;
            ct.ThrowIfCancellationRequested();
            return detachAsync?.Invoke(expectedTransport) ?? Task.CompletedTask;
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

        private async Task<VoiceTransportLifetimeCompleted?> AttachCoreAsync(IVoiceTransport transport)
        {
            if (attachAsync != null)
                await attachAsync(transport);

            return new VoiceTransportLifetimeCompleted
            {
                SessionId = "session-1",
                TransportLeaseId = "transport-1",
                Reason = "completed",
                OwnerId = "voice-presence.host",
            };
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
