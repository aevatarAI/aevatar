// ─────────────────────────────────────────────────────────────
// ChatEndpoints — "用户只关心 chat" 的极简 API
//
// POST /api/chat  → 创建或复用 WorkflowGAgent，SSE 返回 AGUI 事件
// GET  /api/agents → 列出活跃 Agent
// GET  /api/workflows → 列出可用工作流
// ─────────────────────────────────────────────────────────────

using Aevatar.AGUI;
using Aevatar.AI;
using Aevatar.Api.Projection;
using Aevatar.Api.Reporting;
using Aevatar.Api.Workflows;
using Aevatar.Cognitive;
using Aevatar.Config;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Aevatar.Api.Endpoints;

/// <summary>请求体：用户发送的 chat 消息。</summary>
public sealed record ChatInput
{
    /// <summary>用户提示词。</summary>
    public required string Prompt { get; init; }

    /// <summary>工作流名称。agentId 为空时必填；未传时默认为 "direct"（直接对话）。</summary>
    public string? Workflow { get; init; }

    /// <summary>复用已有 Agent 的 ID。空则新建。</summary>
    public string? AgentId { get; init; }
}

public static class ChatEndpoints
{
    public static IEndpointRouteBuilder MapChatEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api").WithTags("Chat");

        group.MapPost("/chat", HandleChat)
            .Produces(StatusCodes.Status200OK, contentType: "text/event-stream")
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/agents", ListAgents)
            .Produces(StatusCodes.Status200OK);

        group.MapGet("/workflows", ListWorkflows)
            .Produces(StatusCodes.Status200OK);

