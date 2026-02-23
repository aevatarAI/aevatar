namespace Aevatar.Foundation.Core.Orchestration;

/// <summary>
/// Handoff orchestration: starts with one agent and dynamically transfers
/// control to another based on the output. Agents decide handoff targets.
/// Agent A ──handoff──→ Agent B ──handoff──→ Agent C
/// </summary>
public sealed class HandoffOrchestration : IOrchestration<OrchestrationInput, OrchestrationOutput>
{
    private readonly Func<string, string, CancellationToken, Task<string>> _executeAgent;
    private readonly Func<string, string, string?> _resolveHandoff;
    private readonly int _maxHandoffs;

    /// <param name="executeAgent">Delegate: (agentId, input, ct) → output</param>
    /// <param name="resolveHandoff">Delegate: (currentAgentId, output) → nextAgentId or null to stop</param>
    /// <param name="maxHandoffs">Maximum number of handoffs to prevent infinite loops.</param>
    public HandoffOrchestration(
        Func<string, string, CancellationToken, Task<string>> executeAgent,
        Func<string, string, string?> resolveHandoff,
        int maxHandoffs = 10)
    {
        _executeAgent = executeAgent ?? throw new ArgumentNullException(nameof(executeAgent));
        _resolveHandoff = resolveHandoff ?? throw new ArgumentNullException(nameof(resolveHandoff));
        _maxHandoffs = maxHandoffs;
    }

    public async Task<OrchestrationOutput> ExecuteAsync(OrchestrationInput input, CancellationToken ct = default)
    {
        if (input.AgentIds.Count == 0)
            return new OrchestrationOutput { Success = false, Error = "No agents specified" };

        var results = new List<AgentResult>();
        var currentAgentId = input.AgentIds[0];
        var currentInput = input.Prompt;

        for (var handoff = 0; handoff <= _maxHandoffs; handoff++)
        {
            try
            {
                var output = await _executeAgent(currentAgentId, currentInput, ct);
                results.Add(new AgentResult { AgentId = currentAgentId, Success = true, Output = output });

                var nextAgent = _resolveHandoff(currentAgentId, output);
                if (nextAgent == null)
                {
                    return new OrchestrationOutput
                    {
                        Success = true, Output = output, AgentResults = results,
                    };
                }

                currentAgentId = nextAgent;
                currentInput = output;
            }
            catch (Exception ex)
            {
                results.Add(new AgentResult { AgentId = currentAgentId, Success = false, Output = ex.Message });
                return new OrchestrationOutput
                {
                    Success = false, Output = currentInput,
                    AgentResults = results, Error = $"Agent '{currentAgentId}' failed: {ex.Message}",
                };
            }
        }

        return new OrchestrationOutput
        {
            Success = false,
            Output = results.Last().Output,
            AgentResults = results,
            Error = $"Max handoffs ({_maxHandoffs}) exceeded",
        };
    }
}
