using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Aevatar.AI.Abstractions.ToolProviders;
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
    public async Task ProxyRequestMultipartAsync_ShouldShapeMultipartProxyRequest()
    {
        var handler = new CapturingHandler("""{ "ok": true }""");
        var client = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example/" },
            new HttpClient(handler));
        var fileBytes = Encoding.UTF8.GetBytes("hello lark upload");
        await using var stream = new MemoryStream(fileBytes);

        var response = await client.ProxyRequestMultipartAsync(
            token: "access-token",
            slug: "api-lark-bot",
            path: "/open-apis/drive/v1/medias/upload_all",
            method: "post",
            formFields: new Dictionary<string, string>
            {
                ["file_name"] = "report.txt",
                ["parent_type"] = "doc_file",
                ["parent_node"] = "doccn_123",
                ["size"] = fileBytes.Length.ToString(),
                ["checksum"] = "sha256-value",
                ["extra"] = """{"source":"workflow"}""",
            },
            fileFieldName: "file",
            fileName: "report.txt",
            fileContentType: "text/plain",
            fileContent: stream,
            extraHeaders: null,
            ct: CancellationToken.None);

        response.Should().Contain("ok");
        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Post);
        request.Uri!.AbsoluteUri.Should().Be(
            "https://nyx.example/api/v1/proxy/s/api-lark-bot/open-apis/drive/v1/medias/upload_all");
        request.Authorization.Should().Be(new AuthenticationHeaderValue("Bearer", "access-token"));
        request.ContentType.Should().Be("multipart/form-data");
        request.ContentTypeHeader.Should().StartWith("multipart/form-data; boundary=");
        request.Headers.Should().ContainKey("User-Agent");
        request.Headers["User-Agent"].Should().Equal(NyxIdApiClient.DefaultProxyUserAgent);

        var body = Encoding.UTF8.GetString(request.Body);
        body.Should().Contain("""name=file_name""");
        body.Should().Contain("report.txt");
        body.Should().Contain("""name=parent_type""");
        body.Should().Contain("doc_file");
        body.Should().Contain("""name=parent_node""");
        body.Should().Contain("doccn_123");
        body.Should().Contain("""name=size""");
        body.Should().Contain(fileBytes.Length.ToString());
        body.Should().Contain("""name=checksum""");
        body.Should().Contain("sha256-value");
        body.Should().Contain("""name=extra""");
        body.Should().Contain("""{"source":"workflow"}""");
        body.Should().Contain("""name=file; filename=report.txt""");
        body.Should().Contain("Content-Type: text/plain");
        body.Should().Contain("hello lark upload");
    }

    [Fact]
    public async Task ProxyRequestAsync_ShouldForwardContextIdempotencyKeyForSideEffectingMethods()
    {
        var handler = new CapturingHandler("""{ "ok": true }""");
        var client = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example" },
            new HttpClient(handler));

        using var _ = AgentToolContextScope.Push(WithIdempotencyKey("  idem-json-1  "));

        await client.ProxyRequestAsync(
            token: "token",
            slug: "github",
            path: "/repos",
            method: "POST",
            body: """{ "name": "repo" }""",
            extraHeaders: null,
            ct: CancellationToken.None);

        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Headers.Should().ContainKey("Idempotency-Key");
        request.Headers["Idempotency-Key"].Should().Equal("idem-json-1");
    }

    [Fact]
    public async Task ProxyRequestBinaryAsync_ShouldForwardContextIdempotencyKeyForSideEffectingMethods()
    {
        var handler = new CapturingHandler("""{ "ok": true }""");
        var client = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example" },
            new HttpClient(handler));

        using var _ = AgentToolContextScope.Push(WithIdempotencyKey("  idem-binary-1  "));

        await client.ProxyRequestBinaryAsync(
            token: "token",
            slug: "ornn",
            path: "api/v1/skills",
            method: "POST",
            body: [1],
            contentType: "application/zip",
            extraHeaders: null,
            ct: CancellationToken.None);

        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Headers.Should().ContainKey("Idempotency-Key");
        request.Headers["Idempotency-Key"].Should().Equal("idem-binary-1");
    }

    [Fact]
    public async Task ProxyRequestMultipartAsync_ShouldForwardContextIdempotencyKeyForSideEffectingMethods()
    {
        var handler = new CapturingHandler("""{ "ok": true }""");
        var client = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example" },
            new HttpClient(handler));
        await using var stream = new MemoryStream([1, 2, 3]);

        using var _ = AgentToolContextScope.Push(WithIdempotencyKey("  idem-multipart-1  "));

        await client.ProxyRequestMultipartAsync(
            token: "token",
            slug: "api-lark-bot",
            path: "/upload",
            method: "POST",
            formFields: new Dictionary<string, string>
            {
                ["file_name"] = "report.txt",
            },
            fileFieldName: "file",
            fileName: "report.txt",
            fileContentType: "text/plain",
            fileContent: stream,
            extraHeaders: null,
            ct: CancellationToken.None);

        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Headers.Should().ContainKey("Idempotency-Key");
        request.Headers["Idempotency-Key"].Should().Equal("idem-multipart-1");
    }

    [Fact]
    public async Task ProxyRequestAsync_ShouldNotForwardIdempotencyKeyForGet()
    {
        var handler = new CapturingHandler("""{ "ok": true }""");
        var client = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example" },
            new HttpClient(handler));

        using var _ = AgentToolContextScope.Push(WithIdempotencyKey("idem-get-1"));

        await client.ProxyRequestAsync(
            token: "token",
            slug: "github",
            path: "/repos",
            method: "GET",
            body: null,
            extraHeaders: null,
            ct: CancellationToken.None);

        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Get);
        request.Headers.Should().NotContainKey("Idempotency-Key");
    }

    [Fact]
    public async Task ProxyRequestAsync_ShouldNotOverwriteCallerProvidedIdempotencyKey()
    {
        var handler = new CapturingHandler("""{ "ok": true }""");
        var client = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example" },
            new HttpClient(handler));

        using var _ = AgentToolContextScope.Push(WithIdempotencyKey("context-idem-1"));

        await client.ProxyRequestAsync(
            token: "token",
            slug: "github",
            path: "/repos",
            method: "POST",
            body: """{ "name": "repo" }""",
            extraHeaders: new Dictionary<string, string>
            {
                ["Idempotency-Key"] = "caller-idem-1",
            },
            ct: CancellationToken.None);

        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Headers.Should().ContainKey("Idempotency-Key");
        request.Headers["Idempotency-Key"].Should().Equal("caller-idem-1");
    }

    [Fact]
    public async Task ProxyRequestAsync_ShouldNotForwardBlankContextIdempotencyKey()
    {
        var handler = new CapturingHandler("""{ "ok": true }""");
        var client = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example" },
            new HttpClient(handler));

        using var _ = AgentToolContextScope.Push(WithIdempotencyKey("   "));

        await client.ProxyRequestAsync(
            token: "token",
            slug: "github",
            path: "/repos",
            method: "POST",
            body: """{ "name": "repo" }""",
            extraHeaders: null,
            ct: CancellationToken.None);

        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Headers.Should().NotContainKey("Idempotency-Key");
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
                request.Content?.Headers.ContentType?.ToString(),
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
        string? ContentTypeHeader,
        byte[] Body,
        IReadOnlyDictionary<string, string[]> Headers);

    private static AgentToolExecutionContext WithIdempotencyKey(string? idempotencyKey) =>
        AgentToolExecutionContext.Empty with
        {
            Request = new AgentToolRequestIdentity(null, null, idempotencyKey),
        };
}
