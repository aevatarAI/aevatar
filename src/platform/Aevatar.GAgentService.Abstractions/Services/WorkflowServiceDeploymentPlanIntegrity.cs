using Aevatar.Workflow.Abstractions;

namespace Aevatar.GAgentService.Abstractions.Services;

public readonly record struct WorkflowServiceBindingIdentity(
    string WorkflowId,
    string RevisionId);

public static class WorkflowServiceDeploymentPlanIntegrity
{
    public static WorkflowServiceBindingIdentity RequireExplicitBindingIdentity(
        string? workflowId,
        string? revisionId)
    {
        ValidateIdentity(workflowId, "workflow_id");
        ValidateIdentity(revisionId, "revision_id");
        return new WorkflowServiceBindingIdentity(workflowId!, revisionId!);
    }

    public static WorkflowServiceBindingIdentity ResolveBindingIdentity(
        PreparedServiceRevisionArtifact artifact,
        string? resolvedRevisionId)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ValidateIdentity(resolvedRevisionId, "resolved revision_id");
        if (!string.Equals(artifact.RevisionId, resolvedRevisionId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Workflow service artifact revision_id does not match the resolved revision_id.");
        }

        var plan = artifact.DeploymentPlan?.WorkflowPlan
            ?? throw new InvalidOperationException("Workflow service deployment plan is required.");
        if (plan.ExecutionMode == ExternalCapabilityExecutionMode.Unspecified ||
            !Enum.IsDefined(plan.ExecutionMode) ||
            plan.CapabilityAdmissionPlan == null ||
            plan.ExecutionMode != plan.CapabilityAdmissionPlan.ExecutionMode)
        {
            throw new InvalidOperationException(
                "Workflow service deployment execution mode must match the capability admission plan.");
        }
        var requiresBindingIdentity = WorkflowCapabilityAdmissionPlanIntegrity
            .RequiresExplicitRequestBindingIdentity(plan.CapabilityAdmissionPlan);
        var hasWorkflowId = !string.IsNullOrWhiteSpace(plan.WorkflowId);
        var hasRevisionId = !string.IsNullOrWhiteSpace(plan.RevisionId);
        if (requiresBindingIdentity || hasWorkflowId || hasRevisionId)
        {
            var identity = RequireExplicitBindingIdentity(plan.WorkflowId, plan.RevisionId);
            if (!string.Equals(identity.RevisionId, resolvedRevisionId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Workflow service workflow plan revision_id does not match the resolved revision_id.");
            }

            return identity;
        }

        return new WorkflowServiceBindingIdentity(string.Empty, resolvedRevisionId!);
    }

    private static void ValidateIdentity(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Workflow service {fieldName} is required and must be canonical.");
        }
    }
}
