using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core;

internal static class WorkflowRunGuards
{
    public static bool TryMatchRunAndStep(string activeRunId, string runId, string stepId) =>
        string.Equals(WorkflowRunIdNormalizer.Normalize(runId), activeRunId, StringComparison.Ordinal) &&
        !string.IsNullOrWhiteSpace(stepId);
}
