using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
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
            CreateRequest(routePreference: "gateway"));

        route.RouteName.Should().Be("nyxid");
        route.Endpoint.Should().Be(new Uri("https://nyx.example.com/api/v1/llm/gateway/v1/"));
    }

    [Fact]
    public async Task ResolveRouteAsync_ShouldUseDefaultGateway_WhenRoutePreferenceIsAuto()
    {
        var provider = CreateProvider();

        var route = await provider.ResolveRouteAsync(
            CreateRequest(routePreference: "auto"));

        route.RouteName.Should().Be("nyxid");
        route.Endpoint.Should().Be(new Uri("https://nyx.example.com/api/v1/llm/gateway/v1/"));
    }

    [Fact]
    public async Task ResolveRouteAsync_ShouldRouteToServiceProxy_WhenRoutePreferenceIsServiceName()
    {
        var provider = CreateProvider();

        var route = await provider.ResolveRouteAsync(
            CreateRequest(routePreference: "chrono-llm"));

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
            LlmControl = CreateControl(),
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
    public async Task ResolveRouteAsync_ShouldUseAccessTokenFromCallerContextCredentials()
    {
        var provider = CreateProvider();

        var request = new LLMRequest
        {
            Messages = [ChatMessage.User("hi")],
            Model = "claude-3-7-sonnet",
            CallerContext = new LLMRequestCallerContext(
                "scope-1",
                "owner-1",
                "resp_1",
                new LLMRequestCallerCredentials("typed-bearer")),
        };

        var route = await provider.ResolveRouteAsync(request);

        route.AccessToken.Should().Be("typed-bearer");
    }

    [Fact]
    public async Task ResolveRouteAsync_ShouldUseAccessTokenFromToolContextCredentials()
    {
        var provider = CreateProvider();

        var request = new LLMRequest
        {
            Messages = [ChatMessage.User("hi")],
            Model = "claude-3-7-sonnet",
            LlmControl = CreateControl(accessToken: "tool-context-bearer"),
        };

        var route = await provider.ResolveRouteAsync(request);

        route.AccessToken.Should().Be("tool-context-bearer");
    }

    [Fact]
    public async Task ResolveRouteAsync_ShouldPreferCallerContextCredentialsOverMetadata()
    {
        // Resolution priority: typed CallerContext.Credentials wins over the legacy
        // Metadata-keyed bearer. Locks in the migration direction set by
        // project_responses_llm_metadata_bearer_excluded.md.
        var provider = CreateProvider();

        var request = new LLMRequest
        {
            Messages = [ChatMessage.User("hi")],
            Model = "claude-3-7-sonnet",
            LlmControl = CreateControl(accessToken: "metadata-bearer"),
            CallerContext = new LLMRequestCallerContext(
                "scope-1",
                "owner-1",
                "resp_1",
                new LLMRequestCallerCredentials("typed-bearer")),
        };

        var route = await provider.ResolveRouteAsync(request);

        route.AccessToken.Should().Be("typed-bearer");
    }

    [Fact]
    public async Task ResolveRouteAsync_ShouldPreferCallerContextCredentialsOverToolContext()
    {
        var provider = CreateProvider();

        var request = new LLMRequest
        {
            Messages = [ChatMessage.User("hi")],
            Model = "claude-3-7-sonnet",
            CallerContext = new LLMRequestCallerContext(
                "scope-1",
                "owner-1",
                "resp_1",
                new LLMRequestCallerCredentials("typed-bearer")),
            ToolContext = AgentToolExecutionContext.Empty with
            {
                Credentials = new AgentToolCredentials("tool-context-bearer", null, null),
            },
        };

        var route = await provider.ResolveRouteAsync(request);

        route.AccessToken.Should().Be("typed-bearer");
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
            LlmControl = CreateControl(modelOverride: "gpt-4-turbo"),
        };

        var route = await provider.ResolveRouteAsync(request);

        route.Request.Model.Should().Be("gpt-4-turbo");
    }

    [Fact]
    public async Task ResolveRouteAsync_ShouldUseModelOverrideFromRoutingContext()
    {
        var provider = CreateProvider();

        var request = new LLMRequest
        {
            Messages = [ChatMessage.User("hi")],
            Model = "claude-3-7-sonnet",
            LlmControl = CreateControl(),
            RoutingContext = new LLMRequestRoutingContext(
                ModelOverride: "gpt-4-turbo",
                NyxIdRoutePreference: null,
                MaxToolRoundsOverride: null,
                UserMemoryPrompt: null),
        };

        var route = await provider.ResolveRouteAsync(request);

        route.Request.Model.Should().Be("gpt-4-turbo");
    }

    [Fact]
    public async Task ResolveRouteAsync_ShouldUseRoutePreferenceFromRoutingContext()
    {
        var provider = CreateProvider();

        var request = new LLMRequest
        {
            Messages = [ChatMessage.User("hi")],
            Model = "claude-3-7-sonnet",
            LlmControl = CreateControl(),
            RoutingContext = new LLMRequestRoutingContext(
                ModelOverride: null,
                NyxIdRoutePreference: "chrono-llm",
                MaxToolRoundsOverride: null,
                UserMemoryPrompt: null),
        };

        var route = await provider.ResolveRouteAsync(request);

        route.RouteName.Should().Be("/api/v1/proxy/s/chrono-llm");
        route.Endpoint.Should().Be(new Uri("https://nyx.example.com/api/v1/proxy/s/chrono-llm"));
    }

    [Theory]
    [InlineData("gpt-5")]
    [InlineData("gpt-5.4")]
    [InlineData("openai/gpt-5.4")]
    [InlineData("o1")]
    [InlineData("o1-mini")]
    [InlineData("openai/o3-mini")]
    [InlineData("o4-mini")]
    public async Task ResolveRouteAsync_ShouldOmitTemperature_ForReasoningModels(string model)
    {
        var provider = CreateProvider();
        var request = new LLMRequest
        {
            Messages = [ChatMessage.User("hi")],
            Model = model,
            Temperature = 0,
            LlmControl = CreateControl(),
        };

        var route = await provider.ResolveRouteAsync(request);

        route.Request.Temperature.Should().BeNull();
    }

    [Theory]
    [InlineData("gpt-4o")]
    [InlineData("gpt-5-chat-latest")]
    [InlineData("openai/gpt-5-chat-latest")]
    public async Task ResolveRouteAsync_ShouldKeepTemperature_ForNonReasoningModels(string model)
    {
        var provider = CreateProvider();
        var request = new LLMRequest
        {
            Messages = [ChatMessage.User("hi")],
            Model = model,
            Temperature = 0.2,
            LlmControl = CreateControl(),
        };

        var route = await provider.ResolveRouteAsync(request);

        route.Request.Temperature.Should().Be(0.2);
    }

    [Fact]
    public async Task ResolveRouteAsync_ShouldIgnoreAbsoluteUriInRoutePreference()
    {
        var provider = CreateProvider();

        var route = await provider.ResolveRouteAsync(
            CreateRequest(routePreference: "https://evil.com"));

        route.RouteName.Should().Be("nyxid");
        route.Endpoint.Should().Be(new Uri("https://nyx.example.com/api/v1/llm/gateway/v1/"));
    }

    [Fact]
    public async Task ResolveRouteAsync_ShouldHandleAbsolutePathRoutePreference()
    {
        var provider = CreateProvider();

        var route = await provider.ResolveRouteAsync(
            CreateRequest(routePreference: "/custom/path"));

        route.RouteName.Should().Be("/custom/path");
        route.Endpoint.Should().Be(new Uri("https://nyx.example.com/custom/path"));
    }

    private static NyxIdLLMProvider CreateProvider() =>
        new(
            name: "nyxid",
            defaultModel: "gpt-5.4",
            nyxEndpoint: "https://nyx.example.com/api/v1/llm/gateway/v1",
            accessTokenAccessor: static () => null);

    private static LLMRequest CreateRequest(string? routePreference = null) =>
        new()
        {
            Messages = [ChatMessage.User("hi")],
            Model = "claude-3-7-sonnet",
            LlmControl = CreateControl(routePreference: routePreference),
        };

    private static LLMControlContext CreateControl(
        string accessToken = "test-token",
        string? modelOverride = null,
        string? routePreference = null) =>
        new(
            NyxIdAccessToken: accessToken,
            NyxIdOrgToken: null,
            SenderNyxIdAccessToken: null,
            ModelOverride: modelOverride,
            NyxIdRoutePreference: routePreference,
            MaxToolRoundsOverride: null,
            UserMemoryPrompt: null);
}
