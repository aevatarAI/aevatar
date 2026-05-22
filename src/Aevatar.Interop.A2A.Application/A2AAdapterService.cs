using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Interop.A2A.Abstractions;
using Aevatar.Interop.A2A.Abstractions.Models;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Interop.A2A.Application;

// Refactor (iter30/cluster-031-a2a-actor-owned):
//   Old pattern: A2A task lifecycle used IA2ATaskStore as process-local truth.
//   New principle: task-scoped GAgent owns lifecycle; adapter dispatches typed commands and reads materialized facts.
public sealed class A2AAdapterService : IA2AAdapterService
{
    private readonly IA2ATaskCommandPort _taskCommandPort;
    private readonly IProjectionDocumentReader<A2ATaskCurrentStateReadModel, string> _taskReadModelReader;
    private readonly IActorEventSubscriptionProvider _subscriptionProvider;
    private readonly ILogger _logger;

    public A2AAdapterService(
        IA2ATaskCommandPort taskCommandPort,
        IProjectionDocumentReader<A2ATaskCurrentStateReadModel, string> taskReadModelReader,
        IActorEventSubscriptionProvider subscriptionProvider,
        ILogger<A2AAdapterService>? logger = null)
    {
        _taskCommandPort = taskCommandPort ?? throw new ArgumentNullException(nameof(taskCommandPort));
        _taskReadModelReader = taskReadModelReader ?? throw new ArgumentNullException(nameof(taskReadModelReader));
        _subscriptionProvider = subscriptionProvider ?? throw new ArgumentNullException(nameof(subscriptionProvider));
        _logger = logger ?? NullLogger<A2AAdapterService>.Instance;
    }

    public async Task<A2ATask> SendTaskAsync(TaskSendParams sendParams, CancellationToken ct = default)
    {
        // Refactor (iter30/cluster-031-a2a-actor-owned):
        //   Old pattern: tasks/send synchronously created ledger row then marked working/failed.
        //   New principle: return honest submitted receipt after typed command reaches task actor inbox.
        ArgumentNullException.ThrowIfNull(sendParams);
        var prompt = ExtractTextFromMessage(sendParams.Message);
        if (string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentException("Message must contain at least one text part.");

        var targetActorId = ResolveTargetActorId(sendParams);
        if (string.IsNullOrWhiteSpace(targetActorId))
            throw new ArgumentException("Target agent ID must be specified in metadata['agentId'] or sessionId.");

        var now = Timestamp.FromDateTime(DateTime.UtcNow);
        var commandId = Guid.NewGuid().ToString("N");
        var command = new A2ATaskSubmitCommand
        {
            TaskId = sendParams.Id,
            SessionId = sendParams.SessionId ?? string.Empty,
            TargetActorId = targetActorId,
            CommandId = commandId,
            CorrelationId = sendParams.Id,
            Message = A2ATaskModelMapper.ToProto(sendParams.Message),
            RequestedAt = now,
        };
        if (sendParams.Metadata != null)
            command.Metadata.Add(sendParams.Metadata);

        var taskActorId = await _taskCommandPort.SubmitAsync(command, ct);
        _logger.LogDebug("A2A task {TaskId} submitted to task actor {TaskActorId}", sendParams.Id, taskActorId);

        return A2ATaskModelMapper.ToDto(new A2ATaskState
        {
            TaskId = sendParams.Id,
            SessionId = sendParams.SessionId ?? string.Empty,
            TargetActorId = targetActorId,
            CommandId = commandId,
            CorrelationId = sendParams.Id,
            Status = A2ATaskModelMapper.BuildStatus(A2ATaskLifecycleState.Submitted, now),
            UpdatedAt = now,
        });
    }

    public async Task<A2ATask?> GetTaskAsync(TaskQueryParams queryParams, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(queryParams);
        var document = await _taskReadModelReader.GetAsync(A2ATaskActorId.Build(queryParams.Id), ct);
        return document?.State == null
            ? null
            : A2ATaskModelMapper.ToDto(document.State, queryParams.HistoryLength);
    }

    public async Task<A2ATask> CancelTaskAsync(TaskIdParams idParams, CancellationToken ct = default)
    {
        // Refactor (iter30/cluster-031-a2a-actor-owned):
        //   Old pattern: tasks/cancel synchronously mutated IA2ATaskStore state.
        //   New principle: dispatch cancel command; lifecycle result is observed via readmodel/update stream.
        ArgumentNullException.ThrowIfNull(idParams);
        var existing = await GetTaskAsync(new TaskQueryParams { Id = idParams.Id }, ct);
        if (existing == null)
            throw new KeyNotFoundException($"Task '{idParams.Id}' not found.");
        if (existing.Status.State is TaskState.Completed or TaskState.Failed or TaskState.Canceled)
            throw new InvalidOperationException($"Task '{idParams.Id}' is in terminal state '{existing.Status.State}' and cannot be canceled.");

        var now = Timestamp.FromDateTime(DateTime.UtcNow);
        var commandId = Guid.NewGuid().ToString("N");
        var command = new A2ATaskCancelCommand
        {
            TaskId = idParams.Id,
            CommandId = commandId,
            CorrelationId = idParams.Id,
            RequestedAt = now,
        };

        await _taskCommandPort.CancelAsync(command, ct);

        return new A2ATask
        {
            Id = existing.Id,
            SessionId = existing.SessionId,
            Status = new A2ATaskStatus
            {
                State = TaskState.Submitted,
                Timestamp = now.ToDateTime().ToString("O"),
            },
            History = existing.History,
            Artifacts = existing.Artifacts,
            Metadata = existing.Metadata,
        };
    }

    public Task<IAsyncDisposable> SubscribeTaskUpdatesAsync(
        string taskId,
        Func<A2ATaskUpdate, Task> handler,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentNullException.ThrowIfNull(handler);
        return _subscriptionProvider.SubscribeAsync(A2ATaskActorId.Build(taskId), handler, ct);
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

}
