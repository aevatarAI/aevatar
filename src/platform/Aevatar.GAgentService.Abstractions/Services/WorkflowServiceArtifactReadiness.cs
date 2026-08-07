using Aevatar.Workflow.Abstractions;

namespace Aevatar.GAgentService.Abstractions.Services;

public static class WorkflowServiceArtifactReadiness
{
    public static bool RequiresCapabilityAdmissionRebind(PreparedServiceRevisionArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (artifact.ImplementationKind != ServiceImplementationKind.Workflow)
            return false;

        if (artifact.DeploymentPlan?.PlanSpecCase != ServiceDeploymentPlan.PlanSpecOneofCase.WorkflowPlan)
            return true;

        var workflowPlan = artifact.DeploymentPlan.WorkflowPlan;
        if (workflowPlan == null ||
            workflowPlan.ExecutionMode == ExternalCapabilityExecutionMode.Unspecified ||
            !Enum.IsDefined(workflowPlan.ExecutionMode))
        {
            return true;
        }

        var admissionPlan = workflowPlan.CapabilityAdmissionPlan;
        if (admissionPlan == null)
            return true;

        return WorkflowCapabilityAdmissionPlanIntegrity.RequiresRebind(admissionPlan.SchemaVersion)
               || !string.Equals(
                   admissionPlan.SchemaVersion,
                   WorkflowCapabilityAdmissionPlanIntegrity.SchemaVersion,
                   StringComparison.Ordinal)
               || admissionPlan.ExecutionMode == ExternalCapabilityExecutionMode.Unspecified
               || !Enum.IsDefined(admissionPlan.ExecutionMode)
               || workflowPlan.ExecutionMode != admissionPlan.ExecutionMode;
    }
}
