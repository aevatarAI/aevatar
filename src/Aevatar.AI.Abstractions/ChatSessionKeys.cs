namespace Aevatar.AI.Abstractions;

/// <summary>
/// Canonical helpers for chat message identifiers used across workflow modules and projections.
/// </summary>
public static class ChatMessageKeys
{
    public static string CreateWorkflowStepMessageId(string runId, string stepId)
    {
        if (string.IsNullOrWhiteSpace(runId))
            throw new ArgumentException("RunId is required.", nameof(runId));
        if (string.IsNullOrWhiteSpace(stepId))
            throw new ArgumentException("StepId is required.", nameof(stepId));

        return $"{runId}:{stepId}";
    }

    public static bool TryParseWorkflowRunId(string? messageId, out string? runId)
    {
        runId = null;
        if (string.IsNullOrWhiteSpace(messageId))
            return false;

        var separatorIndex = messageId.IndexOf(':');
        if (separatorIndex <= 0)
            return false;

        runId = messageId[..separatorIndex];
        return !string.IsNullOrWhiteSpace(runId);
    }
}
