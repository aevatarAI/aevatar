using System.Text;
using System.Threading.Channels;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Aevatar.Foundation.VoicePresence.Hosting;
using Aevatar.Foundation.VoicePresence.Modules;
using Aevatar.Foundation.VoicePresence.Transport;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
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
        transport.Disposed.ShouldBeFalse();
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
        var dispatched = new List<IMessage>();
        var lifetimeCompleted = Channel.CreateUnbounded<VoiceTransportLifetimeCompleted>();
        var session = CreateTrackingSession(
            module,
            selfSignals: dispatched,
            pcmSampleRateHz: 16000,
            lifetimeCompleted.Writer);
        using var app = CreateApp((_, _) => Task.FromResult<VoicePresenceSession?>(session), factory);
        var context = CreateContext(app, HttpMethods.Post, "v=0\r\noffer");
        context.Request.RouteValues["actorId"] = "agent-1";

        await GetWhipEndpoint(app, HttpMethods.Post).RequestDelegate!(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status201Created);
        context.Response.ContentType.ShouldBe("application/sdp");
        context.Response.Headers.Location.ToString().ShouldBe("/voice/webrtc/agent-1");
        (await ReadBodyAsync(context)).ShouldBe("v=0\r\nanswer");
        module.HasVolatileTransportLease.ShouldBeTrue();
        factory.Calls.Count.ShouldBe(1);
        factory.Calls[0].RemoteOfferSdp.ShouldBe("v=0\r\noffer");
        factory.Calls[0].Options.PcmSampleRateHz.ShouldBe(16000);
        transport.Disposed.ShouldBeFalse();

        completion.SetResult();
        var completed = await lifetimeCompleted.Reader.ReadAsync();
        module.HasVolatileTransportLease.ShouldBeTrue();
        transport.Disposed.ShouldBeFalse();

        await ReconcileTransportLifetimeCompletedAsync(module, completed);

        module.HasVolatileTransportLease.ShouldBeFalse();
        transport.Disposed.ShouldBeTrue();
        dispatched.OfType<VoiceTransportDetachRequested>().ShouldBeEmpty();
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
        module.HasVolatileTransportLease.ShouldBeFalse();
        transport.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task Delete_should_detach_actor_owned_attached_session()
    {
        var detachCalls = 0;
        var releaseCalls = 0;
        var session = new VoicePresenceSession(
            isInitialized: static () => true,
            isTransportAttached: static () => true,
            attachTransportAsync: static (_, _) => throw new InvalidOperationException("attach should not run"),
            detachTransportAsync: (_, _) =>
            {
                detachCalls++;
                releaseCalls++;
                return Task.CompletedTask;
            },
            pcmSampleRateHz: 24000);
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
        var session = new VoicePresenceSession(
            isInitialized: static () => true,
            isTransportAttached: static () => true,
            attachTransportAsync: (_, _) =>
            {
                attachCalls++;
                return Task.CompletedTask;
            },
            detachTransportAsync: (_, _) =>
            {
                detachCalls++;
                return Task.CompletedTask;
            },
            pcmSampleRateHz: 24000);
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
    public async Task Post_should_return_service_unavailable_and_dispose_transport_when_remote_audio_is_unavailable()
    {
        var transport = new StubVoiceTransport();
        var factory = new FakeWebRtcVoiceTransportFactory(new WebRtcVoiceTransportSession(transport, "answer", Task.CompletedTask));
        var session = new VoicePresenceSession(
            isInitialized: static () => true,
            isTransportAttached: static () => false,
            attachTransportAsync: static (_, _) => throw new VoiceRemoteAudioTransportUnavailableException(),
            detachTransportAsync: static (_, _) => Task.CompletedTask,
            pcmSampleRateHz: 24000);
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
        var dispatched = new List<IMessage>();
        var lifetimeCompleted = Channel.CreateUnbounded<VoiceTransportLifetimeCompleted>();
        var session = CreateTrackingSession(
            module,
            selfSignals: dispatched,
            lifetimeCompleted: lifetimeCompleted.Writer);
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
        module.HasVolatileTransportLease.ShouldBeTrue();
        transport2.Disposed.ShouldBeFalse();

        completion1.SetResult();
        var staleCompleted = await lifetimeCompleted.Reader.ReadAsync();

        module.HasVolatileTransportLease.ShouldBeTrue();
        transport2.Disposed.ShouldBeFalse();
        dispatched.OfType<VoiceTransportLifetimeCompleted>()
            .Count(static signal => signal.SessionId == "lease-1")
            .ShouldBe(1);
        var activeAttach = dispatched.OfType<VoiceTransportAttachRequested>()
            .Single(static signal => signal.SessionId == "lease-2");
        await ReconcileTransportLifetimeCompletedAsync(
            module,
            staleCompleted,
            activeAttach);
        module.HasVolatileTransportLease.ShouldBeTrue();
        transport2.Disposed.ShouldBeFalse();

        completion2.SetResult();
        var activeCompleted = await lifetimeCompleted.Reader.ReadAsync();
        activeCompleted.SessionId.ShouldBe("lease-2");
        await ReconcileTransportLifetimeCompletedAsync(module, activeCompleted);
        module.HasVolatileTransportLease.ShouldBeFalse();
        transport2.Disposed.ShouldBeTrue();
    }

    private static VoicePresenceSession CreateTrackingSession(
        VoicePresenceModule module,
        List<IMessage> selfSignals,
        int pcmSampleRateHz = 24000,
        ChannelWriter<VoiceTransportLifetimeCompleted>? lifetimeCompleted = null) =>
        new(
            isInitialized: () => module.IsInitialized,
            isTransportAttached: () => module.HasVolatileTransportLease,
            attachTransportAsync: (transport, ct) =>
            {
                return module.AttachTransportAsync(
                    transport,
                    (message, _) =>
                    {
                        selfSignals.Add(message);
                        if (message is VoiceTransportLifetimeCompleted completed)
                            lifetimeCompleted?.TryWrite(completed);
                        return Task.CompletedTask;
                    },
                    $"lease-{selfSignals.OfType<VoiceTransportAttachRequested>().Count() + 1}",
                    "host-1",
                    Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(5)),
                    ct);
            },
            detachTransportAsync: async (expectedTransport, _) =>
            {
                await module.DetachTransportAsync(expectedTransport);
            },
            pcmSampleRateHz,
            module,
            (message, _) =>
            {
                selfSignals.Add(message);
                if (message is VoiceTransportLifetimeCompleted completed)
                    lifetimeCompleted?.TryWrite(completed);
                return Task.CompletedTask;
            },
            attachTransportAndBuildLifetimeAsync: (transport, ct) =>
                module.AttachTransportAsync(
                    transport,
                    (message, _) =>
                    {
                        selfSignals.Add(message);
                        return Task.CompletedTask;
                    },
                    $"lease-{selfSignals.OfType<VoiceTransportAttachRequested>().Count() + 1}",
                    "host-1",
                    Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(5)),
                    ct));

    private static Task ReconcileTransportLifetimeCompletedAsync(
        VoicePresenceModule module,
        VoiceTransportLifetimeCompleted completed,
        VoiceTransportAttachRequested? activeAttach = null)
    {
        var activeSessionId = activeAttach?.SessionId ?? completed.SessionId;
        var activeOwnerId = activeAttach?.OwnerId ?? completed.OwnerId;
        var activeTransportLeaseId = activeAttach?.TransportLeaseId ?? completed.TransportLeaseId;
        var activeLeaseExpiresAt = activeAttach?.LeaseExpiresAt?.Clone() ?? completed.LeaseExpiresAt?.Clone();
        var roleAgent = new RecordingRoleAgent("voice-agent");
        roleAgent.State.VoicePresence["voice_presence"] = new VoicePresenceRuntimeState
        {
            ActiveSessionId = activeSessionId,
            ActiveLeaseOwnerId = activeOwnerId,
            LeaseExpiresAt = activeLeaseExpiresAt,
            TransportAttached = true,
            ActiveTransportLeaseId = activeTransportLeaseId,
            Status = VoicePresenceRuntimeStatus.Idle,
            CurrentResponseId = 0,
            NextResponseId = 1,
            LastDrainAckResponseId = -1,
            LastDrainAckPlayoutSequence = -1,
        };
        var ctx = new StubEventHandlerContext(roleAgent);
        return module.HandleAsync(CreateEnvelope(new VoiceModuleSignal
        {
            ModuleName = "voice_presence",
            TransportLifetimeCompleted = completed,
        }), ctx, CancellationToken.None);
    }

    private static EventEnvelope CreateEnvelope(IMessage payload) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(payload),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication("voice-agent", TopologyAudience.Self),
        };

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
        public Task<RealtimeVoiceProviderSession> ConnectAsync(
            VoiceProviderSessionKey sessionKey,
            VoiceProviderConfig config,
            Func<VoiceProviderSessionKey, VoiceProviderEvent, CancellationToken, Task> eventSink,
            CancellationToken ct)
        {
            _ = sessionKey;
            _ = config;
            _ = eventSink;
            _ = ct;
            return Task.FromResult<RealtimeVoiceProviderSession>(new NoopProviderSession());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class NoopProviderSession : RealtimeVoiceProviderSession
        {
            public override Task SendAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken ct) => Task.CompletedTask;
            public override Task SendToolResultAsync(string callId, string resultJson, CancellationToken ct) => Task.CompletedTask;
            public override Task InjectEventAsync(VoiceConversationEventInjection injection, CancellationToken ct) => Task.CompletedTask;
            public override Task CancelResponseAsync(CancellationToken ct) => Task.CompletedTask;
            public override Task UpdateSessionAsync(VoiceSessionConfig session, CancellationToken ct) => Task.CompletedTask;
            public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
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

    private sealed class StubEventHandlerContext(IAgent? agent = null) : IEventHandlerContext
    {
        public EventEnvelope InboundEnvelope { get; } = new();

        public string AgentId => "voice-agent";

        public IServiceProvider Services { get; } = new ServiceCollection().BuildServiceProvider();

        public Microsoft.Extensions.Logging.ILogger Logger { get; } = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        public IAgent Agent { get; } = agent ?? new StubAgent();

        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience audience = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            _ = evt;
            _ = audience;
            _ = ct;
            _ = options;
            return Task.CompletedTask;
        }

        public Task SendToAsync<TEvent>(
            string targetActorId,
            TEvent evt,
            CancellationToken ct = default,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            _ = targetActorId;
            _ = evt;
            _ = ct;
            _ = options;
            return Task.CompletedTask;
        }

        public Task<RuntimeCallbackLease> ScheduleSelfDurableTimeoutAsync(
            string callbackId,
            TimeSpan dueTime,
            IMessage evt,
            EventEnvelopePublishOptions? options = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<RuntimeCallbackLease> ScheduleSelfDurableTimerAsync(
            string callbackId,
            TimeSpan dueTime,
            TimeSpan period,
            IMessage evt,
            EventEnvelopePublishOptions? options = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task CancelDurableCallbackAsync(RuntimeCallbackLease lease, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubAgent : IAgent
    {
        public string Id => "voice-agent";

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> GetDescriptionAsync() => Task.FromResult(Id);

        public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() =>
            Task.FromResult<IReadOnlyList<System.Type>>([]);

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingRoleAgent(string id) : IAgent, IVoicePresenceRuntimeStateOwner
    {
        public string Id => id;

        public RecordingRoleState State { get; } = new();

        public bool TryGetVoicePresenceRuntimeState(string moduleName, out VoicePresenceRuntimeState runtimeState)
        {
            if (State.VoicePresence.TryGetValue(moduleName, out var stored))
            {
                runtimeState = stored.Clone();
                return true;
            }

            runtimeState = new VoicePresenceRuntimeState();
            return false;
        }

        public Task PersistVoicePresenceRuntimeStateAsync(
            string moduleName,
            VoicePresenceRuntimeState runtimeState,
            CancellationToken ct = default)
        {
            _ = ct;
            State.VoicePresence[moduleName] = runtimeState.Clone();
            return Task.CompletedTask;
        }

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> GetDescriptionAsync() => Task.FromResult(id);

        public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() =>
            Task.FromResult<IReadOnlyList<System.Type>>([]);

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingRoleState
    {
        public Dictionary<string, VoicePresenceRuntimeState> VoicePresence { get; } = [];
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
