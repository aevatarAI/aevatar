namespace Aevatar.GAgents.NyxidChat;

// Refactor (iter98/cluster-002):
//   Old pattern: actor id 由 CorrelationId 字符串拼出(ActorIdPrefix + correlationId)
//   New principle: run_id 是 actor id 的唯一构成;correlation_id 只用于 trace 传播
internal readonly record struct AgentRunId
{
    private const string Prefix = "agent-run-";

    public string Value { get; }

    private AgentRunId(string value)
    {
        Value = value;
    }

    public static AgentRunId Parse(string? value, string paramName = "runId")
    {
        var normalized = Normalize(value);
        if (normalized is null)
            throw new ArgumentException(
                "Deferred LLM reply request requires explicit run_id for AgentRunGAgent dispatch.",
                paramName);

        return new AgentRunId(normalized);
    }

    public static AgentRunId New() => new(Prefix + Guid.NewGuid().ToString("N"));

    public static bool TryParse(string? value, out AgentRunId runId)
    {
        var normalized = Normalize(value);
        if (normalized is null)
        {
            runId = default;
            return false;
        }

        runId = new AgentRunId(normalized);
        return true;
    }

    public override string ToString() => Value;

    private static string? Normalize(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }
}

internal static class AgentRunActorIds
{
    // Refactor (iter102/cluster-103): Old pattern: AgentRunActorIds reverse-parsed actorId prefix -> runId fallback. New principle: run_id is the typed authority on commands/state; actorId stays one-way opaque.
    public static string ForRun(AgentRunId runId) => "channel-agent-run:" + runId.Value;
}
