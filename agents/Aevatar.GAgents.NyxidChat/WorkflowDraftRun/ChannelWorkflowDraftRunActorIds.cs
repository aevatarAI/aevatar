namespace Aevatar.GAgents.NyxidChat.WorkflowDraftRun;

internal readonly record struct ChannelWorkflowDraftRunId
{
    private const string Prefix = "workflow-draft-run-";

    public string Value { get; }

    private ChannelWorkflowDraftRunId(string value)
    {
        Value = value;
    }

    public static ChannelWorkflowDraftRunId Parse(string? value, string paramName = "runId")
    {
        var normalized = Normalize(value);
        if (normalized is null)
            throw new ArgumentException(
                "Workflow draft-run request requires explicit run_id for actor dispatch.",
                paramName);

        return new ChannelWorkflowDraftRunId(normalized);
    }

    public static string NewValue() => Prefix + Guid.NewGuid().ToString("N");

    public static bool TryParse(string? value, out ChannelWorkflowDraftRunId runId)
    {
        var normalized = Normalize(value);
        if (normalized is null)
        {
            runId = default;
            return false;
        }

        runId = new ChannelWorkflowDraftRunId(normalized);
        return true;
    }

    public override string ToString() => Value;

    private static string? Normalize(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }
}

internal static class ChannelWorkflowDraftRunActorIds
{
    public static string ForRun(ChannelWorkflowDraftRunId runId) => "channel-workflow-draft-run:" + runId.Value;
}
