// ─────────────────────────────────────────────────────────────
// A2AAdapterService — bidirectional conversion between the A2A protocol and internal EventEnvelope
//
// Maps A2A tasks/send to IActorDispatchPort.DispatchAsync,
// wraps internal ChatRequestEvent as an EventEnvelope and dispatches it to the target GAgent.
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
        // 1. Extract the text prompt from the message
        var prompt = ExtractTextFromMessage(sendParams.Message);
        if (string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentException("Message must contain at least one text part.");

        // 2. Resolve the target actor ID (from metadata or session)
        var targetActorId = ResolveTargetActorId(sendParams);
        if (string.IsNullOrWhiteSpace(targetActorId))
            throw new ArgumentException("Target agent ID must be specified in metadata['agentId'] or sessionId.");

        // 3. Create the task record
        var task = await _taskStore.CreateTaskAsync(sendParams.Id, sendParams.SessionId, sendParams.Message, ct);

        // 4. Build the EventEnvelope and dispatch it
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

        // Trim by historyLength
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
        // Safely create ChatRequestEvent via reflection (avoiding a direct dependency on AI.Abstractions)
        // The actual proto type is Aevatar.AI.Abstractions.ChatRequestEvent
        // But this layer dispatches generically through Foundation Abstractions Any.Pack
        //
        // Because the Application layer does not directly depend on AI.Abstractions (to keep layering clean),
        // we build a generic message from agent_messages.proto.
        // Callers can use AgentMessage or build ChatRequestEvent directly.
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
