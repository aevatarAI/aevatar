using Aevatar.AI.Abstractions.LLMProviders;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Abstractions.Responses;

public sealed record ChatRunStartRequest(
    string ResponseId,
    string? ModelName,
    IReadOnlyList<ChatMessage> Messages,
    TimeSpan? IdleTtl = null);

// Refactor (iter290/cluster001): Old pattern: chat-run completion control was parsed back out of ResultJson. New principle: completion control crosses the public contract as typed scalar fields.
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

// Refactor (iter290/cluster001): Old pattern: chat-run tools returned only boundary JSON to the coordinator. New principle: chat-run-aware tools return a typed completion request for command observation.
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
