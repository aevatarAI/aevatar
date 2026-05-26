using Aevatar.GAgents.Channel.Runtime;

namespace Aevatar.GAgents.NyxidChat;

public interface IAgentRunReplyGenerationExecutorPort
{
    Task StartAsync(AgentRunReplyGenerationExecutionRequest request, CancellationToken ct);
}

public sealed record AgentRunReplyGenerationExecutionRequest(
    string RunId,
    string RunActorId,
    int Attempt,
    NeedsLlmReplyEvent Request);
