using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Aevatar.GAgents.NyxidChat;

internal sealed class NyxIdChatSseWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly HttpResponse _response;
    private bool _started;

    public NyxIdChatSseWriter(HttpResponse response)
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

    public ValueTask WriteRunStartedAsync(string actorId, CancellationToken ct) =>
        WriteFrameAsync(new { type = "RUN_STARTED", actorId }, ct);

    public ValueTask WriteTextDeltaAsync(string delta, CancellationToken ct) =>
        WriteFrameAsync(new { type = "TEXT_MESSAGE_CONTENT", textMessageContent = new { delta } }, ct);

    public ValueTask WriteTextStartAsync(string messageId, CancellationToken ct) =>
        WriteFrameAsync(new { type = "TEXT_MESSAGE_START", textMessageStart = new { messageId, role = "assistant" } }, ct);

    public ValueTask WriteTextEndAsync(string messageId, CancellationToken ct) =>
        WriteFrameAsync(new { type = "TEXT_MESSAGE_END", textMessageEnd = new { messageId } }, ct);

    public ValueTask WriteRunFinishedAsync(CancellationToken ct) =>
        WriteFrameAsync(new { type = "RUN_FINISHED" }, ct);

    public ValueTask WriteToolCallStartAsync(string toolName, string callId, CancellationToken ct) =>
        WriteFrameAsync(new { type = "TOOL_CALL_START", toolCallStart = new { toolName, toolCallId = callId } }, ct);

    public ValueTask WriteToolCallEndAsync(string callId, string result, CancellationToken ct) =>
        WriteFrameAsync(new { type = "TOOL_CALL_END", toolCallEnd = new { toolCallId = callId, result } }, ct);

    public ValueTask WriteRunErrorAsync(string message, CancellationToken ct) =>
        WriteFrameAsync(new { type = "RUN_ERROR", runError = new { message } }, ct);

    public ValueTask WriteToolApprovalRequestAsync(
        string requestId, string toolName, string toolCallId,
        string argumentsJson, bool isDestructive, int timeoutSeconds,
        CancellationToken ct) =>
        WriteFrameAsync(new
        {
            type = "TOOL_APPROVAL_REQUEST",
            toolApprovalRequest = new
            {
                requestId,
                toolName,
                toolCallId,
                argumentsJson,
                isDestructive,
                timeoutSeconds,
            }
        }, ct);
}
