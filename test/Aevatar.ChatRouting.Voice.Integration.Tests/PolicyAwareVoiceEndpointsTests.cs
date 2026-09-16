using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using Aevatar.Authentication.Hosting;
using Aevatar.ChatRouting.Abstractions;
using Aevatar.ChatRouting.Core;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Aevatar.Foundation.VoicePresence.Abstractions.Sessions;
using Aevatar.Foundation.VoicePresence.Hosting;
using Aevatar.Foundation.VoicePresence.Transport;
using Aevatar.Mainnet.Host.Api.Voice;
using Aevatar.GAgents.Scheduled;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
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
    public async Task VoiceRoutes_WhenVoiceFeatureIsNotConfigured_ShouldReturn503InsteadOfDiCrash()
    {
        // Mirrors an unconfigured deployment (issue #2023): no voice provider
        // → RegisterVoicePresenceModules registered nothing → the host maps
        // the fail-closed stand-ins instead of handlers whose [FromServices]
        // dependencies would throw on every request.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        var app = builder.Build();

        PolicyAwareVoiceEndpoints.IsVoiceRealtimeConfigured(app.Services).Should().BeFalse();
        app.MapVoiceNotConfiguredEndpoints();

        foreach (var uri in new[] { "/ws/voice", "/ws/voice/voice-agent-lark", "/whip/offer?sessionId=app-session-1" })
        {
            var context = CreateVoiceContext(app, uri);
            var pattern = uri.StartsWith("/whip/offer", StringComparison.Ordinal)
                ? "/whip/offer"
                : uri == "/ws/voice"
                    ? "/ws/voice"
                    : "/ws/voice/{actorId}";
            await GetEndpoint(app, pattern).RequestDelegate!(context);

            context.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable, uri);
            (await ReadBodyAsync(context)).Should().Be("voice_not_configured", uri);
        }
    }

    [Fact]
    public void IsVoiceRealtimeConfigured_ShouldReflectRealtimeSessionRegistration()
    {
        var without = new ServiceCollection().BuildServiceProvider();
        PolicyAwareVoiceEndpoints.IsVoiceRealtimeConfigured(without).Should().BeFalse();

        var with = new ServiceCollection()
            .AddSingleton<IRealtimeSession<VoiceRealtimeSessionRequest, VoiceRealtimeSessionAccepted, VoiceRealtimeSessionStartError, VoiceRealtimeFrame, VoiceRealtimeSessionCompletion>>(
                new RecordingVoiceRealtimeSession(VoiceRealtimeSessionStartError.NotFound))
            .BuildServiceProvider();
        PolicyAwareVoiceEndpoints.IsVoiceRealtimeConfigured(with).Should().BeTrue();
    }

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
    public async Task PolicyAwareWhip_WhenVoiceRuleForwardToModelHasTypedAttachTarget_ShouldAttachAndReturnAnswerSdp()
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
        var transport = new StubVoiceTransport();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new FakeWebRtcVoiceTransportFactory(
            new WebRtcVoiceTransportSession(transport, "v=0\r\nanswer", completion.Task));
        var attachedTransports = new List<IVoiceTransport>();
        var lifetimeCompleted = new TaskCompletionSource<(
            VoicePresenceSessionLeaseHandle Handle,
            VoiceTransportLifetimeCompleted? Completed,
            string Reason)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var mediaPort = new RecordingVolatileMediaStreamPort(
            attachAsync: attached =>
            {
                attachedTransports.Add(attached);
                return Task.CompletedTask;
            },
            completeTransportLifetimeAsync: (handle, completed, reason) =>
            {
                lifetimeCompleted.TrySetResult((handle, completed, reason));
                return Task.CompletedTask;
            });
        using var app = CreatePolicyAwareApp(
            policyPort,
            new RecordingCatalogQueryPort(allowedActorIds: ["voice-agent-lark"]),
            new RecordingVoiceRealtimeSession(),
            mediaPort,
            transportFactory: factory);
        var context = CreateWhipContext(
            app,
            "/whip/offer?sessionId=app-session-1&channel=lark",
            "v=0\r\noffer");

        await GetEndpoint(app, "/whip/offer").RequestDelegate!(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status201Created);
        context.Response.ContentType.Should().Be("application/sdp");
        context.Response.Headers.Location.ToString().Should().Be("/whip/offer?sessionId=app-session-1");
        (await ReadBodyAsync(context)).Should().Be("v=0\r\nanswer");
        factory.Calls.Should().ContainSingle();
        factory.Calls[0].RemoteOfferSdp.Should().Be("v=0\r\noffer");
        factory.Calls[0].Options.PcmSampleRateHz.Should().Be(24000);
        factory.Calls[0].Options.ControlDataChannelLabel.Should().Be("vp-control");
        attachedTransports.Should().ContainSingle().Which.Should().BeSameAs(transport);
        transport.Disposed.Should().BeFalse();

        var request = sessionRequest(app).Requests.Should().ContainSingle().Which;
        request.ActorId.Should().Be("voice-agent-lark");
        request.ModuleName.Should().Be("voice_presence_openai");
        request.Purpose.Should().Be(VoiceRealtimeSessionPurpose.Attach);
        request.SessionOverrides.Should().NotBeNull();
        request.SessionOverrides!.Voice.Should().Be("verse");
        request.SessionOverrides.SampleRateHz.Should().Be(16000);
        request.SessionOverrides.TurnDetectionMode.Should().Be(VoiceTurnDetectionMode.Disabled);

        completion.SetResult();
        var cleanup = await lifetimeCompleted.Task;
        cleanup.Handle.ActorId.Should().Be("voice-agent-lark");
        cleanup.Handle.ModuleName.Should().Be("voice_presence_openai");
        cleanup.Handle.SessionId.Should().Be("session-1");
        cleanup.Completed.Should().NotBeNull();
        cleanup.Completed!.SessionId.Should().Be("session-1");
        cleanup.Completed.TransportLeaseId.Should().Be("transport-1");
        cleanup.Completed.OwnerId.Should().Be("voice-presence.host");
        cleanup.Completed.Reason.Should().Be("completed");
        cleanup.Reason.Should().Be("host_transport_completed");
        transport.Disposed.Should().BeFalse();

        static RecordingVoiceRealtimeSession sessionRequest(WebApplication app) =>
            (RecordingVoiceRealtimeSession)app.Services.GetRequiredService<
                IRealtimeSession<VoiceRealtimeSessionRequest, VoiceRealtimeSessionAccepted, VoiceRealtimeSessionStartError, VoiceRealtimeFrame, VoiceRealtimeSessionCompletion>>();
    }

    [Fact]
    public async Task PolicyAwareWhip_WhenSessionIdIsMissing_ShouldRejectBeforeRouteResolution()
    {
        var policyPort = StaticPolicyPort.For(new ChatRoutePolicySnapshot(
            VoiceAttachTarget("voice-agent-lark", "voice_presence_openai"),
            []));
        using var app = CreatePolicyAwareApp(
            policyPort,
            new RecordingCatalogQueryPort(allowedActorIds: ["voice-agent-lark"]),
            new RecordingVoiceRealtimeSession());
        var context = CreateWhipContext(app, "/whip/offer?channel=lark", "v=0\r\noffer");

        await GetEndpoint(app, "/whip/offer").RequestDelegate!(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        (await ReadBodyAsync(context)).Should().Be("sessionId is required.");
        policyPort.LastCallerScope.Should().BeNull();
    }

    [Fact]
    public async Task PolicyAwareWhip_WhenSdpOfferIsEmpty_ShouldRejectBeforeRouteResolution()
    {
        var policyPort = StaticPolicyPort.For(new ChatRoutePolicySnapshot(
            VoiceAttachTarget("voice-agent-lark", "voice_presence_openai"),
            []));
        using var app = CreatePolicyAwareApp(
            policyPort,
            new RecordingCatalogQueryPort(allowedActorIds: ["voice-agent-lark"]),
            new RecordingVoiceRealtimeSession());
        var context = CreateWhipContext(app, "/whip/offer?sessionId=app-session-1&channel=lark", "  ");

        await GetEndpoint(app, "/whip/offer").RequestDelegate!(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        (await ReadBodyAsync(context)).Should().Be("SDP offer is required.");
        policyPort.LastCallerScope.Should().BeNull();
    }

    [Fact]
    public async Task PolicyAwareWhip_WhenForwardToModelHasNoTypedVoiceTarget_ShouldFailClosedBeforeAttach()
    {
        var policyPort = StaticPolicyPort.For(new ChatRoutePolicySnapshot(ForwardToModel("realtime-model"), []));
        var session = new RecordingVoiceRealtimeSession();
        var factory = new FakeWebRtcVoiceTransportFactory(
            new WebRtcVoiceTransportSession(new StubVoiceTransport(), "answer", Task.CompletedTask));
        using var app = CreatePolicyAwareApp(
            policyPort,
            new RecordingCatalogQueryPort(allowedActorIds: ["voice-agent-lark"]),
            session,
            transportFactory: factory);
        var context = CreateWhipContext(app, "/whip/offer?sessionId=app-session-1", "v=0\r\noffer");

        await GetEndpoint(app, "/whip/offer").RequestDelegate!(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status501NotImplemented);
        (await ReadBodyAsync(context)).Should().Be("Voice ForwardToModel is not supported in v1.");
        session.Requests.Should().BeEmpty();
        factory.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task PolicyAwareWhip_WhenPolicyProjectionIsMissing_ShouldNotRematerializeInRequestPath()
    {
        var policyPort = new HealablePolicyPort();
        var recovery = new RecordingProjectionRecoveryPort(
            result: true,
            onHeal: () => policyPort.Heal(new ChatRoutePolicySnapshot(
                VoiceAttachTarget("voice-agent-lark", "voice_presence_openai"),
                [])));
        var session = new RecordingVoiceRealtimeSession();
        var factory = new FakeWebRtcVoiceTransportFactory(
            new WebRtcVoiceTransportSession(new StubVoiceTransport(), "answer", Task.CompletedTask));
        using var app = CreatePolicyAwareApp(
            policyPort,
            new RecordingCatalogQueryPort(allowedActorIds: ["voice-agent-lark"]),
            session,
            recoveryPort: recovery,
            transportFactory: factory);
        var context = CreateWhipContext(app, "/whip/offer?sessionId=app-session-1&channel=lark", "v=0\r\noffer");

        await GetEndpoint(app, "/whip/offer").RequestDelegate!(context);

        recovery.Calls.Should().Be(0);
        policyPort.Lookups.Should().Be(1);
        context.Response.StatusCode.Should().Be(StatusCodes.Status501NotImplemented);
        (await ReadBodyAsync(context)).Should().Be("Voice ForwardToModel is not supported in v1.");
        session.Requests.Should().BeEmpty();
        factory.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task PolicyAwareWhip_WhenAttachFails_ShouldReturnConflictAndDisposeTransport()
    {
        var policyPort = StaticPolicyPort.For(new ChatRoutePolicySnapshot(
            VoiceAttachTarget("voice-agent-lark", "voice_presence_openai"),
            []));
        var transport = new StubVoiceTransport();
        var factory = new FakeWebRtcVoiceTransportFactory(
            new WebRtcVoiceTransportSession(transport, "answer", Task.CompletedTask));
        var mediaPort = new RecordingVolatileMediaStreamPort(
            attachAsync: static _ => throw new VoiceTransportAlreadyAttachedException());
        using var app = CreatePolicyAwareApp(
            policyPort,
            new RecordingCatalogQueryPort(allowedActorIds: ["voice-agent-lark"]),
            new RecordingVoiceRealtimeSession(),
            mediaPort,
            transportFactory: factory);
        var context = CreateWhipContext(app, "/whip/offer?sessionId=app-session-1", "v=0\r\noffer");

        await GetEndpoint(app, "/whip/offer").RequestDelegate!(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        (await ReadBodyAsync(context)).Should().Be("Voice transport already attached.");
        transport.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task PolicyAwareWhip_WhenProviderCredentialIsUnavailable_ShouldReturn503AndDisposeTransport()
    {
        var policyPort = StaticPolicyPort.For(new ChatRoutePolicySnapshot(
            VoiceAttachTarget("voice-agent-lark", "voice_presence_openai"),
            []));
        var transport = new StubVoiceTransport();
        var factory = new FakeWebRtcVoiceTransportFactory(
            new WebRtcVoiceTransportSession(transport, "answer", Task.CompletedTask));
        var mediaPort = new RecordingVolatileMediaStreamPort(
            attachAsync: static _ => throw new RealtimeProviderCredentialException("broker response omitted secret"));
        using var app = CreatePolicyAwareApp(
            policyPort,
            new RecordingCatalogQueryPort(allowedActorIds: ["voice-agent-lark"]),
            new RecordingVoiceRealtimeSession(),
            mediaPort,
            transportFactory: factory);
        var context = CreateWhipContext(app, "/whip/offer?sessionId=app-session-1", "v=0\r\noffer");

        await GetEndpoint(app, "/whip/offer").RequestDelegate!(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        (await ReadBodyAsync(context)).Should().Be(
            VoiceWebSocketAttachExecutor.VoiceProviderCredentialUnavailableReason);
        transport.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task PolicyAwareWhip_WhenRemoteAudioUnavailable_ShouldReturn503AndDisposeTransport()
    {
        var policyPort = StaticPolicyPort.For(new ChatRoutePolicySnapshot(
            VoiceAttachTarget("voice-agent-lark", "voice_presence_openai"),
            []));
        var transport = new StubVoiceTransport();
        var factory = new FakeWebRtcVoiceTransportFactory(
            new WebRtcVoiceTransportSession(transport, "answer", Task.CompletedTask));
        var mediaPort = new RecordingVolatileMediaStreamPort(
            attachAsync: static _ => throw new VoiceVolatileMediaStreamUnavailableException());
        using var app = CreatePolicyAwareApp(
            policyPort,
            new RecordingCatalogQueryPort(allowedActorIds: ["voice-agent-lark"]),
            new RecordingVoiceRealtimeSession(),
            mediaPort,
            transportFactory: factory);
        var context = CreateWhipContext(app, "/whip/offer?sessionId=app-session-1", "v=0\r\noffer");

        await GetEndpoint(app, "/whip/offer").RequestDelegate!(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        (await ReadBodyAsync(context)).Should().Be("remote_audio_transport_unavailable");
        factory.Calls.Should().ContainSingle();
        transport.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task PolicyAwareWhip_WithQueryAccessToken_ShouldIssueToolCredentialAndBindTransport()
    {
        var policyPort = StaticPolicyPort.For(new ChatRoutePolicySnapshot(
            VoiceAttachTarget("voice-agent-lark", "voice_presence_openai"),
            []));
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(4);
        var issuer = new RecordingVoiceToolCredentialIssuer(
            new VoiceToolCredentialIssueResult(
                "voice-tool:issued-1",
                expiresAt,
                new VoiceToolCredentialTransportBinding("voice-tool:issued-1", "caller-jwt", expiresAt)));
        var transport = new StubVoiceTransport();
        var factory = new FakeWebRtcVoiceTransportFactory(
            new WebRtcVoiceTransportSession(transport, "answer", Task.CompletedTask));
        var mediaPort = new RecordingVolatileMediaStreamPort();
        var session = new RecordingVoiceRealtimeSession();
        using var app = CreatePolicyAwareApp(
            policyPort,
            new RecordingCatalogQueryPort(allowedActorIds: ["voice-agent-lark"]),
            session,
            mediaPort,
            toolCredentialIssuer: issuer,
            transportFactory: factory);
        var context = CreateWhipContext(
            app,
            "/whip/offer?sessionId=app-session-1&channel=lark&access_token=%20caller-jwt%20",
            "v=0\r\noffer");

        await GetEndpoint(app, "/whip/offer").RequestDelegate!(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status201Created);
        issuer.Requests.Should().ContainSingle()
            .Which.NyxIdAccessToken.Should().Be("caller-jwt");
        mediaPort.AttachedCredentialBindings.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new
            {
                CredentialRef = "voice-tool:issued-1",
                NyxIdAccessToken = "caller-jwt",
                ExpiresAtUtc = expiresAt,
            });
        var toolContext = session.Requests.Should().ContainSingle().Subject.ToolContext;
        toolContext.Should().NotBeNull();
        toolContext!.CredentialRef.Should().Be("voice-tool:issued-1");
        toolContext.ChannelPlatform.Should().Be("lark");
        toolContext.ToolCatalogProof.Should().NotBeNull();
        toolContext.ToolCatalogPolicyVersion.Should().Be("test-voice-policy/v1");
        issuer.ReleasedRefs.Should().BeEmpty();
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
        wsFeature.AcceptedSubprotocol.Should().BeNull("legacy clients may connect without offering a subprotocol");
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

    [Fact]
    public async Task PolicyAwareVoice_WhenProjectionStaleButRecoveryRematerializes_ShouldAttach()
    {
        // The voice-attach policy the grain still holds; its read-model row starts "missing" (idle-grain
        // staleness). The handler must self-heal via the recovery port, re-read, and attach — not 501.
        var healed = new ChatRoutePolicySnapshot(
            ForwardToModel("fallback-model"),
            [
                new ChatRouteRule
                {
                    RuleId = "lark-voice",
                    Priority = 10,
                    Match = new ChatRouteMatch { SourceKind = ChatSourceKind.Voice, Channel = "lark" },
                    Action = VoiceAttachTarget("voice-agent-lark", "voice_presence_openai"),
                },
            ]);
        var policyPort = new HealablePolicyPort();
        var recovery = new RecordingProjectionRecoveryPort(
            result: true, onHeal: () => policyPort.Heal(healed));
        var catalog = new RecordingCatalogQueryPort(allowedActorIds: ["voice-agent-lark"]);
        var session = new RecordingVoiceRealtimeSession();
        var mediaPort = new RecordingVolatileMediaStreamPort(
            attachAsync: transport => transport.DisposeAsync().AsTask(),
            detachAsync: _ => Task.CompletedTask);
        var socket = new FakeWebSocket(WebSocketState.Open);
        using var app = CreatePolicyAwareApp(policyPort, catalog, session, mediaPort, recoveryPort: recovery);
        var context = CreateVoiceContext(app, "/ws/voice?channel=lark&registration_scope_id=bot-1&sender_id=sender-1");
        var wsFeature = new FakeHttpWebSocketFeature(socket);
        context.Features.Set<IHttpWebSocketFeature>(wsFeature);

        await GetEndpoint(app, "/ws/voice").RequestDelegate!(context);

        recovery.Calls.Should().Be(1);                          // self-heal fired exactly once
        policyPort.Lookups.Should().BeGreaterThanOrEqualTo(2);  // initial null + re-read after heal
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        wsFeature.AcceptCalls.Should().Be(1);                   // attached, not 501
        session.Requests.Should().ContainSingle()
            .Which.ActorId.Should().Be("voice-agent-lark");
    }

    [Fact]
    public async Task PolicyAwareVoice_WhenBrowserOffersVoiceAndBearerProtocols_ShouldSelectOnlyVoiceProtocol()
    {
        var policyPort = StaticPolicyPort.For(new ChatRoutePolicySnapshot(
            VoiceAttachTarget("voice-agent-default", "voice_presence_openai"),
            []));
        var catalog = new RecordingCatalogQueryPort(allowedActorIds: ["voice-agent-default"]);
        var session = new RecordingVoiceRealtimeSession();
        var mediaPort = new RecordingVolatileMediaStreamPort(
            attachAsync: transport => transport.DisposeAsync().AsTask());
        var socket = new FakeWebSocket(WebSocketState.Open);
        const string callerToken = "header.payload.signature";
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(4);
        var issuer = new RecordingVoiceToolCredentialIssuer(
            new VoiceToolCredentialIssueResult(
                "voice-tool:subprotocol-test",
                expiresAt,
                new VoiceToolCredentialTransportBinding(
                    "voice-tool:subprotocol-test",
                    callerToken,
                    expiresAt)));
        using var app = CreatePolicyAwareApp(
            policyPort,
            catalog,
            session,
            mediaPort,
            toolCredentialIssuer: issuer);
        var context = CreateVoiceContext(app, "/ws/voice?codec=pcm16&mode=half_duplex");
        context.Request.Headers["Sec-WebSocket-Protocol"] = string.Join(", ",
            WebSocketSubprotocolToken.VoiceSubprotocol,
            WebSocketSubprotocolToken.BearerPrefix + callerToken);
        var wsFeature = new FakeHttpWebSocketFeature(socket);
        context.Features.Set<IHttpWebSocketFeature>(wsFeature);

        await GetEndpoint(app, "/ws/voice").RequestDelegate!(context);

        wsFeature.AcceptCalls.Should().Be(1);
        wsFeature.AcceptedSubprotocol.Should().Be(WebSocketSubprotocolToken.VoiceSubprotocol);
        wsFeature.AcceptedSubprotocol.Should().NotStartWith(WebSocketSubprotocolToken.BearerPrefix);
    }

    [Fact]
    public async Task PolicyAwareVoice_WhenProjectionMissingAndNoPolicyExists_ShouldStillReturn501()
    {
        // Genuinely-missing policy: re-materialize finds nothing, the row stays null, so the existing
        // 501 fires unchanged. Proves the self-heal does not mask a real no-voice-policy condition.
        var policyPort = new HealablePolicyPort();
        var recovery = new RecordingProjectionRecoveryPort(result: false);
        var catalog = new RecordingCatalogQueryPort(allowedActorIds: ["voice-agent-lark"]);
        var session = new RecordingVoiceRealtimeSession();
        var socket = new FakeWebSocket(WebSocketState.Open);
        using var app = CreatePolicyAwareApp(policyPort, catalog, session, recoveryPort: recovery);
        var context = CreateVoiceContext(app, "/ws/voice?codec=pcm16&sample_rate_hz=24000");
        var wsFeature = new FakeHttpWebSocketFeature(socket);
        context.Features.Set<IHttpWebSocketFeature>(wsFeature);

        await GetEndpoint(app, "/ws/voice").RequestDelegate!(context);

        recovery.Calls.Should().Be(1);                          // attempted self-heal once...
        context.Response.StatusCode.Should().Be(StatusCodes.Status501NotImplemented); // ...still fails closed
        wsFeature.AcceptCalls.Should().Be(0);
        session.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task PolicyAwareVoice_WhenProjectionFresh_ShouldNotInvokeRecovery()
    {
        // Happy path: a fresh projection returns a snapshot on the first read, so the recovery port is
        // never touched — zero added latency / behavior on the common case.
        var policyPort = StaticPolicyPort.For(new ChatRoutePolicySnapshot(
            ForwardToModel("fallback-model"),
            [
                new ChatRouteRule
                {
                    RuleId = "lark-voice",
                    Priority = 10,
                    Match = new ChatRouteMatch { SourceKind = ChatSourceKind.Voice, Channel = "lark" },
                    Action = VoiceAttachTarget("voice-agent-lark", "voice_presence_openai"),
                },
            ]));
        var recovery = new RecordingProjectionRecoveryPort(result: true);
        var catalog = new RecordingCatalogQueryPort(allowedActorIds: ["voice-agent-lark"]);
        var session = new RecordingVoiceRealtimeSession();
        var mediaPort = new RecordingVolatileMediaStreamPort(
            attachAsync: transport => transport.DisposeAsync().AsTask(),
            detachAsync: _ => Task.CompletedTask);
        var socket = new FakeWebSocket(WebSocketState.Open);
        using var app = CreatePolicyAwareApp(policyPort, catalog, session, mediaPort, recoveryPort: recovery);
        var context = CreateVoiceContext(app, "/ws/voice?channel=lark&registration_scope_id=bot-1&sender_id=sender-1");
        var wsFeature = new FakeHttpWebSocketFeature(socket);
        context.Features.Set<IHttpWebSocketFeature>(wsFeature);

        await GetEndpoint(app, "/ws/voice").RequestDelegate!(context);

        recovery.Calls.Should().Be(0);                          // recovery never invoked on the fresh path
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        wsFeature.AcceptCalls.Should().Be(1);
    }

    [Theory]
    [InlineData("authorization")]
    [InlineData("query")]
    public async Task PolicyAwareVoice_WithCallerBearer_ShouldIssueToolCredentialAndReleaseOnSessionEnd(
        string credentialSource)
    {
        var policyPort = StaticPolicyPort.For(new ChatRoutePolicySnapshot(
            VoiceAttachTarget("voice-agent-lark", "voice_presence_openai"),
            []));
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(4);
        var issuer = new RecordingVoiceToolCredentialIssuer(
            new VoiceToolCredentialIssueResult(
                "voice-tool:issued-1",
                expiresAt,
                new VoiceToolCredentialTransportBinding("voice-tool:issued-1", "caller-jwt", expiresAt)));
        var session = new RecordingVoiceRealtimeSession();
        var mediaPort = new RecordingVolatileMediaStreamPort(
            attachAsync: transport => transport.DisposeAsync().AsTask());
        var socket = new FakeWebSocket(WebSocketState.Open);
        var uri = "/ws/voice?channel=lark&registration_scope_id=bot-1&sender_id=sender-1" +
                  "&message_id=message-1&platform_message_id=platform-message-1" +
                  "&delivery_target_id=delivery-1&connected_services_context=%7B%22ok%22%3Atrue%7D" +
                  "&nyxid_route_preference=direct&sender_binding_id=binding-1";
        if (credentialSource == "query")
            uri += "&access_token=%20caller-jwt%20";

        using var app = CreatePolicyAwareApp(
            policyPort,
            new RecordingCatalogQueryPort(allowedActorIds: ["voice-agent-lark"]),
            session,
            mediaPort,
            toolCredentialIssuer: issuer);
        var context = CreateVoiceContext(app, uri);
        if (credentialSource == "authorization")
            context.Request.Headers.Authorization = "Bearer  caller-jwt  ";
        var wsFeature = new FakeHttpWebSocketFeature(socket);
        context.Features.Set<IHttpWebSocketFeature>(wsFeature);

        await GetEndpoint(app, "/ws/voice").RequestDelegate!(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        wsFeature.AcceptCalls.Should().Be(1);
        issuer.Requests.Should().ContainSingle()
            .Which.NyxIdAccessToken.Should().Be("caller-jwt");
        issuer.ReleasedRefs.Should().ContainSingle().Which.Should().Be("voice-tool:issued-1");
        mediaPort.AttachedCredentialBindings.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new
            {
                CredentialRef = "voice-tool:issued-1",
                NyxIdAccessToken = "caller-jwt",
                ExpiresAtUtc = expiresAt,
            });
        var toolContext = session.Requests.Should().ContainSingle().Subject.ToolContext;
        toolContext.Should().NotBeNull();
        toolContext!.CredentialRef.Should().Be("voice-tool:issued-1");
        toolContext.ExpiresAt.ToDateTimeOffset().Should().Be(expiresAt.ToUniversalTime());
        toolContext.CallerScopeId.Should().Be("user-1");
        toolContext.ChannelPlatform.Should().Be("lark");
        toolContext.ChannelSenderId.Should().Be("sender-1");
        toolContext.ChannelRegistrationScopeId.Should().Be("bot-1");
        toolContext.ChannelMessageId.Should().Be("message-1");
        toolContext.ChannelPlatformMessageId.Should().Be("platform-message-1");
        toolContext.ChannelDeliveryTargetId.Should().Be("delivery-1");
        toolContext.ConnectedServicesContextJson.Should().Be("""{"ok":true}""");
        toolContext.NyxIdRoutePreference.Should().Be("direct");
        toolContext.SenderBindingId.Should().Be("binding-1");
        toolContext.ToolCatalogProof.Should().NotBeNull();
        toolContext.ToolCatalogProof!.MaximumToolCount.Should().Be(6);
        toolContext.ToolCatalogPolicyVersion.Should().Be("test-voice-policy/v1");
    }

    [Fact]
    public async Task PolicyAwareVoice_WhenToolCatalogFails_ShouldReturn503BeforeUpgrade()
    {
        var policyPort = StaticPolicyPort.For(new ChatRoutePolicySnapshot(
            VoiceAttachTarget("voice-agent-lark", "voice_presence_openai"),
            []));
        var session = new RecordingVoiceRealtimeSession();
        var socket = new FakeWebSocket(WebSocketState.Open);
        using var app = CreatePolicyAwareApp(
            policyPort,
            new RecordingCatalogQueryPort(allowedActorIds: ["voice-agent-lark"]),
            session,
            toolCatalog: new RecordingVoiceToolCatalog { ThrowOnDiscover = true });
        var context = CreateVoiceContext(app, "/ws/voice?channel=lark");
        var wsFeature = new FakeHttpWebSocketFeature(socket);
        context.Features.Set<IHttpWebSocketFeature>(wsFeature);

        await GetEndpoint(app, "/ws/voice").RequestDelegate!(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        (await ReadBodyAsync(context)).Should().Be("voice_tool_catalog_unavailable");
        wsFeature.AcceptCalls.Should().Be(0);
        session.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task PolicyAwareVoice_WithCallerBearerAndMissingIssuer_ShouldReturn503BeforeUpgrade()
    {
        var policyPort = StaticPolicyPort.For(new ChatRoutePolicySnapshot(
            VoiceAttachTarget("voice-agent-lark", "voice_presence_openai"),
            []));
        var session = new RecordingVoiceRealtimeSession();
        var socket = new FakeWebSocket(WebSocketState.Open);
        using var app = CreatePolicyAwareApp(
            policyPort,
            new RecordingCatalogQueryPort(allowedActorIds: ["voice-agent-lark"]),
            session);
        var context = CreateVoiceContext(app, "/ws/voice?channel=lark");
        context.Request.Headers.Authorization = "Bearer caller-jwt";
        var wsFeature = new FakeHttpWebSocketFeature(socket);
        context.Features.Set<IHttpWebSocketFeature>(wsFeature);

        await GetEndpoint(app, "/ws/voice").RequestDelegate!(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        (await ReadBodyAsync(context)).Should().Be("voice_credential_unavailable");
        wsFeature.AcceptCalls.Should().Be(0);
        session.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("null")]
    [InlineData("throw")]
    public async Task PolicyAwareVoice_WhenIssuerFails_ShouldReturn503BeforeUpgrade(string failureCase)
    {
        var policyPort = StaticPolicyPort.For(new ChatRoutePolicySnapshot(
            VoiceAttachTarget("voice-agent-lark", "voice_presence_openai"),
            []));
        var issuer = failureCase == "throw"
            ? new RecordingVoiceToolCredentialIssuer { ThrowOnIssue = true }
            : new RecordingVoiceToolCredentialIssuer(null);
        var session = new RecordingVoiceRealtimeSession();
        var socket = new FakeWebSocket(WebSocketState.Open);
        using var app = CreatePolicyAwareApp(
            policyPort,
            new RecordingCatalogQueryPort(allowedActorIds: ["voice-agent-lark"]),
            session,
            toolCredentialIssuer: issuer);
        var context = CreateVoiceContext(app, "/ws/voice?channel=lark");
        context.Request.Headers.Authorization = "Bearer caller-jwt";
        var wsFeature = new FakeHttpWebSocketFeature(socket);
        context.Features.Set<IHttpWebSocketFeature>(wsFeature);

        await GetEndpoint(app, "/ws/voice").RequestDelegate!(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        (await ReadBodyAsync(context)).Should().Be("voice_credential_unavailable");
        wsFeature.AcceptCalls.Should().Be(0);
        session.Requests.Should().BeEmpty();
        issuer.Requests.Should().ContainSingle();
        issuer.ReleasedRefs.Should().BeEmpty();
    }

    [Fact]
    public async Task PolicyAwareVoice_WhenAttachedClientSendsInputImage_ShouldExposeImageTransportFrame()
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
        var receivedFrames = new List<VoiceTransportFrame>();
        var mediaPort = new RecordingVolatileMediaStreamPort(
            attachAsync: async transport =>
            {
                await using (transport)
                {
                    await foreach (var frame in transport.ReceiveFramesAsync(CancellationToken.None))
                        receivedFrames.Add(frame);
                }
            });
        var socket = new FakeWebSocket(WebSocketState.Open);
        socket.EnqueueReceive(
            WebSocketMessageType.Text,
            Encoding.UTF8.GetBytes(JsonFormatter.Default.Format(new VoiceControlFrame
            {
                InputImage = new VoiceInputImage
                {
                    MediaType = "image/png",
                    Data = ByteString.CopyFrom([4, 5, 6]),
                },
            })));
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
        var frame = receivedFrames.Should().ContainSingle().Which;
        frame.InputImage.Should().NotBeNull();
        frame.InputImage!.MediaType.Should().Be("image/png");
        frame.InputImage.Data.ToByteArray().Should().Equal(4, 5, 6);
    }

    [Fact]
    public async Task PolicyAwareVoice_WhenAttached_ShouldForwardRealtimeFramesToWebSocketControlChannel()
    {
        var policyPort = StaticPolicyPort.For(new ChatRoutePolicySnapshot(
            VoiceAttachTarget("voice-agent-lark", "voice_presence_openai"),
            []));
        var responseId = 51;
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
                    PlayoutSequence = 12,
                },
            })));
        socket.EnqueueReceive(
            WebSocketMessageType.Text,
            Encoding.UTF8.GetBytes(JsonFormatter.Default.Format(new VoiceControlFrame
            {
                FunctionCallOutput = new VoiceFunctionCallOutput
                {
                    CallId = "client-call-1",
                    ToolName = "edge.light.toggle",
                    OutputJson = """{"ok":true}""",
                },
            })));
        var mediaPort = new RecordingVolatileMediaStreamPort(
            attachAsync: async transport =>
            {
                await hub.PublishAsync(
                    "voice-agent-lark",
                    "session-1",
                    new VoiceRealtimeFrame
                    {
                        ModuleName = "voice_presence_openai",
                        SessionId = "session-1",
                        ResponseStarted = new VoiceResponseStarted
                        {
                            ResponseId = responseId,
                            ProviderResponseId = "provider-response-51",
                        },
                    });

                await using (transport)
                {
                    await foreach (var frame in transport.ReceiveFramesAsync(CancellationToken.None))
                    {
                        if (frame.Control != null)
                            receivedControls.Add(frame.Control.Clone());
                    }
                }
            });
        using var app = CreatePolicyAwareApp(
            policyPort,
            new RecordingCatalogQueryPort(allowedActorIds: ["voice-agent-lark"]),
            new RecordingVoiceRealtimeSession(),
            mediaPort,
            realtimeHub: hub);
        var context = CreateVoiceContext(app, "/ws/voice?channel=lark");
        var wsFeature = new FakeHttpWebSocketFeature(socket);
        context.Features.Set<IHttpWebSocketFeature>(wsFeature);

        await GetEndpoint(app, "/ws/voice").RequestDelegate!(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        wsFeature.AcceptCalls.Should().Be(1);
        socket.SentTexts.Should().HaveCount(2);

        var accepted = JsonParser.Default.Parse<VoiceControlFrame>(socket.SentTexts[0]);
        accepted.FrameCase.Should().Be(VoiceControlFrame.FrameOneofCase.SessionAccepted);
        accepted.SessionAccepted.SessionId.Should().Be("session-1");
        accepted.SessionAccepted.PcmSampleRateHz.Should().Be(24000);
        accepted.SessionAccepted.WireContractVersion.Should().Be(VoiceWireContractDefaults.CurrentWireContractVersion);
        accepted.SessionAccepted.InputImagePolicy.MaxBytes.Should().Be(VoiceWireContractDefaults.MaxInputImageBytes);
        accepted.SessionAccepted.InputImagePolicy.AllowedMediaTypes
            .Should().Equal(VoiceWireContractDefaults.SupportedInputImageMediaTypes);

        var realtime = JsonParser.Default.Parse<VoiceControlFrame>(socket.SentTexts[1]);
        realtime.FrameCase.Should().Be(VoiceControlFrame.FrameOneofCase.RealtimeFrame);
        realtime.RealtimeFrame.SessionId.Should().Be("session-1");
        realtime.RealtimeFrame.ResponseStarted.ResponseId.Should().Be(responseId);
        realtime.RealtimeFrame.ResponseStarted.ProviderResponseId.Should().Be("provider-response-51");

        receivedControls.Should().HaveCount(2);
        var ack = receivedControls[0];
        ack.FrameCase.Should().Be(VoiceControlFrame.FrameOneofCase.DrainAcknowledged);
        ack.DrainAcknowledged.ResponseId.Should().Be(realtime.RealtimeFrame.ResponseStarted.ResponseId);
        ack.DrainAcknowledged.PlayoutSequence.Should().Be(12);

        var output = receivedControls[1];
        output.FrameCase.Should().Be(VoiceControlFrame.FrameOneofCase.FunctionCallOutput);
        output.FunctionCallOutput.CallId.Should().Be("client-call-1");
        output.FunctionCallOutput.OutputJson.Should().Be("""{"ok":true}""");
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
        if (expectedStatusCode == StatusCodes.Status409Conflict)
            context.Response.Headers.RetryAfter.ToString().Should().Be("1");

        (await ReadBodyAsync(context)).Should().Be(expectedBody);
        wsFeature.AcceptCalls.Should().Be(0);
        var request = session.Requests.Should().ContainSingle().Subject;
        request.ActorId.Should().Be("voice-agent-lark");
        request.ModuleName.Should().Be("voice_presence_openai");
        request.Purpose.Should().Be(VoiceRealtimeSessionPurpose.Attach);
        request.ToolContext.Should().NotBeNull();
        request.ToolContext!.ToolCatalogProof.Should().NotBeNull();
    }

    [Fact]
    public async Task PolicyAwareVoice_WhenCapabilityProjectionNotReady_ShouldReturnTyped503BeforeUpgrade()
    {
        // The capability grain accepted the lease, but its read-model projection never caught up (and the
        // self-heal did not converge), so the realtime session reports CapabilityNotReady. The ingress
        // must return a typed, retryable 503 voice_capability_not_ready with an {error,detail} body —
        // NOT the opaque empty-body 500 the bare TimeoutException used to surface.
        var policyPort = StaticPolicyPort.For(new ChatRoutePolicySnapshot(
            VoiceAttachTarget("voice-agent-lark", "voice_presence_openai"),
            []));
        var session = new RecordingVoiceRealtimeSession(VoiceRealtimeSessionStartError.CapabilityNotReady);
        var socket = new FakeWebSocket(WebSocketState.Open);
        using var app = CreatePolicyAwareApp(
            policyPort,
            new RecordingCatalogQueryPort(allowedActorIds: ["voice-agent-lark"]),
            session);
        var context = CreateVoiceContext(app, "/ws/voice?channel=lark");
        var wsFeature = new FakeHttpWebSocketFeature(socket);
        context.Features.Set<IHttpWebSocketFeature>(wsFeature);

        await GetEndpoint(app, "/ws/voice").RequestDelegate!(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        context.Response.Headers.RetryAfter.ToString().Should().Be("1");
        var body = await ReadBodyAsync(context);
        body.Should().Contain("\"error\":\"voice_capability_not_ready\"");
        body.Should().Contain("\"detail\":");
        wsFeature.AcceptCalls.Should().Be(0);
        session.Requests.Should().ContainSingle()
            .Which.ActorId.Should().Be("voice-agent-lark");
    }

    [Theory]
    [InlineData("remote-audio-unavailable", "remote_audio_transport_unavailable")]
    [InlineData("credential-unavailable", "voice_credential_unavailable")]
    [InlineData("provider-credential-unavailable", "voice_provider_credential_unavailable")]
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
            "credential-unavailable" => new RecordingVolatileMediaStreamPort(
                attachAsync: static _ => throw new VoiceVolatileToolCredentialUnavailableException()),
            "provider-credential-unavailable" => new RecordingVolatileMediaStreamPort(
                attachAsync: static _ => throw new RealtimeProviderCredentialException("broker response omitted secret")),
            "already-attached" => new RecordingVolatileMediaStreamPort(
                attachAsync: static _ => throw new VoiceTransportAlreadyAttachedException()),
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
    public void PolicyAwareVoiceEndpoints_ShouldNotRegisterActorIdRoute()
    {
        var policyPort = StaticPolicyPort.For(new ChatRoutePolicySnapshot(
            VoiceAttachTarget("voice-agent-lark", "voice_presence_openai"),
            []));
        using var app = CreatePolicyAwareApp(
            policyPort,
            new RecordingCatalogQueryPort(allowedActorIds: ["voice-agent-lark"]),
            new RecordingVoiceRealtimeSession());

        GetEndpointPatterns(app).Should().Contain("/ws/voice");
        GetEndpointPatterns(app).Should().Contain("/whip/offer");
        GetEndpointPatterns(app).Should().NotContain("/ws/voice/{actorId}");
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
        IChatRoutePolicyQueryPort policyPort,
        RecordingCatalogQueryPort catalog,
        RecordingVoiceRealtimeSession session,
        RecordingVolatileMediaStreamPort? mediaPort = null,
        Action<VoiceWebSocketAttachOptions>? configureOptions = null,
        IProjectionSessionEventHub<VoiceRealtimeFrame>? realtimeHub = null,
        IVoiceToolCredentialIssuer? toolCredentialIssuer = null,
        IChatRoutePolicyProjectionRecoveryPort? recoveryPort = null,
        IWebRtcVoiceTransportFactory? transportFactory = null,
        IVoiceToolCatalog? toolCatalog = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        if (configureOptions != null)
            builder.Services.Configure(configureOptions);
        builder.Services.AddOptions<VoiceWebSocketAttachOptions>();
        builder.Services.AddSingleton<IValidateOptions<VoiceWebSocketAttachOptions>, VoiceWebSocketAttachOptionsValidator>();
        builder.Services.AddSingleton<VoiceWebSocketAttachExecutor>();
        builder.Services.AddSingleton<IChatRoutePolicyQueryPort>(policyPort);
        builder.Services.AddSingleton<IChatRoutePolicyProjectionRecoveryPort>(
            recoveryPort ?? new RecordingProjectionRecoveryPort());
        builder.Services.AddSingleton(new ChatRouteResolver(new StaticFallbackProvider("fallback-model")));
        builder.Services.AddSingleton<IUserAgentCatalogQueryPort>(catalog);
        builder.Services.AddSingleton<IRealtimeSession<VoiceRealtimeSessionRequest, VoiceRealtimeSessionAccepted, VoiceRealtimeSessionStartError, VoiceRealtimeFrame, VoiceRealtimeSessionCompletion>>(session);
        builder.Services.AddSingleton<IVoiceVolatileMediaStreamPort>(mediaPort ?? new RecordingVolatileMediaStreamPort());
        builder.Services.AddSingleton<IVoiceToolCatalog>(toolCatalog ?? new RecordingVoiceToolCatalog());
        if (transportFactory != null)
            builder.Services.AddSingleton(transportFactory);
        builder.Services.AddVoiceWebRtcTransport();
        if (toolCredentialIssuer != null)
            builder.Services.AddSingleton(toolCredentialIssuer);
        if (realtimeHub != null)
            builder.Services.AddSingleton(realtimeHub);
        var app = builder.Build();
        app.MapPolicyAwareVoiceEndpoint();
        app.MapPolicyAwareVoiceWhipEndpoint();
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

    private static DefaultHttpContext CreateWhipContext(WebApplication app, string uri, string body)
    {
        var context = CreateVoiceContext(app, uri);
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Method = HttpMethods.Post;
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        context.Request.ContentType = "application/sdp";
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

    private static List<string?> GetEndpointPatterns(WebApplication app) =>
        ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(static dataSource => dataSource.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(static endpoint => endpoint.RoutePattern.RawText)
            .ToList();

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

    // A query port whose read-model row is "missing" (returns null) until a recovery
    // re-materializes it via Heal(...), modelling the idle-grain projection staleness.
    private sealed class HealablePolicyPort : IChatRoutePolicyQueryPort
    {
        private ChatRoutePolicySnapshot? _snapshot;

        public int Lookups { get; private set; }
        public RoutingOwnerScope? LastCallerScope { get; private set; }

        public void Heal(ChatRoutePolicySnapshot snapshot) => _snapshot = snapshot;

        public Task<ChatRoutePolicySnapshot?> LookupForCallerAsync(RoutingOwnerScope callerScope, CancellationToken ct = default)
        {
            _ = ct;
            Lookups++;
            LastCallerScope = callerScope;
            return Task.FromResult(_snapshot);
        }
    }

    // Recovery port double. result controls TryRematerializeAsync's return; onHeal lets a test
    // simulate the projection row re-materializing (so a subsequent lookup succeeds).
    private sealed class RecordingProjectionRecoveryPort : IChatRoutePolicyProjectionRecoveryPort
    {
        private readonly bool _result;
        private readonly Action? _onHeal;

        public RecordingProjectionRecoveryPort(bool result = false, Action? onHeal = null)
        {
            _result = result;
            _onHeal = onHeal;
        }

        public int Calls { get; private set; }

        public Task<bool> TryRematerializeAsync(RoutingOwnerScope callerScope, CancellationToken ct = default)
        {
            _ = (callerScope, ct);
            Calls++;
            _onHeal?.Invoke();
            return Task.FromResult(_result);
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

        public Task<UserAgentCatalogReadModelEntry?> GetVisibleForCallerAsync(
            string agentId,
            ScheduledOwnerScope caller,
            CancellationToken ct = default) =>
            GetForCallerAsync(agentId, caller, ct);

        public Task<UserAgentCatalogReadModelEntry?> GetTriggerableForCallerAsync(
            string agentId,
            ScheduledOwnerScope caller,
            CancellationToken ct = default) =>
            GetForCallerAsync(agentId, caller, ct);

        public Task<bool> ExistsActiveAsync(string agentId, CancellationToken ct = default)
        {
            _ = ct;
            return Task.FromResult(_allowedActorIds.Contains(agentId));
        }

        public Task<IReadOnlyList<UserAgentCatalogReadModelEntry>> QueryByCallerAsync(
            ScheduledOwnerScope caller,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<UserAgentCatalogReadModelEntry>>([]);

        public Task<IReadOnlyList<UserAgentCatalogReadModelEntry>> QueryVisibleByCallerAsync(
            ScheduledOwnerScope caller,
            CancellationToken ct = default) =>
            QueryByCallerAsync(caller, ct);

        public Task<IReadOnlyList<UserAgentApiKeyRevocationReadModelEntry>> QueryPendingApiKeyRevocationsByCallerAsync(
            ScheduledOwnerScope caller,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<UserAgentApiKeyRevocationReadModelEntry>>([]);

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
                CreateLeaseHandle(inbound.ActorId, inbound.ModuleName) with
                {
                    ToolContext = inbound.ToolContext?.Clone(),
                });
            return Task.FromResult(
                RealtimeSessionResult<VoiceRealtimeSessionAccepted, VoiceRealtimeSessionStartError, VoiceRealtimeSessionCompletion>
                    .Success(accepted, VoiceRealtimeSessionCompletion.Accepted, completed: true));
        }
    }

    private sealed class RecordingVoiceToolCredentialIssuer(
        VoiceToolCredentialIssueResult? result = null) : IVoiceToolCredentialIssuer
    {
        public List<VoiceToolCredentialIssueRequest> Requests { get; } = [];
        public List<string> ReleasedRefs { get; } = [];
        public bool ThrowOnIssue { get; init; }

        public Task<VoiceToolCredentialIssueResult?> IssueAsync(
            VoiceToolCredentialIssueRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request);
            if (ThrowOnIssue)
                throw new InvalidOperationException("issuer failed");

            return Task.FromResult(result);
        }

        public Task ReleaseAsync(string credentialRef, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ReleasedRefs.Add(credentialRef);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingVoiceToolCatalog : IVoiceToolCatalog
    {
        public bool ThrowOnDiscover { get; init; }
        public List<VoiceToolExecutionContext?> Contexts { get; } = [];

        public Task<VoiceToolCatalogSnapshot> DiscoverAsync(
            VoiceToolExecutionContext? toolContext = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Contexts.Add(toolContext?.Clone());
            if (ThrowOnDiscover)
                throw new InvalidOperationException("catalog unavailable");

            return Task.FromResult(new VoiceToolCatalogSnapshot
            {
                PolicyVersion = "test-voice-policy/v1",
                Proof = new VoiceAgentTurnToolCatalogProof
                {
                    CatalogDigest = "sha256:test-empty",
                    MaximumToolCount = VoiceToolCatalogSnapshotValidator.MaximumToolCount,
                    MaximumSchemaBytes = VoiceToolCatalogSnapshotValidator.MaximumSchemaBytes,
                },
            });
        }
    }

    private sealed class RecordingVolatileMediaStreamPort(
        Func<IVoiceTransport, Task>? attachAsync = null,
        Func<IVoiceTransport?, Task>? detachAsync = null,
        Func<VoicePresenceSessionLeaseHandle, VoiceTransportLifetimeCompleted?, string, Task>? completeTransportLifetimeAsync = null)
        : IVoiceVolatileMediaStreamPort
    {
        public bool SupportsRemoteAudio => true;

        public List<VoiceToolCredentialTransportBinding?> AttachedCredentialBindings { get; } = [];

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
            CancellationToken ct = default)
            => AttachAsync(handle, transport, null, ct);

        public Task<VoiceTransportLifetimeCompleted?> AttachAsync(
            VoicePresenceSessionLeaseHandle handle,
            IVoiceTransport transport,
            VoiceToolCredentialTransportBinding? toolCredentialBinding,
            CancellationToken ct = default)
        {
            _ = handle;
            ct.ThrowIfCancellationRequested();
            AttachedCredentialBindings.Add(toolCredentialBinding);
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
            ct.ThrowIfCancellationRequested();
            return completeTransportLifetimeAsync?.Invoke(handle, completed, reason) ?? Task.CompletedTask;
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

    private sealed class StubVoiceTransport : IVoiceTransport
    {
        public bool Disposed { get; private set; }

        public Task SendAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken ct)
        {
            _ = pcm16;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task SendControlAsync(VoiceControlFrame frame, CancellationToken ct)
        {
            _ = frame;
            ct.ThrowIfCancellationRequested();
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
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeWebRtcVoiceTransportFactory(WebRtcVoiceTransportSession session)
        : IWebRtcVoiceTransportFactory
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

    private sealed class FakeHttpWebSocketFeature(FakeWebSocket socket) : IHttpWebSocketFeature
    {
        public int AcceptCalls { get; private set; }
        public string? AcceptedSubprotocol { get; private set; }
        public bool IsWebSocketRequest => true;

        public Task<WebSocket> AcceptAsync(WebSocketAcceptContext context)
        {
            AcceptCalls++;
            AcceptedSubprotocol = context.SubProtocol;
            return Task.FromResult<WebSocket>(socket);
        }
    }

    private sealed class FakeWebSocket(WebSocketState state) : WebSocket
    {
        private readonly Queue<ReceiveFrame> _frames = [];
        private WebSocketState _state = state;

        public List<(WebSocketCloseStatus Status, string? Description)> CloseCalls { get; } = [];
        public List<string> SentTexts { get; } = [];
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;

        public void EnqueueReceive(WebSocketMessageType messageType, byte[] data, bool endOfMessage = true) =>
            _frames.Enqueue(new ReceiveFrame(messageType, data, endOfMessage));

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
            cancellationToken.ThrowIfCancellationRequested();
            if (_frames.Count > 0)
            {
                var frame = _frames.Dequeue();
                _state = WebSocketState.Open;
                if (frame.Data.Length > 0 && buffer.Array != null)
                    Array.Copy(frame.Data, 0, buffer.Array, buffer.Offset, frame.Data.Length);

                return Task.FromResult(
                    new WebSocketReceiveResult(frame.Data.Length, frame.MessageType, frame.EndOfMessage));
            }

            _state = WebSocketState.CloseReceived;
            return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
        }

        public override Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            _ = endOfMessage;
            cancellationToken.ThrowIfCancellationRequested();
            if (messageType == WebSocketMessageType.Text)
                SentTexts.Add(Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, buffer.Count));

            return Task.CompletedTask;
        }

        public override ValueTask SendAsync(
            ReadOnlyMemory<byte> buffer,
            WebSocketMessageType messageType,
            WebSocketMessageFlags flags,
            CancellationToken cancellationToken)
        {
            _ = flags;
            cancellationToken.ThrowIfCancellationRequested();
            if (messageType == WebSocketMessageType.Text)
                SentTexts.Add(Encoding.UTF8.GetString(buffer.Span));

            return ValueTask.CompletedTask;
        }

        private readonly record struct ReceiveFrame(
            WebSocketMessageType MessageType,
            byte[] Data,
            bool EndOfMessage);
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

}
