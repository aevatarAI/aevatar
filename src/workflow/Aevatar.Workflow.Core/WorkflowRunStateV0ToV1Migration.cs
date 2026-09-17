using Aevatar.Foundation.Abstractions.Runtime;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Workflow.Core.Execution;

namespace Aevatar.Workflow.Core;

/// <summary>
/// Establishes the v1 reader capability without manufacturing normalized
/// provenance for legacy runs. Proto unknown/default fields remain intact and
/// the legacy variables map stays authoritative.
/// </summary>
[ActorStateMigration(
    "workflow.run",
    RequiredCapability = RuntimeFleetCapability.WorkflowNormalizedStateWritesV1,
    RequiredContractId = WorkflowNormalizedStateWriteAdmission.ContractId,
    RequiredContractVersion = 1)]
internal sealed class WorkflowRunStateV0ToV1Migration
    : IActorStateMigration<WorkflowRunState>
{
    public int FromStateVersion => 0;

    public int ToStateVersion => 1;

    public WorkflowRunState Apply(WorkflowRunState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.Clone();
    }
}
