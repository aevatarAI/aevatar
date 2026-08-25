using Aevatar.Foundation.Abstractions.Runtime;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Workflow.Core.Execution;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Core;

/// <summary>
/// Adds digest-backed replay evidence before schema-v2 value tombstones can be
/// written. The migration never invents an author release or value identity.
/// </summary>
[ActorStateMigration(
    "workflow.run",
    RequiredCapability = RuntimeFleetCapability.WorkflowNormalizedStateWritesV1,
    RequiredContractId = WorkflowNormalizedStateWriteAdmission.ContractId,
    RequiredContractVersion = RuntimeFleetCapabilityContracts.WorkflowNormalizedStateReaderVersionV2)]
internal sealed class WorkflowRunStateV1ToV2Migration
    : IActorStateMigration<WorkflowRunState>
{
    public int FromStateVersion => 1;

    public int ToStateVersion => 2;

    public WorkflowRunState Apply(WorkflowRunState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var migrated = state.Clone();
        foreach (var (scopeKey, packedState) in migrated.ExecutionStates.ToArray())
        {
            if (packedState?.Is(WorkflowExecutionKernelState.Descriptor) != true)
                continue;

            var kernelState = packedState.Unpack<WorkflowExecutionKernelState>();
            WorkflowExecutionValueStore.MigrateToValueLifecycleV2(kernelState);
            migrated.ExecutionStates[scopeKey] = Any.Pack(kernelState);
        }

        return migrated;
    }
}
