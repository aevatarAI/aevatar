using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.GAgents.ChatHistory;

public sealed record ChatConversationContinuationAdmission(
    bool CanContinue,
    WorkflowConversationExecutionContext? ConversationContext)
{
    public static ChatConversationContinuationAdmission NotFound() =>
        new(false, null);

    public static ChatConversationContinuationAdmission Found(
        WorkflowConversationExecutionContext conversationContext) =>
        new(true, conversationContext);
}

public interface IChatConversationContinuationAdmissionReader
{
    Task<ChatConversationContinuationAdmission> GetContinuationAsync(
        string scopeId,
        string conversationId,
        CancellationToken ct = default);
}
