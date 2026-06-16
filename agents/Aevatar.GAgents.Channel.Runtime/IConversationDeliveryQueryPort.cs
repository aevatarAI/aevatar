namespace Aevatar.GAgents.Channel.Runtime;

public interface IConversationDeliveryQueryPort
{
    Task<ConversationDeliveryCurrentStateDocument?> GetAsync(string actorId, CancellationToken ct = default);
}
