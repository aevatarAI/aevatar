using System.Net;
using System.Text;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.LLMProviders.NyxId;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class NyxIdLLMProviderRoutingTests
{
    [Fact]
    public async Task ResolveRouteAsync_ShouldUseChronoLlmProxy_WhenNyxIdAccountHasActiveChronoLlmService()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {
                  "keys": [
                    {
                      "slug": "chrono-llm",
                      "endpoint_url": "https://chrono-llm.example.com",
                      "status": "active"
                    }
                  ]
                }
                """,
                Encoding.UTF8,
                "application/json"),
        });

        var provider = CreateProvider(handler);

        var route = await provider.ResolveRouteAsync(CreateRequest());

        route.RouteName.Should().Be("chrono-llm");
        route.Endpoint.Should().Be(new Uri("https://nyx.example.com/api/v1/proxy/s/chrono-llm/"));
        route.Request.Model.Should().Be("claude-3-7-sonnet");
        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.RequestUri.Should().Be(new Uri("https://nyx.example.com/api/v1/keys"));
        handler.LastRequest.Headers.Authorization?.Scheme.Should().Be("Bearer");
        handler.LastRequest.Headers.Authorization?.Parameter.Should().Be("test-token");
    }

    [Fact]
    public async Task ResolveRouteAsync_ShouldFallBackToGateway_WhenChronoLlmServiceIsMissing()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {
                  "keys": [
                    {
                      "slug": "telegram-bot",
                      "endpoint_url": "https://telegram.example.com",
                      "status": "active"
                    }
                  ]
                }
                """,
                Encoding.UTF8,
                "application/json"),
        });

        var provider = CreateProvider(handler);

        var route = await provider.ResolveRouteAsync(CreateRequest());

        route.RouteName.Should().Be("nyxid");
        route.Endpoint.Should().Be(new Uri("https://nyx.example.com/api/v1/llm/gateway/v1/"));
    }

    [Fact]
    public async Task ResolveRouteAsync_ShouldFallBackToGateway_WhenRouteLookupFails()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));
        var provider = CreateProvider(handler);

        var route = await provider.ResolveRouteAsync(CreateRequest());

        route.RouteName.Should().Be("nyxid");
        route.Endpoint.Should().Be(new Uri("https://nyx.example.com/api/v1/llm/gateway/v1/"));
    }

    [Fact]
    public async Task ResolveRouteAsync_ShouldRespectGatewayRoutePreference()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {
                  "keys": [
                    {
                      "slug": "chrono-llm",
                      "endpoint_url": "https://chrono-llm.example.com",
                      "status": "active"
                    }
                  ]
                }
                """,
                Encoding.UTF8,
                "application/json"),
        });
        var provider = CreateProvider(handler);

        var route = await provider.ResolveRouteAsync(CreateRequest(LLMRequestMetadataKeys.NyxIdRoutePreference, "gateway"));

        route.RouteName.Should().Be("nyxid");
        route.Endpoint.Should().Be(new Uri("https://nyx.example.com/api/v1/llm/gateway/v1/"));
        handler.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task ResolveRouteAsync_ShouldRespectExplicitServiceRoutePreference()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {
                  "keys": [
                    {
                      "slug": "chrono-llm",
                      "endpoint_url": "https://chrono-llm.example.com",
                      "status": "active"
                    }
                  ]
                }
                """,
                Encoding.UTF8,
                "application/json"),
        });
        var provider = CreateProvider(handler);

        var route = await provider.ResolveRouteAsync(CreateRequest(LLMRequestMetadataKeys.NyxIdRoutePreference, "chrono-llm"));

        route.RouteName.Should().Be("chrono-llm");
        route.Endpoint.Should().Be(new Uri("https://nyx.example.com/api/v1/proxy/s/chrono-llm/"));
    }

    private static NyxIdLLMProvider CreateProvider(HttpMessageHandler handler) =>
        new(
            name: "nyxid",
            defaultModel: "gpt-4o-mini",
            gatewayEndpoint: "https://nyx.example.com/api/v1/llm/gateway/v1",
            accessTokenAccessor: static () => null,
            routeLookupClient: new HttpClient(handler));

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

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(_responseFactory(request));
        }
    }
}
