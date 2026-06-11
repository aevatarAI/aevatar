using Aevatar.GAgents.Channel.Runtime;

namespace Aevatar.GAgents.NyxidChat;

public interface IAgentRunReplyGenerationExecutorPort
{
    Task<AgentRunReplyStepState> BuildInitialStepStateAsync(AgentRunReplyGenerationExecutionRequest request, CancellationToken ct);

    // Refactor (iter110/cluster-110-agent-run-executor-authoritative-step-state):
    //   Old pattern: AgentRunReplyGenerationExecutor performs LLM/tool IO and constructs the authoritative next AgentRunReplyStepState outside the run actor.
    //   New principle: Executor returns typed IO facts only; AgentRunGAgent applies deterministic step-state transition and persists state inside actor event handling.
    Task ExecuteLlmStepAsync(AgentRunReplyStepExecutionRequest request, CancellationToken ct);

    Task ExecuteToolStepAsync(AgentRunReplyStepExecutionRequest request, CancellationToken ct);

    Task<AgentRunNextLlmStepRequestedEvent> BuildLlmStepContinuationAsync(
        AgentRunReplyStepExecutionRequest request,
        CancellationToken ct);

    Task<AgentRunNextToolStepRequestedEvent> BuildToolStepContinuationAsync(
        AgentRunReplyStepExecutionRequest request,
        CancellationToken ct);
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
