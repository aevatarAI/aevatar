namespace Aevatar.Foundation.Core.Orchestration;

/// <summary>
/// Base interface for reusable multi-agent orchestration patterns.
/// Each pattern takes typed input and produces typed output.
/// Inspired by MAF's built-in orchestration patterns.
/// </summary>
/// <typeparam name="TInput">Orchestration input type.</typeparam>
/// <typeparam name="TOutput">Orchestration output type.</typeparam>
public interface IOrchestration<in TInput, TOutput>
{
    Task<TOutput> ExecuteAsync(TInput input, CancellationToken ct = default);
}

/// <summary>Standard input for agent orchestration: a prompt routed to one or more agents.</summary>
public sealed class OrchestrationInput
{
    /// <summary>User prompt or task description.</summary>
    public required string Prompt { get; init; }

    /// <summary>Optional context from previous steps.</summary>
    public string? Context { get; init; }

    /// <summary>Target agent IDs to orchestrate.</summary>
    public IReadOnlyList<string> AgentIds { get; init; } = [];

    /// <summary>Arbitrary parameters.</summary>
    public IDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>();
}

/// <summary>Standard output from agent orchestration.</summary>
public sealed class OrchestrationOutput
{
    /// <summary>Whether the orchestration succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>Merged/selected output.</summary>
    public string Output { get; init; } = "";

    /// <summary>Individual agent results (for multi-agent patterns).</summary>
    public IReadOnlyList<AgentResult> AgentResults { get; init; } = [];

    /// <summary>Error message if failed.</summary>
    public string? Error { get; init; }
}

/// <summary>Result from a single agent in an orchestration.</summary>
public sealed class AgentResult
{
    /// <summary>Agent identifier.</summary>
    public required string AgentId { get; init; }

    /// <summary>Whether this agent succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>Agent output.</summary>
    public string Output { get; init; } = "";
}
