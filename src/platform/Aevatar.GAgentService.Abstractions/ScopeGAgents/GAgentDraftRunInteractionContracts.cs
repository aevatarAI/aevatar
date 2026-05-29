using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.Presentation.AGUI;

namespace Aevatar.GAgentService.Abstractions.ScopeGAgents;

public sealed record GAgentDraftRunInteractionRequest(
    string ScopeId,
    string ActorTypeName,
    string Prompt,
    string? PreferredActorId = null,
    string? SessionId = null,
    string? NyxIdAccessToken = null,
    string? ModelOverride = null,
    string? PreferredLlmRoute = null,
    IReadOnlyList<GAgentDraftRunInputPart>? InputParts = null,
    AgentToolExecutionContext? ToolContext = null,
    LLMControlContext? LlmControl = null);

public sealed record GAgentDraftRunPreparedActor(
    string ScopeId,
    string ActorTypeName,
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
