using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Aevatar.Capabilities.ExecutionActivity;

public sealed class ExecutionActivityEventJsonSseWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpResponse _response;

    public ExecutionActivityEventJsonSseWriter(HttpResponse response)
    {
        _response = response ?? throw new ArgumentNullException(nameof(response));
    }

    public async Task WriteAsync(string eventName, object payload, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        ArgumentNullException.ThrowIfNull(payload);

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes($"event: {eventName}\ndata: {json}\n\n");
        await _response.Body.WriteAsync(bytes, ct);
        await _response.Body.FlushAsync(ct);
    }
}
