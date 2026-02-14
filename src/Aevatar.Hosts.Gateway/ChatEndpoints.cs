// ─────────────────────────────────────────────────────────────
// ChatEndpoints — Chat HTTP 端点
// 极薄转换层：用户消息 ↔ EventEnvelope
// ─────────────────────────────────────────────────────────────

using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core;
using Aevatar.AI.Abstractions;
using Aevatar.Workflows.Core;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Mvc;

namespace Aevatar.Hosts.Gateway;

/// <summary>Chat HTTP 端点。Gateway 的唯一业务入口。</summary>
public static class ChatEndpoints
{
    /// <summary>注册所有 Chat 端点。</summary>
    public static void MapChatEndpoints(this WebApplication app)
    {
        // ─── 发送消息 ───
        app.MapPost("/api/chat/{agentId}", async (
            string agentId,
            [FromBody] ChatRequest request,
            [FromServices] IActorRuntime runtime,
            CancellationToken ct) =>
        {
            var actor = await runtime.GetAsync(agentId);
            if (actor == null) return Results.NotFound($"Agent {agentId} 不存在");

            var envelope = new EventEnvelope
            {
                Id = Guid.NewGuid().ToString("N"),
                Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
                Payload = Any.Pack(new ChatRequestEvent
                {
                    Prompt = request.Message,
                    SessionId = request.SessionId ?? agentId,
                }),
                PublisherId = "gateway",
                Direction = EventDirection.Self,
            };

            await actor.HandleEventAsync(envelope, ct);
            return Results.Accepted();
        });

        // ─── 流式响应（SSE） ───
        app.MapGet("/api/chat/{agentId}/stream", async (
            string agentId,
            [FromServices] IActorRuntime runtime,
            [FromServices] IStreamProvider streams,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var actor = await runtime.GetAsync(agentId);
            if (actor == null) { httpContext.Response.StatusCode = 404; return; }

            httpContext.Response.ContentType = "text/event-stream";
            httpContext.Response.Headers.CacheControl = "no-cache";

            var stream = streams.GetStream(agentId);
            var tcs = new TaskCompletionSource();

            await using var sub = await stream.SubscribeAsync<EventEnvelope>(async envelope =>
            {
                var typeUrl = envelope.Payload?.TypeUrl;
                if (typeUrl == null) return;

                // AG-UI: TextMessageStart → 通知客户端流开始
                if (typeUrl.Contains("TextMessageStartEvent"))
                {
                    var evt = envelope.Payload.Unpack<TextMessageStartEvent>();
                    var data = System.Text.Json.JsonSerializer.Serialize(new { type = "TEXT_MESSAGE_START", session_id = evt.SessionId, agent_id = evt.AgentId });
                    await httpContext.Response.WriteAsync($"data: {data}\n\n", ct);
                    await httpContext.Response.Body.FlushAsync(ct);
                }
                // AG-UI: TextMessageContent → 增量 token
                else if (typeUrl.Contains("TextMessageContentEvent"))
                {
                    var evt = envelope.Payload.Unpack<TextMessageContentEvent>();
                    var data = System.Text.Json.JsonSerializer.Serialize(new { type = "TEXT_MESSAGE_CONTENT", delta = evt.Delta });
                    await httpContext.Response.WriteAsync($"data: {data}\n\n", ct);
                    await httpContext.Response.Body.FlushAsync(ct);
                }
                // AG-UI: TextMessageEnd → 流结束
                else if (typeUrl.Contains("TextMessageEndEvent"))
                {
                    var evt = envelope.Payload.Unpack<TextMessageEndEvent>();
                    var data = System.Text.Json.JsonSerializer.Serialize(new { type = "TEXT_MESSAGE_END", content = evt.Content });
                    await httpContext.Response.WriteAsync($"data: {data}\n\n", ct);
                    await httpContext.Response.Body.FlushAsync(ct);
                    tcs.TrySetResult();
                }
                // 兼容：非流式 ChatResponseEvent
                else if (typeUrl.Contains("ChatResponseEvent"))
                {
                    var evt = envelope.Payload.Unpack<ChatResponseEvent>();
                    var data = System.Text.Json.JsonSerializer.Serialize(new { type = "CHAT_RESPONSE", content = evt.Content });
                    await httpContext.Response.WriteAsync($"data: {data}\n\n", ct);
                    await httpContext.Response.Body.FlushAsync(ct);
                    tcs.TrySetResult();
                }
            }, ct);

            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            try { await tcs.Task.WaitAsync(linked.Token); }
            catch (OperationCanceledException) { }
        });

        // ─── 创建 WorkflowGAgent ───
        app.MapPost("/api/agents/workflow", async (
            [FromBody] CreateWorkflowRequest request,
            [FromServices] IActorRuntime runtime,
            CancellationToken ct) =>
        {
            var actor = await runtime.CreateAsync<WorkflowGAgent>(request.Id, ct);
            return Results.Created($"/api/agents/{actor.Id}", new { id = actor.Id });
        });

        // ─── 列出所有 Agent ───
        app.MapGet("/api/agents", async ([FromServices] IActorRuntime runtime) =>
        {
            var actors = await runtime.GetAllAsync();
            return Results.Ok(actors.Select(a => new { id = a.Id, type = a.Agent.GetType().Name }));
        });
    }
}

/// <summary>Chat 请求体。</summary>
public sealed class ChatRequest
{
    public required string Message { get; init; }
    public string? SessionId { get; init; }
}

/// <summary>创建 WorkflowGAgent 请求体。</summary>
public sealed class CreateWorkflowRequest
{
    public string? Id { get; init; }
    public string? WorkflowYaml { get; init; }
}
