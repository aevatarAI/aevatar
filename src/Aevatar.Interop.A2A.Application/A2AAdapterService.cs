// ─────────────────────────────────────────────────────────────
// A2AAdapterService — A2A 协议 ↔ 内部 EventEnvelope 双向转换
//
// 将 A2A tasks/send 映射到 IActorDispatchPort.DispatchAsync，
// 将内部 ChatRequestEvent 封装为 EventEnvelope 投递到目标 GAgent。
// ─────────────────────────────────────────────────────────────

using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.MultiAgent;
using Aevatar.Interop.A2A.Abstractions;
using Aevatar.Interop.A2A.Abstractions.Models;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Interop.A2A.Application;

public sealed class A2AAdapterService : IA2AAdapterService
{
    private readonly IActorDispatchPort _dispatchPort;
    private readonly IA2ATaskStore _taskStore;
    private readonly ILogger _logger;

    public A2AAdapterService(
        IActorDispatchPort dispatchPort,
        IA2ATaskStore taskStore,
        ILogger<A2AAdapterService>? logger = null)
    {
        _dispatchPort = dispatchPort;
        _taskStore = taskStore;
        _logger = logger ?? NullLogger<A2AAdapterService>.Instance;
    }

    public async Task<A2ATask> SendTaskAsync(TaskSendParams sendParams, CancellationToken ct = default)
    {
        // 1. 从消息中提取文本 prompt
        var prompt = ExtractTextFromMessage(sendParams.Message);
        if (string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentException("Message must contain at least one text part.");

        // 2. 解析目标 actor ID（从 metadata 或 session 中获取）
        var targetActorId = ResolveTargetActorId(sendParams);
        if (string.IsNullOrWhiteSpace(targetActorId))
            throw new ArgumentException("Target agent ID must be specified in metadata['agentId'] or sessionId.");

        // 3. 创建 task 记录
        var task = await _taskStore.CreateTaskAsync(sendParams.Id, sendParams.SessionId, sendParams.Message, ct);

        // 4. 构建 EventEnvelope 并投递
        var chatRequest = BuildChatRequestEvent(prompt, sendParams);
        var envelope = BuildEnvelope(chatRequest, sendParams.Id, targetActorId);

        try
        {
            await _dispatchPort.DispatchAsync(targetActorId, envelope, ct);
            task = await _taskStore.UpdateTaskStateAsync(sendParams.Id, TaskState.Working, ct: ct);
            _logger.LogDebug("A2A task {TaskId} dispatched to actor {ActorId}", sendParams.Id, targetActorId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A2A task {TaskId} dispatch failed", sendParams.Id);
            var errorMessage = new Message
            {
                Role = "agent",
                Parts = [new TextPart { Text = $"Dispatch failed: {ex.Message}" }],
            };
            task = await _taskStore.UpdateTaskStateAsync(sendParams.Id, TaskState.Failed, errorMessage, ct);
        }

        return task;
    }

    public async Task<A2ATask?> GetTaskAsync(TaskQueryParams queryParams, CancellationToken ct = default)
    {
        var task = await _taskStore.GetTaskAsync(queryParams.Id, ct);
        if (task == null) return null;

        // 按 historyLength 截断
        if (queryParams.HistoryLength.HasValue && task.History != null)
        {
            var len = queryParams.HistoryLength.Value;
            if (len >= 0 && len < task.History.Count)
            {
                task.History = task.History.GetRange(task.History.Count - len, len);
            }
        }

        return task;
    }

    public async Task<A2ATask> CancelTaskAsync(TaskIdParams idParams, CancellationToken ct = default)
    {
        var task = await _taskStore.GetTaskAsync(idParams.Id, ct);
        if (task == null)
            throw new KeyNotFoundException($"Task '{idParams.Id}' not found.");

        if (task.Status.State is TaskState.Completed or TaskState.Failed or TaskState.Canceled)
            throw new InvalidOperationException($"Task '{idParams.Id}' is in terminal state '{task.Status.State}' and cannot be canceled.");

        return await _taskStore.UpdateTaskStateAsync(idParams.Id, TaskState.Canceled, ct: ct);
    }

    public AgentCard GetAgentCard(string baseUrl)
    {
        return new AgentCard
        {
            Name = "Aevatar GAgent",
            Description = "Aevatar GAgent accessible via A2A protocol.",
            Url = baseUrl.TrimEnd('/') + "/a2a",
            Version = "1.0.0",
            Capabilities = new AgentCapabilities
            {
                Streaming = true,
                PushNotifications = false,
                StateTransitionHistory = true,
            },
            Skills =
            [
                new AgentSkill
                {
                    Id = "chat",
                    Name = "Chat",
                    Description = "General-purpose conversational agent.",
                    Tags = ["chat", "conversation"],
                },
            ],
        };
    }

    // ─── Private Helpers ───

    private static string ExtractTextFromMessage(Message message)
    {
        var textParts = message.Parts.OfType<TextPart>().Select(p => p.Text);
        return string.Join("\n", textParts);
    }

    private static string? ResolveTargetActorId(TaskSendParams sendParams)
    {
        if (sendParams.Metadata?.TryGetValue("agentId", out var agentId) == true
            && !string.IsNullOrWhiteSpace(agentId))
            return agentId;

        if (!string.IsNullOrWhiteSpace(sendParams.SessionId))
            return sendParams.SessionId;

        return null;
    }

    private static IMessage BuildChatRequestEvent(string prompt, TaskSendParams sendParams)
    {
        // 使用反射安全地创建 ChatRequestEvent（避免对 AI.Abstractions 的直接依赖）
        // 实际 proto 类型为 Aevatar.AI.Abstractions.ChatRequestEvent
        // 但此层通过 Foundation Abstractions 的 Any.Pack 通用投递
        //
        // 由于 Application 层不直接依赖 AI.Abstractions（保持分层清洁），
        // 我们构建一个通用的 agent_messages.proto 中的消息。
        // 调用方可以通过 AgentMessage 或直接构建 ChatRequestEvent。
        var agentMessage = new AgentMessage
        {
            Content = prompt,
            FromAgentId = "a2a-adapter",
        };

        return agentMessage;
    }

    private static EventEnvelope BuildEnvelope(IMessage payload, string correlationId, string targetActorId)
    {
        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(payload),
            Route = EnvelopeRouteSemantics.CreateDirect("a2a-adapter", targetActorId),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = correlationId,
            },
        };
    }
}
