using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.GAgents.ChatHistory;

public enum ChatConversationContinuationAdmissionFailure
{
    None = 0,
    NotFound = 1,
    ReadModelNotReady = 2,
}

public sealed record ChatConversationContinuationAdmission(
    bool CanContinue,
    WorkflowConversationExecutionContext? ConversationContext,
    ChatConversationContinuationAdmissionFailure Failure)
{
    public static ChatConversationContinuationAdmission NotFound() =>
        new(false, null, ChatConversationContinuationAdmissionFailure.NotFound);

    public static ChatConversationContinuationAdmission NotReady() =>
        new(false, null, ChatConversationContinuationAdmissionFailure.ReadModelNotReady);

    public static ChatConversationContinuationAdmission Found(
        WorkflowConversationExecutionContext conversationContext) =>
        new(true, conversationContext, ChatConversationContinuationAdmissionFailure.None);
}

public interface IChatConversationContinuationAdmissionReader
{
    Task<ChatConversationContinuationAdmission> GetContinuationAsync(
        string scopeId,
        string conversationId,
        long minimumStateVersion,
        CancellationToken ct = default);
}
