using Aevatar.AI.Abstractions.LLMProviders;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Abstractions.Responses;

public sealed record ChatRunStartRequest(
    string ResponseId,
    string? ModelName,
    IReadOnlyList<ChatMessage> Messages,
    TimeSpan? IdleTtl = null);

// Refactor (issue1298-first): Old: ResultJson string control parsing. New: typed scalar dispatch fields.
public sealed record ChatRunToolCompletionRequest(
    string ResponseId,
    string? ModelName,
    IReadOnlyList<ChatMessage> Messages,
    ToolCall ToolCall,
    string ArgumentsJson,
    string ToolExecutionResultJson,
    int LlmRound,
    string RunId = "",
    string StreamTopic = "",
    string ActorId = "",
    string ServiceId = "",
    string EndpointId = "",
    string ScopeId = "",
    ChatRunSubRunWaitMode WaitMode = ChatRunSubRunWaitMode.Unspecified,
    string Status = "",
    string CompletionResultJson = "",
    bool CompletionObserved = false,
    string ErrorCode = "");

// Refactor (issue1298-first): Old: ResultJson string control parsing. New: typed scalar dispatch fields.
public interface IChatRunToolCompletionControlExecutor
{
    Task<ChatRunToolCompletionRequest> ExecuteForChatRunAsync(
        ChatRunToolCompletionRequest request,
        CancellationToken ct = default);
}

public sealed record ChatRunToolCompletionResult(
    string ActorId,
    string RunId,
    string ToolCallId,
    string ToolName,
    string ResultJson);

public interface IChatRunActorPort
{
    Task<string> StartAsync(ChatRunStartRequest request, CancellationToken ct = default);

    Task SubmitToolCallAsync(
        string chatRunActorId,
        ChatRunToolCompletionRequest request,
        CancellationToken ct = default);

    Task BeginSubRunObservationAsync(
        string chatRunActorId,
        ChatRunToolCompletionRequest request,
        CancellationToken ct = default);

    Task ObserveSubRunTerminalAsync(
        string chatRunActorId,
        ChatRunSubRunTerminalObserved observed,
        CancellationToken ct = default);

    Task TerminateAsync(
        string chatRunActorId,
        string reason,
        CancellationToken ct = default);
}

public interface IChatRunToolCompletionSink
{
    ValueTask OnToolResultReadyAsync(
        ChatRunToolResultReady ready,
        CancellationToken ct = default);
}
