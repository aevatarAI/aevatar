using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.LLMProviders.NyxId;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class NyxIdLLMProviderRoutingTests
{
    [Fact]
    public async Task ResolveRouteAsync_ShouldUseDefaultGateway_WhenNoRoutePreference()
    {
        var provider = CreateProvider();

        var route = await provider.ResolveRouteAsync(CreateRequest());

        route.RouteName.Should().Be("nyxid");
        route.Endpoint.Should().Be(new Uri("https://nyx.example.com/api/v1/llm/gateway/v1/"));
    }

    [Fact]
    public async Task ResolveRouteAsync_ShouldUseDefaultGateway_WhenRoutePreferenceIsGateway()
    {
        var provider = CreateProvider();

        var route = await provider.ResolveRouteAsync(
            CreateRequest(LLMRequestMetadataKeys.NyxIdRoutePreference, "gateway"));

        route.RouteName.Should().Be("nyxid");
        route.Endpoint.Should().Be(new Uri("https://nyx.example.com/api/v1/llm/gateway/v1/"));
    }

    [Fact]
    public async Task ResolveRouteAsync_ShouldUseDefaultGateway_WhenRoutePreferenceIsAuto()
    {
        var provider = CreateProvider();

        var route = await provider.ResolveRouteAsync(
            CreateRequest(LLMRequestMetadataKeys.NyxIdRoutePreference, "auto"));

        route.RouteName.Should().Be("nyxid");
        route.Endpoint.Should().Be(new Uri("https://nyx.example.com/api/v1/llm/gateway/v1/"));
    }

    [Fact]
    public async Task ResolveRouteAsync_ShouldRouteToServiceProxy_WhenRoutePreferenceIsServiceName()
    {
        var provider = CreateProvider();

        var route = await provider.ResolveRouteAsync(
            CreateRequest(LLMRequestMetadataKeys.NyxIdRoutePreference, "chrono-llm"));

        route.RouteName.Should().Be("/api/v1/proxy/s/chrono-llm");
        route.Endpoint.Should().Be(new Uri("https://nyx.example.com/api/v1/proxy/s/chrono-llm"));
    }

    [Fact]
    public async Task ResolveRouteAsync_ShouldResolveModelFromRequest()
    {
        var provider = CreateProvider();

        var route = await provider.ResolveRouteAsync(CreateRequest());

        route.Request.Model.Should().Be("claude-3-7-sonnet");
    }

    [Fact]
    public async Task ResolveRouteAsync_ShouldFallBackToDefaultModel_WhenRequestModelIsEmpty()
    {
        var provider = CreateProvider();

        var request = new LLMRequest
        {
            Messages = [ChatMessage.User("hi")],
            Model = null,
            Metadata = new Dictionary<string, string>
            {
                [LLMRequestMetadataKeys.NyxIdAccessToken] = "test-token",
            },
        };

        var route = await provider.ResolveRouteAsync(request);

        route.Request.Model.Should().Be("gpt-5.4");
    }

    [Fact]
    public async Task ResolveRouteAsync_ShouldUseAccessTokenFromMetadata()
    {
        var provider = CreateProvider();

        var route = await provider.ResolveRouteAsync(CreateRequest());

        route.AccessToken.Should().Be("test-token");
    }

    [Fact]
    public void ResolveRouteAsync_ShouldThrow_WhenNoAccessToken()
    {
        var provider = CreateProvider();

        var request = new LLMRequest
        {
            Messages = [ChatMessage.User("hi")],
            Model = "gpt-4o",
        };

        var act = async () => await provider.ResolveRouteAsync(request);

        act.Should().ThrowAsync<NyxIdAuthenticationRequiredException>();
    }

    [Fact]
    public async Task ResolveRouteAsync_ShouldUseModelOverrideFromMetadata()
    {
        var provider = CreateProvider();

        var request = new LLMRequest
        {
            Messages = [ChatMessage.User("hi")],
            Model = "claude-3-7-sonnet",
            Metadata = new Dictionary<string, string>
            {
                [LLMRequestMetadataKeys.NyxIdAccessToken] = "test-token",
                [LLMRequestMetadataKeys.ModelOverride] = "gpt-4-turbo",
            },
        };

        var route = await provider.ResolveRouteAsync(request);

        route.Request.Model.Should().Be("gpt-4-turbo");
    }

    [Theory]
    [InlineData("gpt-5")]
    [InlineData("gpt-5.4")]
    [InlineData("openai/gpt-5.4")]
    public async Task ResolveRouteAsync_ShouldOmitTemperature_ForGpt5Models(string model)
    {
        var provider = CreateProvider();
        var request = new LLMRequest
        {
            Messages = [ChatMessage.User("hi")],
            Model = model,
            Temperature = 0,
            Metadata = new Dictionary<string, string>
            {
                [LLMRequestMetadataKeys.NyxIdAccessToken] = "test-token",
            },
        };

        var route = await provider.ResolveRouteAsync(request);

        route.Request.Temperature.Should().BeNull();
    }

    [Fact]
    public async Task ResolveRouteAsync_ShouldKeepTemperature_ForNonGpt5Models()
    {
        var provider = CreateProvider();
        var request = new LLMRequest
        {
            Messages = [ChatMessage.User("hi")],
            Model = "gpt-4o",
            Temperature = 0.2,
            Metadata = new Dictionary<string, string>
            {
                [LLMRequestMetadataKeys.NyxIdAccessToken] = "test-token",
            },
        };

        var route = await provider.ResolveRouteAsync(request);

        route.Request.Temperature.Should().Be(0.2);
    }

    [Fact]
    public async Task ResolveRouteAsync_ShouldIgnoreAbsoluteUriInRoutePreference()
    {
        var provider = CreateProvider();

        var route = await provider.ResolveRouteAsync(
            CreateRequest(LLMRequestMetadataKeys.NyxIdRoutePreference, "https://evil.com"));

        route.RouteName.Should().Be("nyxid");
        route.Endpoint.Should().Be(new Uri("https://nyx.example.com/api/v1/llm/gateway/v1/"));
    }

    [Fact]
    public async Task ResolveRouteAsync_ShouldHandleAbsolutePathRoutePreference()
    {
        var provider = CreateProvider();

        var route = await provider.ResolveRouteAsync(
            CreateRequest(LLMRequestMetadataKeys.NyxIdRoutePreference, "/custom/path"));

        route.RouteName.Should().Be("/custom/path");
        route.Endpoint.Should().Be(new Uri("https://nyx.example.com/custom/path"));
    }

    private static NyxIdLLMProvider CreateProvider() =>
        new(
            name: "nyxid",
            defaultModel: "gpt-5.4",
            nyxEndpoint: "https://nyx.example.com/api/v1/llm/gateway/v1",
            accessTokenAccessor: static () => null);

    private static LLMRequest CreateRequest(string? metadataKey = null, string? metadataValue = null)
    {
        var metadata = new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "test-token",
        };

        if (!string.IsNullOrWhiteSpace(metadataKey) && metadataValue != null)
            metadata[metadataKey] = metadataValue;

        return new LLMRequest
        {
            Messages = [ChatMessage.User("hi")],
            Model = "claude-3-7-sonnet",
            Metadata = metadata,
        };
    }
}
