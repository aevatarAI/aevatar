using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Aevatar.GAgents.StreamingProxy;

internal sealed class StreamingProxySseWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly HttpResponse _response;
    private bool _started;

    public StreamingProxySseWriter(HttpResponse response)
    {
        _response = response;
    }

    public bool Started => _started;

    public async ValueTask StartAsync(CancellationToken ct = default)
    {
        if (_started)
            return;

        _started = true;
        _response.StatusCode = StatusCodes.Status200OK;
        _response.Headers.ContentType = "text/event-stream; charset=utf-8";
        _response.Headers.CacheControl = "no-store";
        _response.Headers.Pragma = "no-cache";
        _response.Headers["X-Accel-Buffering"] = "no";
        await _response.StartAsync(ct);
    }

    public async ValueTask WriteFrameAsync(object frame, CancellationToken ct = default)
    {
        await StartAsync(ct);
        var json = JsonSerializer.Serialize(frame, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes($"data: {json}\n\n");
        await _response.Body.WriteAsync(bytes, ct);
        await _response.Body.FlushAsync(ct);
    }

    public ValueTask WriteRoomCreatedAsync(string roomId, string roomName, CancellationToken ct) =>
        WriteFrameAsync(new { type = "ROOM_CREATED", roomId, roomName }, ct);

    public ValueTask WriteTopicStartedAsync(string prompt, string sessionId, CancellationToken ct) =>
        WriteFrameAsync(new { type = "TOPIC_STARTED", prompt, sessionId }, ct);

    public ValueTask WriteAgentMessageAsync(string agentId, string agentName, string content, long sequence, CancellationToken ct) =>
        WriteFrameAsync(new { type = "AGENT_MESSAGE", agentId, agentName, content, sequence }, ct);

    public ValueTask WriteParticipantJoinedAsync(string agentId, string displayName, CancellationToken ct) =>
        WriteFrameAsync(new { type = "PARTICIPANT_JOINED", agentId, displayName }, ct);

    public ValueTask WriteParticipantLeftAsync(string agentId, CancellationToken ct) =>
        WriteFrameAsync(new { type = "PARTICIPANT_LEFT", agentId }, ct);

    public ValueTask WriteRunFinishedAsync(CancellationToken ct) =>
        WriteFrameAsync(new { type = "RUN_FINISHED" }, ct);

    public ValueTask WriteRunErrorAsync(string message, CancellationToken ct) =>
        WriteFrameAsync(new { type = "RUN_ERROR", runError = new { message } }, ct);
}
