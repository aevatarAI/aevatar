using System.Net;
using System.Net.Http.Headers;

namespace Aevatar.AI.ToolProviders.Ornn.Tests;

internal sealed class OrnnTestHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();
    private readonly bool _hangUntilCanceled;
    private readonly TaskCompletionSource _requestStarted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public List<CapturedHttpRequest> Requests { get; } = [];
    public Task RequestStarted => _requestStarted.Task;

    public OrnnTestHttpMessageHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
        : this(hangUntilCanceled: false, responses)
    {
    }

    private OrnnTestHttpMessageHandler(bool hangUntilCanceled, params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
    {
        _hangUntilCanceled = hangUntilCanceled;
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
        return new OrnnTestHttpMessageHandler(hangUntilCanceled: true);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(await CapturedHttpRequest.FromAsync(request, cancellationToken));
        _requestStarted.TrySetResult();

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

        var responseFactory = _responses.Count > 0
            ? _responses.Dequeue()
            : _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        return responseFactory(request);
    }

    public static HttpResponseMessage JsonResponse(string json, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
    }
}

internal sealed record CapturedHttpRequest(
    HttpMethod Method,
    Uri? RequestUri,
    AuthenticationHeaderValue? Authorization,
    string? ContentType,
    string? Body)
{
    public static async Task<CapturedHttpRequest> FromAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        return new CapturedHttpRequest(
            request.Method,
            request.RequestUri,
            request.Headers.Authorization,
            request.Content?.Headers.ContentType?.MediaType,
            request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken));
    }
}
