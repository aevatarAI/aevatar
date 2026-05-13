using Aevatar.Mainnet.Host.Api.Responses;
using Aevatar.Studio.Application.Studio.Abstractions;
using FluentAssertions;

namespace Aevatar.Hosting.Tests;

public sealed class NyxIdResponsesModelsAggregatorTests
{
    [Fact]
    public void NormalizeModelsBody_ShouldPrefixEveryIdWithServiceSlug_RegardlessOfSource()
    {
        // OpenRouter-style: gateway-provider entries (anthropic, openai...) AND
        // proxy-service entries (chrono-llm...) ALL come out as `slug/model`. The
        // earlier mixed-shape design (bare for gateway, prefixed for proxy) was
        // replaced because consistent prefixing makes Stage 2's route resolution
        // uniform: every incoming `vendor/model` always goes through the
        // catalog-backed IResponsesRouteResolver, no source-dependent branching.
        var body = """{"data":[{"id":"claude-opus-4-7"},{"id":"claude-sonnet-4-6"}]}""";
        var anthropicGateway = new NyxIdLlmService(
            UserServiceId: "anthropic",
            ServiceSlug: "anthropic",
            DisplayName: "Anthropic",
            RouteValue: "/api/v1/llm/anthropic/v1",
            DefaultModel: null,
            Models: [],
            Status: "ready",
            Source: NyxIdLlmProviderSource.GatewayProvider,
            Allowed: true,
            Description: null);

        var entries = NyxIdResponsesModelsAggregator.NormalizeModelsBody(body, anthropicGateway);

        entries.Should().HaveCount(2);
        entries[0].Id.Should().Be("anthropic/claude-opus-4-7");
        entries[0].Group.Should().Be("anthropic");
        entries[0].OwnedBy.Should().Be("anthropic");
        entries[0].RouteValue.Should().Be("/api/v1/llm/anthropic/v1");
        entries[1].Id.Should().Be("anthropic/claude-sonnet-4-6");
    }

    [Fact]
    public void NormalizeModelsBody_ShouldPrefixProxyServiceEntriesToo()
    {
        var body = """{"data":[{"id":"gpt-4o"},{"id":"qwen-3"}]}""";
        var chronoLlm = new NyxIdLlmService(
            UserServiceId: "chrono-llm-id",
            ServiceSlug: "chrono-llm",
            DisplayName: "Chrono LLM",
            RouteValue: "/api/v1/proxy/s/chrono-llm",
            DefaultModel: null,
            Models: [],
            Status: "ready",
            Source: NyxIdLlmProviderSource.ProxyService,
            Allowed: true,
            Description: null);

        var entries = NyxIdResponsesModelsAggregator.NormalizeModelsBody(body, chronoLlm);

        entries.Select(e => e.Id).Should().Equal("chrono-llm/gpt-4o", "chrono-llm/qwen-3");
    }


    [Theory]
    [InlineData("https://nyx.example.com", "/api/v1/llm/anthropic/v1", "https://nyx.example.com/api/v1/llm/anthropic/v1/models")]
    [InlineData("https://nyx.example.com", "/api/v1/llm/anthropic/v1/", "https://nyx.example.com/api/v1/llm/anthropic/v1/models")]
    [InlineData("https://nyx.example.com/", "/api/v1/proxy/s/chrono-llm", "https://nyx.example.com/api/v1/proxy/s/chrono-llm/models")]
    [InlineData("https://nyx.example.com", "/api/v1/proxy/s/chrono-llm/", "https://nyx.example.com/api/v1/proxy/s/chrono-llm/models")]
    public void BuildModelsUrl_ShouldAppendModelsToRoute(string authority, string routeValue, string expected)
    {
        // GatewayProvider routes are already `/v1`-terminated; ProxyService routes terminate at
        // the slug but NyxID's DownstreamService.base_url itself already embeds `/v1` (e.g.
        // chrono-llm.base_url=https://llm.aelf.dev/v1 → `/proxy/s/chrono-llm/models` lands at
        // https://llm.aelf.dev/v1/models). Both planes correctly accept a single appended
        // `/models`; appending `/v1/models` would double the segment on the proxy plane and 404.
        NyxIdResponsesModelsAggregator.BuildModelsUrl(authority, routeValue).Should().Be(expected);
    }
}
