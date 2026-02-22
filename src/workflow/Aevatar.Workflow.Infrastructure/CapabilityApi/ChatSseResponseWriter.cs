using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Microsoft.AspNetCore.Http;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

internal sealed class ChatSseResponseWriter
{
    private static readonly JsonSerializerOptions OutputJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpResponse _response;
    private bool _started;

    public ChatSseResponseWriter(HttpResponse response)
    {
        _response = response;
    }

    public bool Started => _started;

    public ValueTask StartAsync(CancellationToken ct = default)
    {
        if (_started)
            return ValueTask.CompletedTask;

        _started = true;
        _response.StatusCode = StatusCodes.Status200OK;
        _response.Headers.ContentType = "text/event-stream; charset=utf-8";
        _response.Headers.CacheControl = "no-store";
        _response.Headers.Pragma = "no-cache";
        _response.Headers["X-Accel-Buffering"] = "no";
        return new ValueTask(_response.StartAsync(ct));
    }

    public async ValueTask WriteAsync(WorkflowOutputFrame frame, CancellationToken ct = default)
    {
        await StartAsync(ct);
        var payload = JsonSerializer.Serialize(frame, OutputJsonOptions);
        var bytes = Encoding.UTF8.GetBytes($"data: {payload}\n\n");
        await _response.Body.WriteAsync(bytes, ct);
        await _response.Body.FlushAsync(ct);
    }
}
