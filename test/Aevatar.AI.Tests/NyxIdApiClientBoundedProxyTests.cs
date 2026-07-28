using System.Net;
using System.Text;
using Aevatar.AI.ToolProviders.NyxId;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class NyxIdApiClientBoundedProxyTests
{
    [Fact]
    public async Task ProxyRequestBoundedAsync_WhenContentLengthExceedsLimit_DoesNotReadBody()
    {
        var content = new ThrowOnReadContent(contentLength: 4);
        var client = CreateClient(new StaticResponseHandler(content));

        var response = await client.ProxyRequestBoundedAsync(
            token: "token",
            slug: "chrono-sandbox",
            userServiceId: "us-sandbox",
            path: "/codex/execute",
            method: "POST",
            body: """{"prompt":"ready"}""",
            extraHeaders: null,
            maxBytes: 3,
            ct: CancellationToken.None);

        response.Succeeded.Should().BeFalse();
        response.Content.Should().BeEmpty();
        response.Detail.Should().Be("content_length_exceeds_max_bytes");
        response.HttpStatus.Should().Be(200);
        content.ReadAttempted.Should().BeFalse();
    }

    [Fact]
    public async Task ProxyRequestBoundedAsync_WhenUnknownLengthBodyExceedsLimit_StopsWithTypedFailure()
    {
        var content = new StreamingContent(Encoding.UTF8.GetBytes("four"));
        var client = CreateClient(new StaticResponseHandler(content));

        var response = await client.ProxyRequestBoundedAsync(
            token: "token",
            slug: "chrono-sandbox",
            userServiceId: "us-sandbox",
            path: "/codex/execute",
            method: "POST",
            body: """{"prompt":"ready"}""",
            extraHeaders: null,
            maxBytes: 3,
            ct: CancellationToken.None);

        response.Succeeded.Should().BeFalse();
        response.Content.Should().BeEmpty();
        response.Detail.Should().Be("content_exceeds_max_bytes");
        response.HttpStatus.Should().Be(200);
    }

    private static NyxIdApiClient CreateClient(HttpMessageHandler handler) =>
        new(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler));

    private sealed class StaticResponseHandler(HttpContent content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content,
            });
    }

    private sealed class ThrowOnReadContent : HttpContent
    {
        public ThrowOnReadContent(long contentLength)
        {
            Headers.ContentLength = contentLength;
        }

        public bool ReadAttempted { get; private set; }

        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context)
        {
            ReadAttempted = true;
            throw new InvalidOperationException("The oversized body must not be read.");
        }

        protected override bool TryComputeLength(out long length)
        {
            length = Headers.ContentLength!.Value;
            return true;
        }
    }

    private sealed class StreamingContent(byte[] content) : HttpContent
    {
        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context) =>
            stream.WriteAsync(content).AsTask();

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new MemoryStream(content, writable: false));

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
