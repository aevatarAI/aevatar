namespace Aevatar.AI.Abstractions;

/// <summary>
/// Canonical helpers for chat session identifiers used across workflow modules and projections.
/// </summary>
public static class ChatSessionKeys
{
    public static string CreateWorkflowStepSessionId(string runId, string stepId)
    {
        if (string.IsNullOrWhiteSpace(runId))
            throw new ArgumentException("RunId is required.", nameof(runId));
        if (string.IsNullOrWhiteSpace(stepId))
            throw new ArgumentException("StepId is required.", nameof(stepId));

        return $"{runId}:{stepId}";
    }

    public static bool TryParseWorkflowRunId(string? sessionId, out string? runId)
    {
        runId = null;
        if (string.IsNullOrWhiteSpace(sessionId))
            return false;

        var separatorIndex = sessionId.IndexOf(':');
        if (separatorIndex <= 0)
            return false;

        runId = sessionId[..separatorIndex];
        return !string.IsNullOrWhiteSpace(runId);
    }
}
