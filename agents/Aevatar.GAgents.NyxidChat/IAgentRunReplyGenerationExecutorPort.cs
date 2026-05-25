using Aevatar.GAgents.Channel.Runtime;

namespace Aevatar.GAgents.NyxidChat;

public interface IAgentRunReplyGenerationExecutorPort
{
    Task StartAsync(AgentRunReplyGenerationExecutionRequest request, CancellationToken ct);

    Task<AgentRunReplyStepState> BuildInitialStepStateAsync(AgentRunReplyGenerationExecutionRequest request, CancellationToken ct);

    Task ExecuteLlmStepAsync(AgentRunReplyStepExecutionRequest request, CancellationToken ct);

    Task ExecuteToolStepAsync(AgentRunReplyStepExecutionRequest request, CancellationToken ct);
}

public sealed record AgentRunReplyGenerationExecutionRequest(
    string RunId,
    string RunActorId,
    int Attempt,
    NeedsLlmReplyEvent Request);

public sealed record AgentRunReplyStepExecutionRequest(
    string RunId,
    string RunActorId,
    int Attempt,
    int StepIndex,
    NeedsLlmReplyEvent Request,
    AgentRunReplyStepState StepState);
