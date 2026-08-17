using System.Reflection;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.LLMProviders.NyxId;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class NyxIdLLMProviderRoutingTests
{
    [Fact]
    public void Capabilities_ShouldExposeDelegateMultimodalInputs()
    {
        var provider = CreateProvider();

        provider.Capabilities.SupportsInput(ContentPartKind.Text).Should().BeTrue();
        provider.Capabilities.SupportsInput(ContentPartKind.Image).Should().BeTrue();
        provider.Capabilities.SupportsToolCalls.Should().BeTrue();
        provider.Capabilities.SupportsStreaming.Should().BeTrue();
    }

    [Fact]
    public async Task ResolveRouteAsync_ShouldUseDefaultGateway_WhenNoRoutePreference()
    {
        var provider = CreateProvider();

        var route = await provider.ResolveRouteAsync(CreateRequest());

        route.RouteName.Should().Be("nyxid");
        route.Endpoint.Should().Be(new Uri("https://nyx.example.com/api/v1/llm/gateway/v1/"));
    }

    [Fact]
    public async Task ResolveRouteAsync_ShouldUseCanonicalGateway_WhenRoutePreferenceIsGateway()
    {
        var provider = CreateProvider();

        var route = await provider.ResolveRouteAsync(
            CreateRequest(routePreference: "gateway"));

        route.RouteName.Should().Be("/api/v1/llm/gateway/v1");
        route.Endpoint.Should().Be(new Uri("https://nyx.example.com/api/v1/llm/gateway/v1"));
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
    public async Task ResolveRouteAsync_ShouldRouteToServiceProxy_WhenRoutePreferenceIsCanonicalProxyPath()
    {
        var provider = CreateProvider();

        var route = await provider.ResolveRouteAsync(
            CreateRequest(routePreference: "/api/v1/proxy/s/chrono-llm"));

        route.RouteName.Should().Be("/api/v1/proxy/s/chrono-llm");
        route.Endpoint.Should().Be(new Uri("https://nyx.example.com/api/v1/proxy/s/chrono-llm"));
    }

    [Fact]
    public async Task ResolveRouteAsync_ShouldPreserveExactCatalogAndUserServiceIdentity()
    {
        var provider = CreateProvider();

        var route = await provider.ResolveRouteAsync(CreateRequest(
            routePreference: "/api/v1/proxy/catalog-chrono?_nyxid_via=us-chrono"));

        route.RouteName.Should().Be("/api/v1/proxy/catalog-chrono?_nyxid_via=us-chrono");
        route.Endpoint.Should().Be(new Uri("https://nyx.example.com/api/v1/proxy/catalog-chrono"));
        route.ExactUserServiceId.Should().Be("us-chrono");
    }

    [Fact]
    public async Task ResolveRouteAsync_ShouldPreservePortableCatalogIdentityWithoutUserSelector()
    {
        var provider = CreateProvider();

        var route = await provider.ResolveRouteAsync(CreateRequest(
            routePreference: "/api/v1/proxy/catalog-chrono"));

        route.RouteName.Should().Be("/api/v1/proxy/catalog-chrono");
        route.Endpoint.Should().Be(new Uri("https://nyx.example.com/api/v1/proxy/catalog-chrono"));
        route.ExactUserServiceId.Should().BeNull();
    }

    [Fact]
    public async Task ResolveRouteAsync_ShouldPreserveExactUserServiceIdentityForLegacySlugRoute()
    {
        var provider = CreateProvider();

        var route = await provider.ResolveRouteAsync(CreateRequest(
            routePreference: "/api/v1/proxy/s/chrono-llm?_nyxid_via=us-chrono"));

        route.RouteName.Should().Be("/api/v1/proxy/s/chrono-llm?_nyxid_via=us-chrono");
        route.Endpoint.Should().Be(new Uri("https://nyx.example.com/api/v1/proxy/s/chrono-llm"));
        route.ExactUserServiceId.Should().Be("us-chrono");
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

    [Theory]
    [InlineData("/https://attacker.example")]
    [InlineData("//attacker.example")]
    [InlineData("https://attacker.example")]
    [InlineData("/api/v1/proxy/s/chrono-llm?target=https://attacker.example")]
    [InlineData("/api/v1/proxy/s/chrono-llm#https://attacker.example")]
    [InlineData("/api/v1/proxy/catalog-chrono?_nyxid_via=")]
    [InlineData("/api/v1/proxy/catalog-chrono?_nyxid_via=us-chrono&target=other")]
    [InlineData("/api/v1/proxy/catalog-chrono/extra?_nyxid_via=us-chrono")]
    [InlineData("/api/v1/proxy/catalog-chrono?_nyxid_via=../other")]
    [InlineData("user@attacker.example")]
    [InlineData("/custom/path")]
    [InlineData("/api/v1/proxy/s/chrono-llm/extra")]
    [InlineData("Bad-Slug")]
    [InlineData("bad_slug")]
    [InlineData("bad.slug")]
    [InlineData("bad-")]
    [InlineData("bad--slug")]
    [InlineData("/api/v1/proxy/s/Bad-Slug")]
    [InlineData("/api/v1/proxy/s/bad_slug")]
    [InlineData("/api/v1/proxy/s/bad.slug")]
    [InlineData("/api/v1/proxy/s/bad-")]
    [InlineData("/api/v1/proxy/s/bad--slug")]
    public async Task ResolveRouteAsync_ShouldRejectNonCanonicalRoutePreference(string routePreference)
    {
        var provider = CreateProvider();

        var act = () => provider.ResolveRouteAsync(
            CreateRequest(routePreference: routePreference));

        await act.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("The explicit NyxID route preference is not canonical.");
    }

    [Theory]
    [InlineData("/https://attacker.example")]
    [InlineData("//attacker.example")]
    [InlineData("https://attacker.example")]
    [InlineData("Bad-Slug")]
    [InlineData("bad_slug")]
    [InlineData("bad.slug")]
    [InlineData("bad-")]
    [InlineData("bad--slug")]
    public async Task ResolveRouteAsync_ShouldRejectNonCanonicalConfiguredDefaultRoutePreference(
        string defaultRoutePreference)
    {
        var provider = CreateProviderWithDefaultRoute(defaultRoutePreference);

        var route = await provider.ResolveRouteAsync(CreateRequest());

        route.RouteName.Should().Be("nyxid");
        route.Endpoint.Should().Be(new Uri("https://nyx.example.com/api/v1/llm/gateway/v1/"));
        route.Endpoint.Scheme.Should().Be(Uri.UriSchemeHttps);
        route.Endpoint.Authority.Should().Be("nyx.example.com");
    }

    [Fact]
    public async Task ResolveRouteAsync_ShouldUseDefaultRoutePreference_WhenConfiguredAndNoRequestPreference()
    {
        // When a default route is configured, the no-override (server-default / owner-fallback)
        // path must resolve to it instead of the bare NyxID gateway — the gateway forwards to
        // the OpenAI provider, which fails closed when OpenAI is not connected.
        var provider = CreateProviderWithDefaultRoute("chrono-llm-public");

        var route = await provider.ResolveRouteAsync(CreateRequest());

        route.RouteName.Should().Be("/api/v1/proxy/s/chrono-llm-public");
        route.Endpoint.Should().Be(new Uri("https://nyx.example.com/api/v1/proxy/s/chrono-llm-public"));
    }

    [Fact]
    public async Task ResolveRouteAsync_ExplicitGatewayAlias_ShouldBeatConfiguredProxyDefault()
    {
        var provider = CreateProviderWithDefaultRoute("chrono-llm-public");

        var route = await provider.ResolveRouteAsync(CreateRequest(routePreference: "gateway"));

        route.RouteName.Should().Be("/api/v1/llm/gateway/v1");
        route.Endpoint.Should().Be(new Uri("https://nyx.example.com/api/v1/llm/gateway/v1"));
    }

    [Fact]
    public async Task ResolveRouteAsync_ExplicitGateway_ShouldBeatConfiguredProxyDefault()
    {
        var provider = CreateProviderWithDefaultRoute("chrono-llm-public");

        var route = await provider.ResolveRouteAsync(
            CreateRequest(routePreference: "/api/v1/llm/gateway/v1"));

        route.RouteName.Should().Be("/api/v1/llm/gateway/v1");
        route.Endpoint.Should().Be(new Uri("https://nyx.example.com/api/v1/llm/gateway/v1"));
    }

    [Fact]
    public async Task ResolveRouteAsync_ShouldHonorExplicitRoutePreference_OverDefaultRoutePreference()
    {
        var provider = CreateProviderWithDefaultRoute("chrono-llm-public");

        var route = await provider.ResolveRouteAsync(CreateRequest(routePreference: "chrono-llm"));

        route.RouteName.Should().Be("/api/v1/proxy/s/chrono-llm");
        route.Endpoint.Should().Be(new Uri("https://nyx.example.com/api/v1/proxy/s/chrono-llm"));
    }

    [Fact]
    public void CreateDelegateProvider_ShouldPassToolExecutionPortToMeaiProvider()
    {
        var executionPort = new RecordingExecutionPort();
        var provider = new NyxIdLLMProvider(
            name: "nyxid",
            defaultModel: "gpt-5.5",
            nyxEndpoint: "https://nyx.example.com/api/v1/llm/gateway/v1",
            accessTokenAccessor: static () => null,
            toolExecutionPort: executionPort);
        var method = typeof(NyxIdLLMProvider).GetMethod(
            "CreateDelegateProvider",
            BindingFlags.Instance | BindingFlags.NonPublic);

        var delegateProvider = method!.Invoke(provider,
            [
                new LLMRequest { Messages = [ChatMessage.User("hi")], Model = "gpt-5.5" },
                new Uri("https://nyx.example.com/api/v1/proxy/s/chrono-llm-public"),
                "/api/v1/proxy/s/chrono-llm-public",
                "test-token",
                null,
            ]);

        delegateProvider.Should().NotBeNull();
        var executionPortField = delegateProvider!.GetType().GetField(
            "_toolExecutionPort",
            BindingFlags.Instance | BindingFlags.NonPublic);
        executionPortField.Should().NotBeNull();
        executionPortField!.GetValue(delegateProvider).Should().BeSameAs(executionPort);
    }

    [Fact]
    public void ApplyExactUserServiceSelector_ShouldReplaceForgedSelectorOnFinalSdkRequest()
    {
        var requestUri = new Uri(
            "https://nyx.example.com/api/v1/proxy/catalog-chrono/chat/completions?api-version=v1&_nyxid_via=forged");

        var selected = NyxIdLLMProvider.ApplyExactUserServiceSelector(requestUri, "us-chrono");

        selected.AbsolutePath.Should().Be("/api/v1/proxy/catalog-chrono/chat/completions");
        var queryParts = selected.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
        queryParts.Should().BeEquivalentTo("api-version=v1", "_nyxid_via=us-chrono");
        queryParts.Should().ContainSingle(static part =>
            part.StartsWith("_nyxid_via=", StringComparison.Ordinal));
    }

    private static NyxIdLLMProvider CreateProvider() =>
        new(
            name: "nyxid",
            defaultModel: "gpt-5.4",
            nyxEndpoint: "https://nyx.example.com/api/v1/llm/gateway/v1",
            accessTokenAccessor: static () => null);

    private static NyxIdLLMProvider CreateProviderWithDefaultRoute(string defaultRoutePreference) =>
        new(
            name: "nyxid",
            defaultModel: "gpt-5.5",
            nyxEndpoint: "https://nyx.example.com/api/v1/llm/gateway/v1",
            accessTokenAccessor: static () => null,
            defaultRoutePreference: defaultRoutePreference);

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

    private sealed class RecordingExecutionPort : IAgentToolExecutionPort
    {
        public Task<AgentToolExecutionOutcome> ExecuteAsync(
            AgentToolExecutionRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
