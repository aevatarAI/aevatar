using System.Net;
using System.Net.Http.Headers;
using System.Collections.Concurrent;

namespace Aevatar.AI.ToolProviders.Ornn.Tests;

internal sealed class OrnnTestHttpMessageHandler : HttpMessageHandler
{
    private readonly ConcurrentQueue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();
    private readonly Func<HttpRequestMessage, HttpResponseMessage>? _responseRouter;
    private readonly bool _hangUntilCanceled;
    private readonly ConcurrentQueue<CapturedHttpRequest> _requests = new();

    public IReadOnlyList<CapturedHttpRequest> Requests => _requests.ToArray();

    public OrnnTestHttpMessageHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
        : this(hangUntilCanceled: false, responseRouter: null, responses)
    {
    }

    private OrnnTestHttpMessageHandler(
        bool hangUntilCanceled,
        Func<HttpRequestMessage, HttpResponseMessage>? responseRouter,
        params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
    {
        _hangUntilCanceled = hangUntilCanceled;
        _responseRouter = responseRouter;
        foreach (var response in responses)
            _responses.Enqueue(response);
    }

    public static OrnnTestHttpMessageHandler ReturningJson(string json, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        return new OrnnTestHttpMessageHandler(_ => JsonResponse(json, statusCode));
    }

    /// <summary>
    /// Simulates a stuck upstream by parking the request until the supplied cancellation token
    /// fires. Use when verifying client-side per-call timeouts: the client's linked CTS must be
    /// the only thing that ends the wait, so the timeout assertion is deterministic regardless
    /// of the host machine's scheduler.
    /// </summary>
    public static OrnnTestHttpMessageHandler HangingUntilCanceled()
    {
        return new OrnnTestHttpMessageHandler(hangUntilCanceled: true, responseRouter: null);
    }

    public static OrnnTestHttpMessageHandler Routing(Func<HttpRequestMessage, HttpResponseMessage> responseRouter)
    {
        return new OrnnTestHttpMessageHandler(hangUntilCanceled: false, responseRouter);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _requests.Enqueue(CapturedHttpRequest.From(request));

        if (_hangUntilCanceled)
        {
            // Park on a TCS that's only completed by cancellation. Deterministic — no Task.Delay
            // polling — so the test's outcome depends purely on the client's own CTS firing.
            var tcs = new TaskCompletionSource();
            using (cancellationToken.Register(static state => ((TaskCompletionSource)state!).TrySetCanceled(), tcs))
            {
                await tcs.Task;
            }
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (_responseRouter is not null)
            return _responseRouter(request);

        var responseFactory = _responses.TryDequeue(out var queuedResponse)
            ? queuedResponse
            : _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        return responseFactory(request);
    }

    public static HttpResponseMessage JsonResponse(
        string json,
        HttpStatusCode statusCode = HttpStatusCode.OK,
        long? contentLength = null)
    {
        var response = new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        if (contentLength is not null)
            response.Content.Headers.ContentLength = contentLength;
        return response;
    }

    public static HttpResponseMessage OversizedStreamResponse(long byteCount)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new RepeatedByteStream(byteCount)),
        };
    }
}

internal sealed class RepeatedByteStream(long length) : Stream
{
    private long _position;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => length;
    public override long Position { get => _position; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var remaining = length - _position;
        if (remaining <= 0)
            return 0;
        var read = (int)Math.Min(count, remaining);
        Array.Fill(buffer, (byte)'x', offset, read);
        _position += read;
        return read;
    }

    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var remaining = length - _position;
        if (remaining <= 0)
            return ValueTask.FromResult(0);
        var read = (int)Math.Min(buffer.Length, remaining);
        buffer.Span[..read].Fill((byte)'x');
        _position += read;
        return ValueTask.FromResult(read);
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

internal sealed record CapturedHttpRequest(
    HttpMethod Method,
    Uri? RequestUri,
    AuthenticationHeaderValue? Authorization,
    string? ContentType)
{
    public static CapturedHttpRequest From(HttpRequestMessage request)
    {
        return new CapturedHttpRequest(
            request.Method,
            request.RequestUri,
            request.Headers.Authorization,
            request.Content?.Headers.ContentType?.MediaType);
    }
}
