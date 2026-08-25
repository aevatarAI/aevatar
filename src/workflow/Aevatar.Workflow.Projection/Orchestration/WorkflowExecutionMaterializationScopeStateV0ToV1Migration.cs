using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions.Runtime;
using Aevatar.Foundation.Abstractions.TypeSystem;

namespace Aevatar.Workflow.Projection.Orchestration;

[ActorStateMigration(
    WorkflowExecutionMaterializationScopeGAgent.AgentKind,
    RequiredCapability = RuntimeFleetCapability.ProjectionIncrementalGraphV1,
    RequiredContractId = RuntimeFleetCapabilityContracts.ProjectionIncrementalGraphV1,
    RequiredContractVersion = RuntimeFleetCapabilityContracts.ProjectionIncrementalGraphReaderVersion)]
internal sealed class WorkflowExecutionMaterializationScopeStateV0ToV1Migration
    : IActorStateMigration<ProjectionScopeState>
{
    public int FromStateVersion => 0;

    public int ToStateVersion =>
        WorkflowExecutionMaterializationScopeGAgent.IncrementalGraphStateSchemaVersion;

    public ProjectionScopeState Apply(ProjectionScopeState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.Clone();
    }
}
