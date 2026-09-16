using Aevatar.Foundation.Abstractions.Runtime;

namespace Aevatar.Workflow.Projection.Orchestration;

internal sealed class WorkflowProjectionIncrementalGraphCapabilityAdvertisement
    : IRuntimeFleetCapabilityAdvertisement
{
    public RuntimeFleetMemberCapability GetCapability() =>
        new()
        {
            Capability = RuntimeFleetCapability.ProjectionIncrementalGraphV1,
            ReaderContractVersion =
                RuntimeFleetCapabilityContracts.ProjectionIncrementalGraphReaderVersion,
            ContractId = RuntimeFleetCapabilityContracts.ProjectionIncrementalGraphV1,
        };

    public Type GetReaderImplementationType() =>
        typeof(WorkflowExecutionMaterializationScopeGAgent);
}
