using Aevatar.Mainnet.Host.Api.Voice;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Xunit;

namespace Aevatar.ChatRouting.Voice.Integration.Tests;

// Contract markers for the voice console static asset (precedent: ChannelsEndpointsTests).
// The page keeps /ws/voice's zero-config promise CLIENT-side: the ingress fails closed (501)
// when the caller's chat route policy resolves no voice attach target (pinned in
// PolicyAwareVoiceEndpointsTests), so the Talk flow must pre-flight the policy and provision
// the default agent through the existing audited endpoints before dialing. These markers keep
// that flow from silently dropping on a page rewrite.
public sealed class VoiceConsolePageTests
{
    [Fact]
    public void MapVoiceConsoleEndpoints_RegistersPageAndCallback_AsAnonymousGet()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development",
        });

        var app = builder.Build();
        var routeBuilder = (IEndpointRouteBuilder)app;
        app.MapVoiceConsoleEndpoints();

        var endpoints = routeBuilder.DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToArray();

        foreach (var pattern in new[] { "/voice", "/voice/callback" })
        {
            var page = endpoints.Single(route => string.Equals(route.RoutePattern.RawText, pattern, StringComparison.Ordinal));
            page.Metadata.OfType<HttpMethodMetadata>().Single().HttpMethods.Should().Contain("GET");
            page.Metadata.OfType<IAllowAnonymous>().Should().NotBeEmpty("the page is gated by in-page OIDC, not server auth");
        }
    }

    [Fact]
    public void EmbeddedAsset_PreservesConsoleSuiteMarkers()
    {
        var html = ReadEmbeddedAsset();

        html.Should().StartWith("<!doctype html>");
        html.Should().Contain("id=\"app\"");
        // OIDC uses the unified suite's shared redirect target; authority/clientId/storageKey
        // arrive via host-injected configuration, never baked into the raw asset.
        html.Should().Contain("/auto/callback");
        html.Should().Contain("__BACKEND_CONSOLE_CONFIG__");
        html.Should().Contain("__VOICE_REALTIME_SERVICE_SLUG__");
        html.Should().Contain("Aevatar Backend Console");
    }

    [Fact]
    public void EmbeddedAsset_AuthorizesRealtimeResourceBeforeRouteOrMicrophoneWork()
    {
        var html = ReadEmbeddedAsset();

        html.Should().Contain(":voice-realtime:token");
        html.Should().Contain("requiredVoiceResources()");
        html.Should().Contain("beginLogin(requiredVoiceResources(),VOICE_TOKEN_PURPOSE)");
        html.Should().Contain("token.oauth_resources = requestedResources");
        html.Should().NotContain("请先连接 openai-realtime 服务",
            "service ownership and OAuth resource authorization are separate facts");

        var authorization = html.IndexOf("await resolveVoiceRealtimeToken(baselineToken)", StringComparison.Ordinal);
        var routePreflight = html.IndexOf("await ensureVoiceRoute(", StringComparison.Ordinal);
        var microphone = html.IndexOf("navigator.mediaDevices.getUserMedia", StringComparison.Ordinal);
        authorization.Should().BeGreaterThan(0);
        authorization.Should().BeLessThan(routePreflight,
            "feature authorization must complete before any route provisioning writes");
        authorization.Should().BeLessThan(microphone,
            "feature authorization must complete before the browser requests microphone access");
    }

    [Fact]
    public void EmbeddedAsset_RefreshesVoiceTokenWithItsStoredResourceGrant()
    {
        var html = ReadEmbeddedAsset();

        html.Should().Contain("const grantedResources=storedTokenResources(token)");
        html.Should().Contain("grantedResources.forEach(resource=>form.append(\"resource\",resource))");
        html.Should().Contain("setToken(refreshed,VOICE_TOKEN_PURPOSE)");
        html.Should().Contain("featureSubject!==baselineSubject",
            "a feature token from another signed-in account must never be reused");
    }

    // The zero-config first connect calls ONLY pre-existing audited endpoints:
    // GET chat-route-policy (pre-flight), POST nyxid-chat/conversations (create the default
    // conversation agent), PUT chat-route-policy/rules/voice-default (bind voice to it), and
    // the two GETs polled until the 202-accepted commands materialize.
    [Fact]
    public void EmbeddedAsset_ProvisionsVoiceRoute_ThroughExistingAuditedEndpoints()
    {
        var html = ReadEmbeddedAsset();

        html.Should().Contain("/chat-route-policy");
        html.Should().Contain("/nyxid-chat/conversations");
        html.Should().Contain("VOICE_ROUTE_RULE_ID = \"voice-default\"");

        // protobuf-JSON body of UpsertChatRouteRuleRequested — the same shape the admin
        // endpoint parses; owner_scope is server-stamped so the page must not send it.
        html.Should().Contain("defaultTargetIfUninitialized");
        html.Should().Contain("voiceAttachTarget");
        html.Should().Contain("CHAT_SOURCE_KIND_VOICE");
        html.Should().NotContain("ownerScope", "owner_scope is server-stamped from the URL scope");

        // 202-accepted honesty: both writes are dispatched commands, so the page polls the
        // readmodels (policy resolves the new actor + conversation registry lists it) before dialing.
        html.Should().Contain("等待路由与会话物化");

        // Compensation: a failed rule write must not strand the just-created conversation.
        html.Should().Contain("orphan conversation cleanup");
    }

    [Fact]
    public void EmbeddedAsset_PreflightsVoiceRoute_BeforeDialingWebSocket()
    {
        var html = ReadEmbeddedAsset();

        var preflight = html.IndexOf("await ensureVoiceRoute(", StringComparison.Ordinal);
        var dial = html.IndexOf("location.origin.replace(/^http/,\"ws\")", StringComparison.Ordinal);
        preflight.Should().BeGreaterThan(0, "startVoice must pre-flight the chat route policy");
        dial.Should().BeGreaterThan(0, "startVoice must still dial /ws/voice");
        preflight.Should().BeLessThan(dial, "the voice route must resolve (or be provisioned) before the WebSocket dial");
    }

    // The client-side resolver mirror must stay honest to ChatRouteResolver semantics:
    // an explicit Reject is respected (never provisioned over), only voice-compatible rules
    // match this page's connect input, and a matching-but-not-voice-capable rule is outranked
    // (priority + 1) rather than left shadowing the new voice-default rule.
    [Fact]
    public void EmbeddedAsset_MirrorsResolverSemantics()
    {
        var html = ReadEmbeddedAsset();

        html.Should().Contain("路由策略拒绝语音接入");
        html.Should().Contain("TOOL_MODE_NONE");
        html.Should().Contain("matchedPriority + 1");
        // reuse before create: an existing resolvable target short-circuits provisioning
        html.Should().Contain("provisioned:false");
        html.Should().Contain("provisioned:true");
    }

    [Fact]
    public void EmbeddedAsset_RetriesOnlyAJustProvisionedUnacceptedHandshakeOnce()
    {
        var html = ReadEmbeddedAsset();

        html.Should().Contain("VOICE_FIRST_CONNECT_RETRY_DELAY_MS = 1000");
        html.Should().Contain("dialVoice(token, routeTarget.provisioned ? 1 : 0)");
        html.Should().Contain(
            "retriesRemaining>0 && !sessionAccepted && ev.code===1006 && !ev.reason",
            "only the browser-hidden retryable first-connect failure may be retried");
        html.Should().Contain("dialVoice(token,retriesRemaining-1)");
        html.Should().Contain("sessionAccepted=true; onSessionAccepted",
            "an upgraded session must never be retried after its acceptance frame");

        var retryBranch = html.IndexOf("if(shouldRetry){", StringComparison.Ordinal);
        var retryReturn = html.IndexOf("return;", retryBranch, StringComparison.Ordinal);
        var audioTeardown = html.IndexOf("teardownAudio();", retryBranch, StringComparison.Ordinal);
        retryReturn.Should().BeLessThan(audioTeardown,
            "the bounded redial must reuse the already-authorized microphone and audio contexts");
    }

    [Fact]
    public void EmbeddedAsset_PrefersTypedCloseReason_AndStopCancelsPendingRetry()
    {
        var html = ReadEmbeddedAsset();
        var onError = html.IndexOf("ws.onerror =", StringComparison.Ordinal);
        var onClose = html.IndexOf("ws.onclose =", StringComparison.Ordinal);

        onError.Should().BeGreaterThan(0);
        onClose.Should().BeGreaterThan(onError);
        html[onError..onClose].Should().NotContain("vStatus(",
            "the opaque browser error event must not overwrite the typed close reason");
        html.Should().Contain("voice_provider_credential_unavailable");
        html.Should().Contain("describeVoiceSocketClose(ev.code,ev.reason||\"\")");
        html.Should().Contain("if(voice.ws!==ws) return", "stale socket callbacks must not restart a stopped session");
        html.Should().Contain("clearTimeout(voice.retryTimer)");
        html.Should().NotContain("access_token=<NyxID JWT>",
            "the bearer belongs in Sec-WebSocket-Protocol, never in a logged URL");
    }

    [Fact]
    public void EmbeddedAsset_StopsMicrophone_WhenUserStopsDuringPermissionPrompt()
    {
        var html = ReadEmbeddedAsset();
        var generationCaptured = html.IndexOf("const startGeneration=++voice.startGeneration", StringComparison.Ordinal);
        var permissionResolved = html.IndexOf("micStream = await navigator.mediaDevices.getUserMedia", StringComparison.Ordinal);
        var stoppedCheck = html.IndexOf("if(startGeneration!==voice.startGeneration || voice.status !== \"connecting\")", permissionResolved, StringComparison.Ordinal);
        var staleStreamStopped = html.IndexOf("micStream.getTracks().forEach(t=>t.stop())", stoppedCheck, StringComparison.Ordinal);
        var streamAssigned = html.IndexOf("voice.micStream = micStream", permissionResolved, StringComparison.Ordinal);
        var audioContextCreated = html.IndexOf("voice.micCtx = new", permissionResolved, StringComparison.Ordinal);
        var stopInvalidatesGeneration = html.IndexOf("voice.startGeneration++", StringComparison.Ordinal);

        generationCaptured.Should().BeGreaterThan(0);
        permissionResolved.Should().BeGreaterThan(0);
        stoppedCheck.Should().BeGreaterThan(permissionResolved);
        staleStreamStopped.Should().BeGreaterThan(stoppedCheck);
        staleStreamStopped.Should().BeLessThan(streamAssigned);
        streamAssigned.Should().BeGreaterThan(stoppedCheck,
            "a stale permission result must be stopped before it can replace the active stream");
        streamAssigned.Should().BeLessThan(audioContextCreated);
        stopInvalidatesGeneration.Should().BeGreaterThan(0,
            "stop must invalidate pending permission attempts even when a new start begins immediately");
    }

    private static string ReadEmbeddedAsset()
    {
        var assembly = typeof(PolicyAwareVoiceEndpoints).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith("Voice.voice-console.html", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded asset '{resourceName}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
