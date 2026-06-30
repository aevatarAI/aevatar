using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Xunit;
using Aevatar.GAgents.Channel.NyxIdRelay;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ChannelsEndpointsTests
{
    [Fact]
    public void MapChannels_RegistersPage_AsAnonymousGet_WithoutOwnCallback()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development",
        });

        var app = builder.Build();
        var routeBuilder = (IEndpointRouteBuilder)app;
        app.MapChannels();

        var endpoints = routeBuilder.DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToArray();

        var page = endpoints.Single(route => string.Equals(route.RoutePattern.RawText, "/channels", StringComparison.Ordinal));
        page.Metadata.OfType<HttpMethodMetadata>().Single().HttpMethods.Should().Contain("GET");
        page.Metadata.OfType<IAllowAnonymous>().Should().NotBeEmpty("the page is gated by in-page OIDC, not server auth");

        // No /channels/callback: the unified console suite shares one OIDC redirect target,
        // /auto/callback, so per-page callback routes are intentionally absent.
        endpoints.Any(route => route.RoutePattern.RawText == "/channels/callback").Should().BeFalse();
    }

    // Regression guard: the page is generated from channels.html at tools-time and embedded as a
    // const. These markers anchor the contract pieces that must not silently drop on regeneration —
    // the skill-mandated Tier-1 scope and the default-LLM reminder (the explicit product ask).
    [Fact]
    public void EmbeddedPage_PreservesContractMarkers()
    {
        var html = ReadEmbeddedHtml();

        html.Should().StartWith("<!doctype html>");
        html.Should().Contain("id=\"app\"");
        html.Should().NotContain("class=\"dock\"", "the design-review dock must be stripped from the shipped page");
        // OIDC uses the unified suite's shared redirect target + shared storage (one login spans all pages)
        html.Should().Contain("/auto/callback");
        html.Should().Contain("aevatar-console:nyxid:pkce");
        // the consolidated suite brand is fixed top-left across every page
        html.Should().Contain("Aevatar Backend Console");
        // skill Tier-1 scope (the #1 silent-bot fix), event sub, and the default LLM reminder
        html.Should().Contain("im:message.p2p_msg:readonly");
        html.Should().Contain("im.message.receive_v1");
        html.Should().Contain("chrono-llm-public");
        html.Should().Contain("gpt-5.5");
        // wired to the live facade (relative), not a mock host
        html.Should().Contain("/api/channels/registrations");
        html.Should().Contain("/api/user-config/llm");
        // honest status (no perpetual "查询中" for un-queryable bots) + admin all-accounts view
        html.Should().Contain("非本账户");
        html.Should().Contain("/api/channels/me");
        html.Should().Contain("所有账户");
    }

    private static string ReadEmbeddedHtml()
    {
        var pageType = typeof(ChannelsEndpoints).Assembly
            .GetType("Aevatar.GAgents.Channel.NyxIdRelay.ChannelsPage")
            ?? throw new InvalidOperationException("ChannelsPage type not found.");
        var field = pageType.GetField("Html", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("ChannelsPage.Html not found.");
        return (string)(field.GetValue(null) ?? throw new InvalidOperationException("ChannelsPage.Html is null."));
    }
}
