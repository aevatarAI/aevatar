using Aevatar.Mainnet.Host.Api.Responses;
using FluentAssertions;

namespace Aevatar.Hosting.Tests;

public sealed class NyxIdResponsesModelsAggregatorTests
{
    [Theory]
    [InlineData("https://nyx.example.com", "/api/v1/llm/anthropic/v1", "https://nyx.example.com/api/v1/llm/anthropic/v1/models")]
    [InlineData("https://nyx.example.com", "/api/v1/llm/anthropic/v1/", "https://nyx.example.com/api/v1/llm/anthropic/v1/models")]
    [InlineData("https://nyx.example.com/", "/api/v1/proxy/s/chrono-llm", "https://nyx.example.com/api/v1/proxy/s/chrono-llm/v1/models")]
    [InlineData("https://nyx.example.com", "/api/v1/proxy/s/chrono-llm/", "https://nyx.example.com/api/v1/proxy/s/chrono-llm/v1/models")]
    public void BuildModelsUrl_ShouldAppendModelsPathAccordingToRouteShape(string authority, string routeValue, string expected)
    {
        // GatewayProvider routes carry their own `/v1` segment; ProxyService/UserService routes
        // stop at the slug. The builder must add `/v1/models` only when missing.
        NyxIdResponsesModelsAggregator.BuildModelsUrl(authority, routeValue).Should().Be(expected);
    }
}
