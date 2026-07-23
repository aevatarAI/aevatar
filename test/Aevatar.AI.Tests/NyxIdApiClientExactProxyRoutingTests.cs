using System.Net;
using System.Text;
using Aevatar.AI.ToolProviders.NyxId;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class NyxIdApiClientExactProxyRoutingTests
{
    [Fact]
    public async Task ProxyRequestAsync_WithExactServiceId_ShouldAppendNyxIdViaWithoutDroppingQuery()
    {
        var handler = new CaptureHandler();
        var client = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler));

        await client.ProxyRequestAsync(
            "token",
            "home-assistant",
            "us-home-alpha",
            "/api/items?limit=2",
            "GET",
            body: null,
            extraHeaders: null,
            CancellationToken.None);

        handler.RequestUri.Should().NotBeNull();
        handler.RequestUri!.AbsolutePath.Should().Be("/api/v1/proxy/s/home-assistant/api/items");
        handler.RequestUri.Query.Should()
            .Contain("limit=2")
            .And.Contain("_nyxid_via=us-home-alpha");
    }

    [Fact]
    public async Task ProxyRequestAsync_WithCallerSuppliedNyxIdVia_ShouldUseOnlyExactServiceId()
    {
        var handler = new CaptureHandler();
        var client = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler));

        await client.ProxyRequestAsync(
            "token",
            "home-assistant",
            "us-home-alpha",
            "/api/items?_nyxid_via=us-forged&limit=2",
            "GET",
            body: null,
            extraHeaders: null,
            CancellationToken.None);

        handler.RequestUri.Should().NotBeNull();
        var queryParts = handler.RequestUri!.Query.TrimStart('?').Split('&');
        queryParts.Should().ContainSingle(part => part.StartsWith("_nyxid_via=", StringComparison.Ordinal));
        queryParts.Should().Contain("_nyxid_via=us-home-alpha");
        queryParts.Should().NotContain("_nyxid_via=us-forged");
        queryParts.Should().Contain("limit=2");
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            });
        }
    }
}
