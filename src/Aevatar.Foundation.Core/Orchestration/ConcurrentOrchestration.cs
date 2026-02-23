namespace Aevatar.Foundation.Core.Orchestration;

/// <summary>
/// Concurrent orchestration: broadcasts input to all agents in parallel,
/// collects all results, and merges them.
///     ┌→ Agent A
/// In ─┼→ Agent B ─→ Merge
///     └→ Agent C
/// </summary>
public sealed class ConcurrentOrchestration : IOrchestration<OrchestrationInput, OrchestrationOutput>
{
    private readonly Func<string, string, CancellationToken, Task<string>> _executeAgent;
    private readonly Func<IReadOnlyList<AgentResult>, string> _mergeResults;

    /// <param name="executeAgent">Delegate: (agentId, input, ct) → output</param>
    /// <param name="mergeResults">Optional merge strategy. Default: join with separator.</param>
    public ConcurrentOrchestration(
        Func<string, string, CancellationToken, Task<string>> executeAgent,
        Func<IReadOnlyList<AgentResult>, string>? mergeResults = null)
    {
        _executeAgent = executeAgent ?? throw new ArgumentNullException(nameof(executeAgent));
        _mergeResults = mergeResults ?? DefaultMerge;
    }

    public async Task<OrchestrationOutput> ExecuteAsync(OrchestrationInput input, CancellationToken ct = default)
    {
        if (input.AgentIds.Count == 0)
            return new OrchestrationOutput { Success = false, Error = "No agents specified" };

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
        var allSuccess = results.All(r => r.Success);
        var merged = _mergeResults(results);

        return new OrchestrationOutput
        {
            Success = allSuccess, Output = merged, AgentResults = results,
            Error = allSuccess ? null : "One or more agents failed",
        };
    }

    private static string DefaultMerge(IReadOnlyList<AgentResult> results) =>
        string.Join("\n---\n", results.Where(r => r.Success).Select(r => r.Output));
}
