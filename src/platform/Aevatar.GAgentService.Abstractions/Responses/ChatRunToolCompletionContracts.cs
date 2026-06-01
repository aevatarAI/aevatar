using Aevatar.AI.Abstractions.LLMProviders;

namespace Aevatar.GAgentService.Abstractions.Responses;

// Refactor (issue1631-first): wait=complete keeps the public tool receipt contract,
// while ChatRun actor-owned auto-resume remains a later product/protocol decision.
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

// Refactor (issue1631-first): chat-run-aware invocation tools can return typed
// accepted/streaming receipt fields without registering a ChatRun continuation actor.
public interface IChatRunToolCompletionControlExecutor
{
    Task<ChatRunToolCompletionRequest> ExecuteForChatRunAsync(
        ChatRunToolCompletionRequest request,
        CancellationToken ct = default);
}
