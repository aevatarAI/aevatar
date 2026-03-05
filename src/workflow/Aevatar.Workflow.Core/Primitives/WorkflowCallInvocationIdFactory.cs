namespace Aevatar.Workflow.Core.Primitives;

/// <summary>
/// Central workflow_call invocation id generator shared by module and actor orchestration layers.
/// </summary>
internal static class WorkflowCallInvocationIdFactory
{
    public static string Build(string parentRunId, string parentStepId)
    {
        var normalizedRunId = WorkflowRunIdNormalizer.Normalize(parentRunId);
        if (string.IsNullOrWhiteSpace(parentStepId))
            throw new ArgumentException("workflow_call parentStepId is required.", nameof(parentStepId));

        var normalizedStepId = parentStepId.Trim();
        return $"{normalizedRunId}:workflow_call:{normalizedStepId}:{Guid.NewGuid():N}";
    }
}
