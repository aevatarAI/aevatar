namespace Aevatar.Foundation.Core.Orchestration;

/// <summary>
/// Vote orchestration: broadcasts input to all agents in parallel,
/// then uses a voting strategy to select the best result.
/// </summary>
public sealed class VoteOrchestration : IOrchestration<OrchestrationInput, OrchestrationOutput>
{
    private readonly Func<string, string, CancellationToken, Task<string>> _executeAgent;
    private readonly Func<IReadOnlyList<AgentResult>, CancellationToken, Task<AgentResult>> _voteStrategy;

    /// <param name="executeAgent">Delegate: (agentId, input, ct) → output</param>
    /// <param name="voteStrategy">Voting delegate to select the best result.</param>
    public VoteOrchestration(
        Func<string, string, CancellationToken, Task<string>> executeAgent,
        Func<IReadOnlyList<AgentResult>, CancellationToken, Task<AgentResult>>? voteStrategy = null)
    {
        _executeAgent = executeAgent ?? throw new ArgumentNullException(nameof(executeAgent));
        _voteStrategy = voteStrategy ?? DefaultVoteAsync;
    }

    public async Task<OrchestrationOutput> ExecuteAsync(OrchestrationInput input, CancellationToken ct = default)
    {
        if (input.AgentIds.Count == 0)
            return new OrchestrationOutput { Success = false, Error = "No agents specified" };

        // Parallel execution
        var tasks = input.AgentIds.Select(async agentId =>
        {
            try
            {
                var output = await _executeAgent(agentId, input.Prompt, ct);
                return new AgentResult { AgentId = agentId, Success = true, Output = output };
            }
            catch (Exception ex)
            {
                return new AgentResult { AgentId = agentId, Success = false, Output = ex.Message };
            }
        });

        var results = (await Task.WhenAll(tasks)).ToList();
        var successful = results.Where(r => r.Success).ToList();

        if (successful.Count == 0)
            return new OrchestrationOutput
            {
                Success = false, AgentResults = results,
                Error = "All agents failed",
            };

        // Vote to select best
        var winner = await _voteStrategy(successful, ct);

        return new OrchestrationOutput
        {
            Success = true, Output = winner.Output, AgentResults = results,
        };
    }

    private static Task<AgentResult> DefaultVoteAsync(
        IReadOnlyList<AgentResult> results, CancellationToken ct)
    {
        var winner = results.OrderByDescending(r => r.Output.Length).First();
        return Task.FromResult(winner);
    }
}
