using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.AGUI.Contracts;

namespace Aevatar.GAgentService.Abstractions.ScopeGAgents;

// Refactor (iter1353/cluster-001): Old pattern: draft-run requests used scalar legacy control fields and payload headers for trusted facts.
// New principle: requests preserve typed ToolContext and LlmControl through the application hop.
public sealed record GAgentDraftRunInteractionRequest(
    string ScopeId,
    string AgentKind,
    string Prompt,
    string? PreferredActorId = null,
    string? SessionId = null,
    string? NyxIdAccessToken = null,
    string? ModelOverride = null,
    string? PreferredLlmRoute = null,
    IReadOnlyDictionary<string, string>? Headers = null,
    IReadOnlyList<GAgentDraftRunInputPart>? InputParts = null,
    AgentToolExecutionContext? ToolContext = null,
    LLMControlContext? LlmControl = null,
    bool UseCorrelationIdAsFallbackSessionId = true);

public sealed record GAgentDraftRunPreparedActor(
    string ScopeId,
    string AgentKind,
    string ActorId,
    bool RequiresRollbackOnFailure);

public interface IGAgentDraftRunInteractionPort
{
    Task<CommandInteractionResult<GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError, GAgentDraftRunCompletionStatus>> ExecuteAsync(
        GAgentDraftRunInteractionRequest request,
        Func<AGUIEvent, CancellationToken, ValueTask> emitAsync,
        Func<GAgentDraftRunAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync = null,
        CancellationToken ct = default);
}
