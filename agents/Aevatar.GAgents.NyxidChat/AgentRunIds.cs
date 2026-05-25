namespace Aevatar.GAgents.NyxidChat;

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
        _ = paramName;
        var normalized = Normalize(value);
        if (normalized is null)
            throw new InvalidOperationException("Deferred LLM reply request requires explicit run_id for AgentRunGAgent dispatch.");

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
    private const string ActorIdPrefix = "channel-agent-run:";

    public static string ForRun(AgentRunId runId) => ActorIdPrefix + runId.Value;

    internal static bool TryGetRunId(string? actorId, out AgentRunId runId)
    {
        if (!string.IsNullOrWhiteSpace(actorId) &&
            actorId.StartsWith(ActorIdPrefix, StringComparison.Ordinal) &&
            AgentRunId.TryParse(actorId[ActorIdPrefix.Length..], out runId))
        {
            return true;
        }

        runId = default;
        return false;
    }
}
