using Aevatar.Platform.Application.Abstractions.Ports;
using System.Net.Http.Headers;

namespace Aevatar.Platform.Infrastructure.Dispatch;

public sealed class HttpPlatformCommandDispatchGateway : IPlatformCommandDispatchGateway
{
    private readonly IHttpClientFactory _httpClientFactory;

    public HttpPlatformCommandDispatchGateway(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task<PlatformCommandDispatchResult> DispatchAsync(
        PlatformCommandDispatchRequest request,
        CancellationToken ct = default)
    {
        using var message = new HttpRequestMessage(new HttpMethod(request.Method), request.TargetEndpoint);
        if (!string.IsNullOrWhiteSpace(request.PayloadJson))
        {
            var payload = new StringContent(request.PayloadJson);
            payload.Headers.ContentType = MediaTypeHeaderValue.Parse(string.IsNullOrWhiteSpace(request.ContentType)
                ? "application/json"
                : request.ContentType);
            message.Content = payload;
        }

        try
        {
            using var client = _httpClientFactory.CreateClient();
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";

            return new PlatformCommandDispatchResult(
                Succeeded: response.IsSuccessStatusCode,
                ResponseStatusCode: (int)response.StatusCode,
                ResponseContentType: contentType,
                ResponseBody: body,
                Error: response.IsSuccessStatusCode ? string.Empty : $"HTTP {(int)response.StatusCode}");
        }
        catch (Exception ex)
        {
            return new PlatformCommandDispatchResult(
                Succeeded: false,
                ResponseStatusCode: null,
                ResponseContentType: string.Empty,
                ResponseBody: string.Empty,
                Error: ex.Message);
        }
    }
}
