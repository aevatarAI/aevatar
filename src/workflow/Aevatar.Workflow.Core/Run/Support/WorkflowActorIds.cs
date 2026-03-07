namespace Aevatar.Workflow.Core;

internal static class WorkflowActorIds
{
    public static string BuildSubWorkflowRunActorId(
        string ownerActorId,
        string workflowName,
        string lifecycle,
        string invocationId)
    {
        var workflowSegment = SanitizeActorSegment(workflowName);
        var lifecycleSegment = SanitizeActorSegment(lifecycle);
        return $"{ownerActorId}:workflow:{workflowSegment}:{lifecycleSegment}:{invocationId}";
    }

    public static string BuildChildActorId(string ownerActorId, string roleId)
    {
        if (string.IsNullOrWhiteSpace(roleId))
            throw new InvalidOperationException("Role id is required to create child actor.");
        return $"{ownerActorId}:{roleId.Trim()}";
    }

    private static string SanitizeActorSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "default";

        var cleaned = new string(value
            .Trim()
            .Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-')
            .ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "default" : cleaned;
    }
}
