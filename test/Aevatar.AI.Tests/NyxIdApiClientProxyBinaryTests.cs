using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Aevatar.AI.ToolProviders.NyxId;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class NyxIdApiClientProxyBinaryTests
{
    [Fact]
    public async Task ProxyRequestBinaryAsync_ShouldPreserveBytesContentTypeAuthHeadersAndUserAgent()
    {
        var handler = new CapturingHandler("""{ "ok": true }""");
        var client = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example/" },
            new HttpClient(handler));
        var body = new byte[] { 0, 1, 2, 255 };

        var response = await client.ProxyRequestBinaryAsync(
            token: "access-token",
            slug: "ornn",
            path: "/api/v1/skills",
            method: "post",
            body: body,
            contentType: "application/zip",
            extraHeaders: new Dictionary<string, string>
            {
                ["X-Trace-Id"] = "trace-1",
            },
            ct: CancellationToken.None);

        response.Should().Contain("ok");
        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Post);
        request.Uri!.AbsoluteUri.Should().Be("https://nyx.example/api/v1/proxy/s/ornn/api/v1/skills");
        request.Authorization.Should().Be(new AuthenticationHeaderValue("Bearer", "access-token"));
        request.ContentType.Should().Be("application/zip");
        request.Body.Should().Equal(body);
        request.Headers.Should().ContainKey("X-Trace-Id");
        request.Headers["X-Trace-Id"].Should().Equal("trace-1");
        request.Headers.Should().ContainKey("User-Agent");
        request.Headers["User-Agent"].Should().Equal(NyxIdApiClient.DefaultProxyUserAgent);
    }

    [Fact]
    public async Task ProxyRequestBinaryAsync_ShouldHonorCallerProvidedUserAgent()
    {
        var handler = new CapturingHandler("""{ "ok": true }""");
        var client = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example" },
            new HttpClient(handler));

        await client.ProxyRequestBinaryAsync(
            token: "token",
            slug: "ornn",
            path: "api/v1/skills",
            method: "POST",
            body: [1],
            contentType: "application/zip",
            extraHeaders: new Dictionary<string, string>
            {
                ["User-Agent"] = "custom-client",
            },
            ct: CancellationToken.None);

        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Headers.Should().ContainKey("User-Agent");
        request.Headers["User-Agent"].Should().Equal("custom-client");
    }

    [Fact]
    public async Task ProxyRequestAsync_ShouldKeepExistingJsonProxyBehavior()
    {
        var handler = new CapturingHandler("""{ "ok": true }""");
        var client = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example" },
            new HttpClient(handler));

        await client.ProxyRequestAsync(
            token: "token",
            slug: "github",
            path: "/repos",
            method: "POST",
            body: """{ "name": "repo" }""",
            extraHeaders: null,
            ct: CancellationToken.None);

        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Post);
        request.Uri!.AbsoluteUri.Should().Be("https://nyx.example/api/v1/proxy/s/github/repos");
        request.ContentType.Should().Be("application/json");
        Encoding.UTF8.GetString(request.Body).Should().Be("""{ "name": "repo" }""");
        request.Headers.Should().ContainKey("User-Agent");
        request.Headers["User-Agent"].Should().Equal(NyxIdApiClient.DefaultProxyUserAgent);
    }

    private sealed class CapturingHandler(
        string responseBody,
        HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new CapturedRequest(
                request.Method,
                request.RequestUri,
                request.Headers.Authorization,
                request.Content?.Headers.ContentType?.MediaType,
                request.Content == null
                    ? []
                    : await request.Content.ReadAsByteArrayAsync(cancellationToken),
                request.Headers.ToDictionary(
                    x => x.Key,
                    x => x.Value.ToArray(),
                    StringComparer.OrdinalIgnoreCase)));

            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody),
            };
        }
    }

    private sealed record CapturedRequest(
        HttpMethod Method,
        Uri? Uri,
        AuthenticationHeaderValue? Authorization,
        string? ContentType,
        byte[] Body,
        IReadOnlyDictionary<string, string[]> Headers);
}
