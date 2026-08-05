using System.Security.Cryptography;
using System.Text;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Tools;
using Aevatar.Foundation.Abstractions.Tools;

namespace Aevatar.AI.Core.Chat;

public sealed class ChatToolPostExternalCheckpointException : InvalidOperationException
{
    public ChatToolPostExternalCheckpointException(
        string message,
        bool permanentMaterialFailure,
        Exception innerException)
        : base(message, innerException)
    {
        PermanentMaterialFailure = permanentMaterialFailure;
    }

    public bool PermanentMaterialFailure { get; }
}

public sealed record ChatToolOperationIntent(
    ToolCall ToolCall,
    AgentToolExecutionContext ExecutionContext,
    AgentToolReplayPolicy ReplayPolicy,
    ToolPresentationDescriptor Presentation);

public sealed record ChatToolBatchIntent(
    string SessionId,
    int Round,
    IReadOnlyList<ChatToolOperationIntent> Operations);

public sealed record PreparedChatToolOperation(
    string SessionId,
    int Round,
    string OperationId,
    ToolCall ToolCall,
    AgentToolExecutionContext ExecutionContext,
    AgentToolReplayPolicy ReplayPolicy,
    ToolPresentationDescriptor Presentation,
    AgentToolExecutionAttemptKind ExecutionAttemptKind = AgentToolExecutionAttemptKind.Initial);

public interface IChatToolCheckpointPort
{
    Task<IReadOnlyList<PreparedChatToolOperation>> PrepareBatchAsync(
        ChatToolBatchIntent batch,
        CancellationToken ct = default);

    Task CommitCompletionAsync(
        PreparedChatToolOperation operation,
        ToolExecutionResult result,
        CancellationToken ct = default);
}

public sealed class NoOpChatToolCheckpointPort : IChatToolCheckpointPort
{
    public static NoOpChatToolCheckpointPort Instance { get; } = new();

    private NoOpChatToolCheckpointPort()
    {
    }

    public Task<IReadOnlyList<PreparedChatToolOperation>> PrepareBatchAsync(
        ChatToolBatchIntent batch,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ct.ThrowIfCancellationRequested();

        IReadOnlyList<PreparedChatToolOperation> prepared = batch.Operations
            .Select((intent, index) => Prepare(batch, intent, index))
            .ToArray();
        return Task.FromResult(prepared);
    }

    public Task CommitCompletionAsync(
        PreparedChatToolOperation operation,
        ToolExecutionResult result,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private static PreparedChatToolOperation Prepare(
        ChatToolBatchIntent batch,
        ChatToolOperationIntent intent,
        int index)
    {
        var operationId = BuildOperationId(batch.SessionId, batch.Round, intent.ToolCall.Id, index);
        var request = intent.ExecutionContext.Request with
        {
            CallId = intent.ToolCall.Id,
            OperationId = operationId,
            IdempotencyKey = intent.ReplayPolicy == AgentToolReplayPolicy.IdempotentRetryable
                ? operationId
                : intent.ExecutionContext.Request.IdempotencyKey,
        };
        return new PreparedChatToolOperation(
            batch.SessionId,
            batch.Round,
            operationId,
            CloneToolCall(intent.ToolCall),
            intent.ExecutionContext with { Request = request },
            intent.ReplayPolicy,
            intent.Presentation.Clone());
    }

    private static string BuildOperationId(string sessionId, int round, string callId, int index)
    {
        var material = $"{sessionId}\n{round}\n{index}\n{callId}";
        return "tool:v1:operation:" +
               Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    private static ToolCall CloneToolCall(ToolCall toolCall) => new()
    {
        Id = toolCall.Id,
        Name = toolCall.Name,
        ArgumentsJson = toolCall.ArgumentsJson,
    };
}
