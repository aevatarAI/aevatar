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
    public async Task ProxyGetBinaryResponseAsync_ShouldPreserveDownloadedBytesAndHeaders()
    {
        var body = new byte[] { 0, 1, 2, 255 };
        var handler = new CapturingHandler(
            body,
            "image/png",
            "attachment; filename=\"photo.png\"");
        var client = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example" },
            new HttpClient(handler));

        var response = await client.ProxyGetBinaryResponseAsync(
            token: "token",
            slug: "api-lark-bot",
            path: "open-apis/im/v1/messages/om_1/resources/img_1?type=image",
            extraHeaders: null,
            ct: CancellationToken.None);

        response.Succeeded.Should().BeTrue();
        response.Content.Should().Equal(body);
        response.ContentType.Should().Be("image/png");
        response.FileName.Should().Be("photo.png");
        response.HttpStatus.Should().Be(200);

        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Get);
        request.Uri!.AbsoluteUri.Should().Be(
            "https://nyx.example/api/v1/proxy/s/api-lark-bot/open-apis/im/v1/messages/om_1/resources/img_1?type=image");
        request.Headers.Should().ContainKey("User-Agent");
        request.Headers["User-Agent"].Should().Equal(NyxIdApiClient.DefaultProxyUserAgent);
    }

    [Fact]
    public async Task ProxyGetBinaryResponseAsync_ShouldReturnFailureWithoutBytesOnNonSuccess()
    {
        var handler = new CapturingHandler(
            Encoding.UTF8.GetBytes("""{"error":"missing scope"}"""),
            "application/json",
            statusCode: HttpStatusCode.Forbidden);
        var client = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example" },
            new HttpClient(handler));

        var response = await client.ProxyGetBinaryResponseAsync(
            token: "token",
            slug: "api-lark-bot",
            path: "open-apis/im/v1/messages/om_1/resources/file_1?type=file",
            extraHeaders: null,
            ct: CancellationToken.None);

        response.Succeeded.Should().BeFalse();
        response.Content.Should().BeEmpty();
        response.ContentType.Should().Be("application/json");
        response.Detail.Should().Contain("missing scope");
        response.HttpStatus.Should().Be(403);
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

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly byte[] _responseBody;
        private readonly string? _contentType;
        private readonly string? _contentDisposition;
        private readonly HttpStatusCode _statusCode;

        public CapturingHandler(
            string responseBody,
            HttpStatusCode statusCode = HttpStatusCode.OK)
            : this(Encoding.UTF8.GetBytes(responseBody), "text/plain", null, statusCode)
        {
        }

        public CapturingHandler(
            byte[] responseBody,
            string? contentType = null,
            string? contentDisposition = null,
            HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            _responseBody = responseBody;
            _contentType = contentType;
            _contentDisposition = contentDisposition;
            _statusCode = statusCode;
        }

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

            var response = new HttpResponseMessage(_statusCode)
            {
                Content = new ByteArrayContent(_responseBody),
            };
            if (!string.IsNullOrWhiteSpace(_contentType))
                response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(_contentType);
            if (!string.IsNullOrWhiteSpace(_contentDisposition))
                response.Content.Headers.ContentDisposition = ContentDispositionHeaderValue.Parse(_contentDisposition);

            return response;
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
