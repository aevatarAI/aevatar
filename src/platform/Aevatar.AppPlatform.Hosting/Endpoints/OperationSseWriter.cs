using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Aevatar.AppPlatform.Hosting.Endpoints;

internal sealed class OperationSseWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpResponse _response;

    public OperationSseWriter(HttpResponse response)
    {
        _response = response ?? throw new ArgumentNullException(nameof(response));
    }

    public async Task WriteAsync(string eventName, object payload, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        ArgumentNullException.ThrowIfNull(payload);

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        await _response.WriteAsync($"event: {eventName}\n", ct);
        await _response.WriteAsync($"data: {json}\n\n", ct);
        await _response.Body.FlushAsync(ct);
    }
}
