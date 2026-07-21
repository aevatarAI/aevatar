namespace Aevatar.GAgents.ChatHistory;

public interface IChatCreateRecoveryReader
{
    Task<ChatCreateRecoveryRecord?> FindAsync(
        string scopeId,
        string createIdempotencyKey,
        CancellationToken ct = default);
}

public sealed record ChatCreateRecoveryRecord(
    string ScopeId,
    string CreateIdempotencyKey,
    string ConversationId,
    string TurnId,
    string Status,
    long SourceVersion,
    string DeliveryActorId,
    string RequestHash);
