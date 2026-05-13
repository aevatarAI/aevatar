using Aevatar.Mainnet.Host.Api.Responses;
using FluentAssertions;

namespace Aevatar.Hosting.Tests;

public sealed class NyxIdResponsesModelsAggregatorTests
{
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
