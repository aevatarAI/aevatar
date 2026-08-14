using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Aevatar.AI.ToolProviders.NyxId;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class NyxIdApiClientPublicTransportFallbackTests
{
    private const string PrimaryBaseUrl = "http://nyxid-internal:3001/internal-api";
    private const string FallbackBaseUrl = "https://nyx.example.com/public-api";

    [Fact]
    public async Task UnboundedGet_DnsFailure_RetriesPublicTransportAndPreservesRequest()
    {
        var handler = new DnsThenResponseHandler(
            () => JsonResponse("""{"ok":true}"""));
        using var client = CreateClient(handler);

        var result = await client.ProxyRequestAsync(
            token: "access-token",
            slug: "calendar",
            path: "/v1/events?cursor=A%2FB&mode=Exact",
            method: "GET",
            body: null,
            extraHeaders: new Dictionary<string, string>
            {
                ["X-Trace-Id"] = "trace-123",
                ["Host"] = "caller-supplied.invalid",
            },
            ct: CancellationToken.None);

        result.Should().Be("""{"ok":true}""");
        handler.Requests.Should().HaveCount(2);
        var primary = handler.Requests[0];
        var fallback = handler.Requests[1];
        primary.Uri.Should().Be(
            "http://nyxid-internal:3001/internal-api/api/v1/proxy/s/calendar/v1/events?cursor=A%2FB&mode=Exact");
        fallback.Uri.Should().Be(
            "https://nyx.example.com/public-api/api/v1/proxy/s/calendar/v1/events?cursor=A%2FB&mode=Exact");
        fallback.Method.Should().Be(HttpMethod.Get);
        fallback.Authorization.Should().Be("Bearer access-token");
        fallback.Headers["X-Trace-Id"].Should().Equal("trace-123");
        primary.Host.Should().Be("caller-supplied.invalid");
        fallback.Host.Should().BeNull();
        fallback.Version.Should().Be(primary.Version);
        fallback.VersionPolicy.Should().Be(primary.VersionPolicy);
    }

    [Fact]
    public async Task BoundedPost_DnsFailure_ReplaysBodyContentHeadersAndExactRoute()
    {
        var handler = new DnsThenResponseHandler(
            () => JsonResponse("""{"accepted":true}"""));
        using var client = CreateClient(handler);
        const string body = """{"name":"Case-Sensitive","count":2}""";

        var result = await client.ProxyRequestBoundedAsync(
            token: "access-token",
            slug: "forms",
            userServiceId: "us-primary",
            path: "/v2/items?view=full%2Fraw&case=ABC",
            method: "POST",
            body,
            extraHeaders: new Dictionary<string, string>
            {
                ["X-Request-Class"] = "bounded",
            },
            maxBytes: 1024,
            ct: CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Content.Should().Be("""{"accepted":true}""");
        handler.Requests.Should().HaveCount(2);
        var primary = handler.Requests[0];
        var fallback = handler.Requests[1];
        fallback.Uri.Should().Be(
            "https://nyx.example.com/public-api/api/v1/proxy/s/forms/v2/items?_nyxid_via=us-primary&view=full%2Fraw&case=ABC");
        fallback.Method.Should().Be(HttpMethod.Post);
        fallback.Authorization.Should().Be("Bearer access-token");
        fallback.Headers["X-Request-Class"].Should().Equal("bounded");
        fallback.Body.Should().Equal(Encoding.UTF8.GetBytes(body));
        fallback.ContentHeaders["Content-Type"].Should().Equal("application/json; charset=utf-8");
        fallback.Version.Should().Be(primary.Version);
        fallback.VersionPolicy.Should().Be(primary.VersionPolicy);
    }

    [Fact]
    public async Task BinaryResponse_DnsFailure_RetriesAndPreservesReturnedBytesAndHeaders()
    {
        var expected = new byte[] { 0, 1, 2, 255 };
        var handler = new DnsThenResponseHandler(() =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(expected),
            };
            response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/octet-stream");
            response.Content.Headers.ContentDisposition =
                ContentDispositionHeaderValue.Parse("attachment; filename=\"export.bin\"");
            return response;
        });
        using var client = CreateClient(handler);

        var result = await client.ProxyGetBinaryResponseAsync(
            token: "access-token",
            slug: "storage",
            path: "/files/export?format=raw",
            extraHeaders: new Dictionary<string, string> { ["X-Trace-Id"] = "binary-1" },
            ct: CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Content.Should().Equal(expected);
        result.ContentType.Should().Be("application/octet-stream");
        result.FileName.Should().Be("export.bin");
        handler.Requests.Should().HaveCount(2);
        handler.Requests[1].Uri.Should().Be(
            "https://nyx.example.com/public-api/api/v1/proxy/s/storage/files/export?format=raw");
        handler.Requests[1].Headers["X-Trace-Id"].Should().Equal("binary-1");
    }

    [Fact]
    public async Task ConnectionRefused_RetriesPublicTransportOnce()
    {
        var handler = new ThrowThenResponseHandler(
            new HttpRequestException(
                HttpRequestError.ConnectionError,
                "connection refused",
                new SocketException((int)SocketError.ConnectionRefused)),
            () => JsonResponse("""{"ok":true}"""));
        using var client = CreateClient(handler);

        var result = await client.ProxyRequestAsync(
            "access-token",
            "calendar",
            "/v1/events",
            "GET",
            null,
            null,
            CancellationToken.None);

        handler.SendCount.Should().Be(2);
        result.Should().Be("""{"ok":true}""");
    }

    [Fact]
    public async Task ConnectionReset_DoesNotRetryBecauseRequestMayHaveReachedPrimary()
    {
        var handler = new ThrowingHandler(
            new HttpRequestException(
                HttpRequestError.ConnectionError,
                "connection reset",
                new SocketException((int)SocketError.ConnectionReset)));
        using var client = CreateClient(handler);

        var result = await client.ProxyRequestAsync(
            "access-token",
            "calendar",
            "/v1/events",
            "GET",
            null,
            null,
            CancellationToken.None);

        handler.SendCount.Should().Be(1);
        result.Should().Contain("\"status\":0");
    }

    [Fact]
    public async Task DnsFailure_WithoutConfiguredPublicTransport_DoesNotRetry()
    {
        var handler = new ThrowingHandler(
            new HttpRequestException(HttpRequestError.NameResolutionError, "dns", null));
        using var client = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = PrimaryBaseUrl },
            new HttpClient(handler));

        var result = await client.ProxyRequestAsync(
            "access-token",
            "calendar",
            "/v1/events",
            "GET",
            null,
            null,
            CancellationToken.None);

        handler.SendCount.Should().Be(1);
        result.Should().Contain("\"status\":0");
    }

    [Fact]
    public async Task DnsFailure_WithUnqualifiedCallerHttpClient_DoesNotRetry()
    {
        var handler = new ThrowingHandler(
            new HttpRequestException(HttpRequestError.NameResolutionError, "dns", null));
        using var client = new NyxIdApiClient(
            new NyxIdToolOptions
            {
                BaseUrl = PrimaryBaseUrl,
                PublicTransportFallbackBaseUrl = FallbackBaseUrl,
            },
            new HttpClient(handler));

        var result = await client.ProxyRequestAsync(
            "access-token",
            "calendar",
            "/v1/events",
            "GET",
            null,
            null,
            CancellationToken.None);

        handler.SendCount.Should().Be(1);
        result.Should().Contain("\"status\":0");
    }

    [Fact]
    public async Task FactoryCreatedClient_DnsFailure_RetriesPublicTransport()
    {
        var handler = new DnsThenResponseHandler(
            () => JsonResponse("""{"ok":true}"""));
        using var httpClient = new HttpClient(handler);
        var factory = new HttpClientFactoryNyxIdApiClientFactory(
            new FixedHttpClientFactory(httpClient),
            new NyxIdToolOptions
            {
                BaseUrl = PrimaryBaseUrl,
                PublicTransportFallbackBaseUrl = FallbackBaseUrl,
            },
            new NyxIdApiClientTransportPolicy());
        using var client = factory.CreateClient();

        var result = await client.ProxyRequestAsync(
            "access-token",
            "calendar",
            "/v1/events",
            "GET",
            null,
            null,
            CancellationToken.None);

        result.Should().Be("""{"ok":true}""");
        handler.Requests.Select(static request => request.Uri).Should().Equal(
            "http://nyxid-internal:3001/internal-api/api/v1/proxy/s/calendar/v1/events",
            "https://nyx.example.com/public-api/api/v1/proxy/s/calendar/v1/events");
    }

    [Fact]
    public async Task DnsFailure_WhenPublicTransportAlsoFails_RetriesExactlyOnce()
    {
        var handler = new ThrowingHandler(
            new HttpRequestException(HttpRequestError.NameResolutionError, "dns", null));
        using var client = CreateClient(handler);

        var result = await client.ProxyRequestAsync(
            "access-token",
            "calendar",
            "/v1/events",
            "GET",
            null,
            null,
            CancellationToken.None);

        handler.SendCount.Should().Be(2);
        result.Should().Contain("\"status\":0");
    }

    [Fact]
    public async Task DnsFailure_WhenCallerCancellationWins_DoesNotRetry()
    {
        using var cts = new CancellationTokenSource();
        var handler = new CancelThenThrowHandler(cts);
        using var client = CreateClient(handler);

        var act = () => client.ProxyRequestAsync(
            "access-token",
            "calendar",
            "/v1/events",
            "GET",
            null,
            null,
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        handler.SendCount.Should().Be(1);
    }

    [Fact]
    public async Task HttpFailureResponse_DoesNotRetry()
    {
        var handler = new StaticResponseHandler(
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("unavailable", Encoding.UTF8, "text/plain"),
            });
        using var client = CreateClient(handler);

        var result = await client.ProxyRequestAsync(
            "access-token",
            "calendar",
            "/v1/events",
            "GET",
            null,
            null,
            CancellationToken.None);

        handler.SendCount.Should().Be(1);
        result.Should().Contain("\"status\": 503");
    }

    [Fact]
    public async Task Multipart_DnsFailure_DoesNotBufferOrRetry()
    {
        var handler = new ThrowingHandler(
            new HttpRequestException(HttpRequestError.NameResolutionError, "dns", null));
        using var client = CreateClient(handler);
        await using var stream = new ReadTrackingStream(new byte[] { 1, 2, 3, 4 });

        var result = await client.ProxyRequestMultipartAsync(
            token: "access-token",
            slug: "files",
            path: "/upload",
            method: "POST",
            formFields: new Dictionary<string, string> { ["name"] = "fixture" },
            fileFieldName: "file",
            fileName: "fixture.bin",
            fileContentType: "application/octet-stream",
            fileContent: stream,
            extraHeaders: null,
            ct: CancellationToken.None);

        handler.SendCount.Should().Be(1);
        stream.ReadCount.Should().Be(0);
        result.Should().Contain("\"status\":0");
    }

    private static NyxIdApiClient CreateClient(HttpMessageHandler handler) =>
        new(
            new NyxIdToolOptions
            {
                BaseUrl = PrimaryBaseUrl,
                PublicTransportFallbackBaseUrl = FallbackBaseUrl,
            },
            new HttpClient(handler),
            new NyxIdApiClientTransportPolicy());

    private static HttpResponseMessage JsonResponse(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private sealed class DnsThenResponseHandler(Func<HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(await CapturedRequest.CreateAsync(request, cancellationToken));
            if (Requests.Count == 1)
            {
                throw new HttpRequestException(
                    HttpRequestError.NameResolutionError,
                    "dns",
                    null);
            }

            return responseFactory();
        }
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        public int SendCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            SendCount++;
            return Task.FromException<HttpResponseMessage>(exception);
        }
    }

    private sealed class ThrowThenResponseHandler(
        Exception exception,
        Func<HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public int SendCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            SendCount++;
            return SendCount == 1
                ? Task.FromException<HttpResponseMessage>(exception)
                : Task.FromResult(responseFactory());
        }
    }

    private sealed class CancelThenThrowHandler(CancellationTokenSource cancellation) : HttpMessageHandler
    {
        public int SendCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            SendCount++;
            cancellation.Cancel();
            return Task.FromException<HttpResponseMessage>(new HttpRequestException(
                HttpRequestError.NameResolutionError,
                "dns",
                null));
        }
    }

    private sealed class StaticResponseHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public int SendCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            SendCount++;
            return Task.FromResult(response);
        }
    }

    private sealed class FixedHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed record CapturedRequest(
        HttpMethod Method,
        string Uri,
        string? Authorization,
        string? Host,
        Version Version,
        HttpVersionPolicy VersionPolicy,
        IReadOnlyDictionary<string, string[]> Headers,
        byte[] Body,
        IReadOnlyDictionary<string, string[]> ContentHeaders)
    {
        public static async Task<CapturedRequest> CreateAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            new(
                request.Method,
                request.RequestUri!.AbsoluteUri,
                request.Headers.Authorization?.ToString(),
                request.Headers.Host,
                request.Version,
                request.VersionPolicy,
                request.Headers.ToDictionary(
                    header => header.Key,
                    header => header.Value.ToArray(),
                    StringComparer.OrdinalIgnoreCase),
                request.Content is null
                    ? []
                    : await request.Content.ReadAsByteArrayAsync(cancellationToken),
                request.Content is null
                    ? new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
                    : request.Content.Headers.ToDictionary(
                        header => header.Key,
                        header => header.Value.ToArray(),
                        StringComparer.OrdinalIgnoreCase));
    }

    private sealed class ReadTrackingStream(byte[] content) : MemoryStream(content)
    {
        public int ReadCount { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadCount++;
            return base.Read(buffer, offset, count);
        }

        public override int Read(Span<byte> buffer)
        {
            ReadCount++;
            return base.Read(buffer);
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ReadCount++;
            return base.ReadAsync(buffer, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
        }
    }
}
