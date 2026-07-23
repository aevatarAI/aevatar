using Aevatar.GAgents.Channel.Runtime;

namespace Aevatar.GAgents.NyxidChat;

public interface IAgentRunReplyGenerationExecutorPort
{
    Task<AgentRunReplyStepState> BuildInitialStepStateAsync(AgentRunReplyGenerationExecutionRequest request, CancellationToken ct);

    Task<AgentRunLlmStepExecution> BuildLlmStepExecutionAsync(
        AgentRunReplyStepExecutionRequest request,
        CancellationToken ct);

    Task<AgentRunNextToolStepRequestedEvent> BuildToolStepContinuationAsync(
        AgentRunReplyStepExecutionRequest request,
        AgentRunAuthorizedToolStep? authorizedToolStep,
        CancellationToken ct);
}

public sealed record AgentRunLlmStepExecution(
    AgentRunNextLlmStepRequestedEvent Continuation,
    AgentRunAuthorizedToolStep? AuthorizedToolStep);

public sealed class AgentRunAuthorizedToolStep
{
    private readonly AgentRunToolCall[] _toolCalls;
    private readonly Func<CancellationToken, Task<AgentRunToolStepResult>> _executeAsync;

    internal AgentRunAuthorizedToolStep(
        string runId,
        string correlationId,
        int attempt,
        int stepIndex,
        IReadOnlyList<AgentRunToolCall> toolCalls,
        Func<CancellationToken, Task<AgentRunToolStepResult>> executeAsync)
    {
        RunId = runId;
        CorrelationId = correlationId;
        Attempt = attempt;
        StepIndex = stepIndex;
        _toolCalls = toolCalls.Select(static call => call.Clone()).ToArray();
        _executeAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
    }

    internal string RunId { get; }

    internal string CorrelationId { get; }

    internal int Attempt { get; }

    internal int StepIndex { get; }

    internal bool Matches(AgentRunReplyStepExecutionRequest request)
    {
        if (!string.Equals(RunId, request.RunId, StringComparison.Ordinal) ||
            !string.Equals(CorrelationId, request.Request.CorrelationId, StringComparison.Ordinal) ||
            Attempt != request.Attempt ||
            StepIndex != request.StepIndex ||
            _toolCalls.Length != request.StepState.PendingToolCalls.Count)
        {
            return false;
        }

        return _toolCalls.Zip(request.StepState.PendingToolCalls).All(static pair =>
            string.Equals(pair.First.Id, pair.Second.Id, StringComparison.Ordinal) &&
            string.Equals(pair.First.Name, pair.Second.Name, StringComparison.Ordinal) &&
            string.Equals(pair.First.ArgumentsJson, pair.Second.ArgumentsJson, StringComparison.Ordinal));
    }

    internal Task<AgentRunToolStepResult> ExecuteAsync(CancellationToken ct) => _executeAsync(ct);
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
