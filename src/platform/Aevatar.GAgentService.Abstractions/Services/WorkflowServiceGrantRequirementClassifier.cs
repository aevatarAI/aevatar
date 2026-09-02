using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.Workflow.Abstractions;

namespace Aevatar.GAgentService.Abstractions.Services;

public static class WorkflowServiceGrantRequirementClassifier
{
    public static AuthorizationGrantRequirement Classify(
        IEnumerable<ExternalWorkflowCapabilityRef> capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        var requiresServiceGrant = false;

        foreach (var capability in capabilities)
        {
            if (capability is null)
            {
                throw new InvalidOperationException(
                    "Workflow external capability cannot be classified for service grants.");
            }

            switch (capability.CapabilityCase)
            {
                case ExternalWorkflowCapabilityRef.CapabilityOneofCase.HostConnector:
                    break;
                case ExternalWorkflowCapabilityRef.CapabilityOneofCase.NyxIdUserService:
                case ExternalWorkflowCapabilityRef.CapabilityOneofCase.NyxIdUserRequest:
                    requiresServiceGrant = true;
                    break;
                default:
                    throw new InvalidOperationException(
                        "Workflow external capability cannot be classified for service grants.");
            }
        }

        return requiresServiceGrant
            ? AuthorizationGrantRequirement.Required
            : AuthorizationGrantRequirement.NotRequired;
    }
}
