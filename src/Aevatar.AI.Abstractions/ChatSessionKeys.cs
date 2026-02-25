namespace Aevatar.AI.Abstractions;

/// <summary>
/// Canonical helpers for chat session identifiers used across workflow modules and projections.
/// </summary>
public static class ChatSessionKeys
{
    public static string CreateWorkflowStepSessionId(string scopeId, string stepId)
    {
        if (string.IsNullOrWhiteSpace(scopeId))
            throw new ArgumentException("ScopeId is required.", nameof(scopeId));
        if (string.IsNullOrWhiteSpace(stepId))
            throw new ArgumentException("StepId is required.", nameof(stepId));

        return $"{scopeId}:{stepId}";
    }

    public static string CreateWorkflowStepSessionId(string scopeId, string runId, string stepId, int attempt = 1)
    {
        if (string.IsNullOrWhiteSpace(scopeId))
            throw new ArgumentException("ScopeId is required.", nameof(scopeId));
        if (string.IsNullOrWhiteSpace(runId))
            throw new ArgumentException("RunId is required.", nameof(runId));
        if (string.IsNullOrWhiteSpace(stepId))
            throw new ArgumentException("StepId is required.", nameof(stepId));
        if (attempt < 1)
            throw new ArgumentOutOfRangeException(nameof(attempt), attempt, "Attempt must be >= 1.");

        return $"{scopeId}:{runId}:{stepId}:a{attempt}";
    }
}