        return app;
    }

    // ─── POST /api/chat ───

    private static async Task HandleChat(
        HttpContext http,
        ChatInput input,
        IActorRuntime runtime,
        IStreamProvider streams,
        WorkflowRegistry registry,
        CancellationToken ct)
    {
        // ─── 1. 创建或复用 Agent ───

        IActor actor;
        if (!string.IsNullOrWhiteSpace(input.AgentId))
        {
            var existing = await runtime.GetAsync(input.AgentId);
            if (existing == null)
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            actor = existing;
        }
        else
        {
            // 未传 workflow 时使用默认工作流 "direct"（直接对话）
            var workflowName = string.IsNullOrWhiteSpace(input.Workflow) ? "direct" : input.Workflow;

            var yaml = registry.GetYaml(workflowName);
            if (yaml == null)
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            actor = await runtime.CreateAsync<WorkflowGAgent>(ct: ct);

            // Initialize workflow via event (RPC-safe: works for Local + Orleans)
            var setWf = new SetWorkflowEvent
            {
                WorkflowYaml = yaml,
                WorkflowName = workflowName,
            };
            var initEnvelope = new EventEnvelope
            {
                Id = Guid.NewGuid().ToString("N"),
                Timestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(DateTime.UtcNow),
                Payload = Google.Protobuf.WellKnownTypes.Any.Pack(setWf),
                PublisherId = "api",
                Direction = EventDirection.Self,
            };
            await actor.HandleEventAsync(initEnvelope, ct);
        }

        // ─── 2. 准备 SSE 响应 ───

        http.Response.StatusCode = StatusCodes.Status200OK;
        http.Response.Headers.ContentType = "text/event-stream; charset=utf-8";
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers.Pragma = "no-cache";
        http.Response.Headers["X-Accel-Buffering"] = "no";
        await http.Response.StartAsync(ct);

        await using var sink = new AgUiEventChannel();
        await using var writer = new AgUiSseWriter(http.Response);

        // ─── 3. 订阅 Agent Stream（捕获所有子 Agent 事件，并记录到 Recorder 用于报告） ───

        var recorder = new ChatRunRecorder(actor.Id);
        var stream = streams.GetStream(actor.Id);
        var subscription = await stream.SubscribeAsync<EventEnvelope>(envelope =>
        {
            recorder.Record(envelope);
            foreach (var agUiEvt in AgUiProjector.Project(envelope))
                sink.Push(agUiEvt);
            return Task.CompletedTask;
        }, ct);

        // ─── 4. 发送 RUN_STARTED + ChatRequestEvent ───

        var runId = Guid.NewGuid().ToString("N");
        var startedAt = DateTimeOffset.UtcNow;
        var ts = startedAt.ToUnixTimeMilliseconds();

        await writer.WriteAsync(new RunStartedEvent
        {
            Timestamp = ts,
            ThreadId = actor.Id,
            RunId = runId,
        }, ct);

        // 构造 ChatRequestEvent → 发送到 Agent
        var chatEvt = new ChatRequestEvent { Prompt = input.Prompt };
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(chatEvt),
            PublisherId = "api",
            Direction = EventDirection.Self,
        };

        // 非阻塞：在后台启动事件处理；sink 由订阅持续推送，直到 WorkflowCompleted → RunFinishedEvent
        var processingTask = Task.Run(async () =>
        {
            try
            {
                await actor.HandleEventAsync(envelope, ct);
            }
            catch (Exception)
            {
                sink.Push(new RunErrorEvent
                {
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Message = "工作流执行异常",
                    Code = "INTERNAL_ERROR",
                });
                sink.Complete();
            }
        }, ct);

        // ─── 5. 流式写出 AGUI 事件 ───

        try
        {
            await foreach (var evt in sink.ReadAllAsync(ct))
            {
                await writer.WriteAsync(evt, ct);

                // RUN_FINISHED / RUN_ERROR 是终止信号
                if (evt is RunFinishedEvent or RunErrorEvent) break;
            }
        }
        catch (OperationCanceledException) { /* 客户端断开 */ }

        // ─── 6. 生成并写入执行报告（artifacts/chat-runs） ───

        var workflowNameForReport = string.IsNullOrWhiteSpace(input.Workflow) ? "direct" : input.Workflow;
        if (!string.IsNullOrWhiteSpace(workflowNameForReport))
        {
            try
            {
                var allActors = await runtime.GetAllAsync();
                var topology = new List<ChatTopologyEdge>();
                foreach (var a in allActors)
                {
                    var parent = await a.GetParentIdAsync();
                    if (!string.IsNullOrWhiteSpace(parent))
                        topology.Add(new ChatTopologyEdge(parent, a.Id));
                }

                var report = recorder.BuildReport(workflowNameForReport, runId, startedAt, input.Prompt, topology);
                var outputDir = Path.Combine(AevatarPaths.RepoRoot, "artifacts", "chat-runs");
                var (jsonPath, htmlPath) = ChatRunReportWriter.BuildDefaultPaths(outputDir);
                await ChatRunReportWriter.WriteAsync(report, jsonPath, htmlPath);

                var loggerFactory = http.RequestServices.GetService(typeof(ILoggerFactory)) as ILoggerFactory;
                loggerFactory?.CreateLogger("Aevatar.Api.Chat").LogInformation("Chat run report saved: json={JsonPath}, html={HtmlPath}", jsonPath, htmlPath);
            }
            catch (Exception ex)
            {
                var loggerFactory = http.RequestServices.GetService(typeof(ILoggerFactory)) as ILoggerFactory;
                loggerFactory?.CreateLogger("Aevatar.Api.Chat").LogWarning(ex, "Failed to write chat run report");
            }
        }

        // ─── 7. 清理 ───

        await subscription.DisposeAsync();
        await processingTask;
    }

    // ─── GET /api/agents ───

    private static async Task<IResult> ListAgents(IActorRuntime runtime)
    {
        var actors = await runtime.GetAllAsync();
        var result = new List<object>();
        foreach (var actor in actors)
        {
            var desc = await actor.GetDescriptionAsync();
            var typeName = await actor.GetAgentTypeNameAsync();
            result.Add(new
            {
                id = actor.Id,
                type = typeName,
                description = desc,
            });
        }
        return Results.Ok(result);
    }

    // ─── GET /api/workflows ───

    private static IResult ListWorkflows(WorkflowRegistry registry) =>
        Results.Ok(registry.GetNames());
}
