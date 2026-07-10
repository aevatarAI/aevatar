using Aevatar.AI.Abstractions;

namespace Aevatar.GAgents.NyxidChat;

public interface IAgentToolReceiptRenderer
{
    string Render(IReadOnlyList<AgentToolReceipt> receipts, IReadOnlyList<AgentRunToolCall> toolCalls);
}
