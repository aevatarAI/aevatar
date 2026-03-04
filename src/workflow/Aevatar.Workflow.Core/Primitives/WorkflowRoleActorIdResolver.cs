namespace Aevatar.Workflow.Core.Primitives;

public static class WorkflowRoleActorIdResolver
{
    public static string ResolveTargetActorId(
        string workflowActorId,
        string targetRole)
    {
        if (string.IsNullOrWhiteSpace(targetRole))
            return targetRole;

        return targetRole.Contains(':', StringComparison.Ordinal)
            ? targetRole
            : $"{workflowActorId}:{targetRole}";
    }
}
