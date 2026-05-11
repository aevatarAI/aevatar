using System.Net;
using System.Net.Http.Headers;

namespace Aevatar.AI.ToolProviders.Ornn.Tests;

internal sealed class OrnnTestHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();

    public List<CapturedHttpRequest> Requests { get; } = [];

    public OrnnTestHttpMessageHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
    {
        foreach (var response in responses)
            _responses.Enqueue(response);
    }

    public static OrnnTestHttpMessageHandler ReturningJson(string json, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        return new OrnnTestHttpMessageHandler(_ => JsonResponse(json, statusCode));
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(CapturedHttpRequest.From(request));

        var responseFactory = _responses.Count > 0
            ? _responses.Dequeue()
            : _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        return Task.FromResult(responseFactory(request));
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
    AuthenticationHeaderValue? Authorization)
{
    public static CapturedHttpRequest From(HttpRequestMessage request)
    {
        return new CapturedHttpRequest(
            request.Method,
            request.RequestUri,
            request.Headers.Authorization);
    }
}
