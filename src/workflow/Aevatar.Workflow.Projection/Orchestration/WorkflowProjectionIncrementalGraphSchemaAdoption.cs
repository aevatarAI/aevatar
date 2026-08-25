using Aevatar.Foundation.Abstractions.Runtime;
using Aevatar.Foundation.Core.Runtime;

namespace Aevatar.Workflow.Projection.Orchestration;

internal static class WorkflowProjectionIncrementalGraphSchemaAdoption
{
    internal static bool IsGranted(IRuntimeActorStateSchemaContextReader? reader)
    {
        var context = reader?.Current;
        if (context == null ||
            context.StateSchemaVersion <
            WorkflowExecutionMaterializationScopeGAgent.IncrementalGraphStateSchemaVersion ||
            !string.Equals(
                context.AgentKind,
                WorkflowExecutionMaterializationScopeGAgent.AgentKind,
                StringComparison.Ordinal))
        {
            return false;
        }

        var receipts = context.AdoptionReceipts
            .Where(static receipt =>
                receipt.StateSchemaVersion ==
                WorkflowExecutionMaterializationScopeGAgent.IncrementalGraphStateSchemaVersion)
            .ToArray();
        if (receipts.Length != 1)
            return false;

        var receipt = receipts[0];
        return receipt.RequiredCapability == RuntimeFleetCapability.ProjectionIncrementalGraphV1 &&
               string.Equals(
                   receipt.RequiredContractId,
                   RuntimeFleetCapabilityContracts.ProjectionIncrementalGraphV1,
                   StringComparison.Ordinal) &&
               receipt.RequiredContractVersion ==
               RuntimeFleetCapabilityContracts.ProjectionIncrementalGraphReaderVersion &&
               receipt.CapabilityEpoch > 0 &&
               receipt.AuthorityStateVersion > 0 &&
               receipt.MembershipEpoch > 0 &&
               !string.IsNullOrWhiteSpace(receipt.DeploymentRevision) &&
               receipt.AdoptedAt != null &&
               string.Equals(
                   receipt.AuthorityActorId,
                   RuntimeFleetCapabilityAuthorityIdentity.ActorId,
                   StringComparison.Ordinal) &&
               !string.IsNullOrWhiteSpace(receipt.MembershipDigest);
    }
}
