using Aevatar.Mainnet.Host.Api.Voice;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Xunit;

namespace Aevatar.ChatRouting.Voice.Integration.Tests;

// Contract markers for the embedded voice console page (precedent: ChannelsEndpointsTests).
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
    public void EmbeddedPage_PreservesConsoleSuiteMarkers()
    {
        var html = VoiceConsolePage.Html;

        html.Should().StartWith("<!doctype html>");
        html.Should().Contain("id=\"app\"");
        // OIDC uses the unified suite's shared redirect target + shared storage (one login spans all pages)
        html.Should().Contain("/auto/callback");
        html.Should().Contain("aevatar-console:nyxid:pkce");
        html.Should().Contain("Aevatar Backend Console");
    }

    // The zero-config first connect calls ONLY pre-existing audited endpoints:
    // GET chat-route-policy (pre-flight), POST nyxid-chat/conversations (create the default
    // conversation agent), PUT chat-route-policy/rules/voice-default (bind voice to it), and
    // the two GETs polled until the 202-accepted commands materialize.
    [Fact]
    public void EmbeddedPage_ProvisionsVoiceRoute_ThroughExistingAuditedEndpoints()
    {
        var html = VoiceConsolePage.Html;

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
    public void EmbeddedPage_PreflightsVoiceRoute_BeforeDialingWebSocket()
    {
        var html = VoiceConsolePage.Html;

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
    public void EmbeddedPage_MirrorsResolverSemantics()
    {
        var html = VoiceConsolePage.Html;

        html.Should().Contain("路由策略拒绝语音接入");
        html.Should().Contain("TOOL_MODE_NONE");
        html.Should().Contain("matchedPriority + 1");
        // reuse before create: an existing resolvable target short-circuits provisioning
        html.Should().Contain("provisioned:false");
        html.Should().Contain("provisioned:true");
    }
}
