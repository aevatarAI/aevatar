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
}
