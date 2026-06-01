using Aevatar.AI.Abstractions.LLMProviders;

namespace Aevatar.GAgentService.Abstractions.Responses;

// Refactor (iter290/cluster001): Old pattern: wait=complete routed through ChatRun
// actor/coordinator continuation scaffolding. New principle: invocation tools return
// accepted/streaming receipt fields on the live tool execution path; completion is
// observed through read models instead of a ChatRun continuation shim.
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
