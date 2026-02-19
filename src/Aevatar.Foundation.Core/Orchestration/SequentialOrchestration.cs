namespace Aevatar.Foundation.Core.Orchestration;

/// <summary>
/// Sequential orchestration: passes output from one agent to the next in order.
/// Agent A → Agent B → Agent C
/// </summary>
public sealed class SequentialOrchestration : IOrchestration<OrchestrationInput, OrchestrationOutput>
{
    private readonly Func<string, string, CancellationToken, Task<string>> _executeAgent;

    /// <param name="executeAgent">Delegate: (agentId, input, ct) → output</param>
    public SequentialOrchestration(Func<string, string, CancellationToken, Task<string>> executeAgent)
    {
        _executeAgent = executeAgent ?? throw new ArgumentNullException(nameof(executeAgent));
    }

    public async Task<OrchestrationOutput> ExecuteAsync(OrchestrationInput input, CancellationToken ct = default)
    {
        if (input.AgentIds.Count == 0)
            return new OrchestrationOutput { Success = false, Error = "No agents specified" };

        var results = new List<AgentResult>();
        var currentInput = input.Prompt;

        foreach (var agentId in input.AgentIds)
        {
            try
            {
                var output = await _executeAgent(agentId, currentInput, ct);
                results.Add(new AgentResult { AgentId = agentId, Success = true, Output = output });
                currentInput = output;
            }
            catch (Exception ex)
            {
                results.Add(new AgentResult { AgentId = agentId, Success = false, Output = ex.Message });
                return new OrchestrationOutput
                {
                    Success = false, Output = currentInput,
                    AgentResults = results, Error = $"Agent '{agentId}' failed: {ex.Message}",
                };
            }
        }

        return new OrchestrationOutput
        {
            Success = true, Output = currentInput, AgentResults = results,
        };
    }
}
